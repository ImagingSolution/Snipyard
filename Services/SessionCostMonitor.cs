using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Snipyard.Services;

/// <summary>What the session has cost so far and what continuing it will cost.</summary>
public sealed class TurnCost
{
    /// <summary>Billed cost of the most recent main-thread turn.</summary>
    public double LastTurnUsd { get; set; }

    /// <summary>Everything billed to this session, subagent turns included.</summary>
    public double SessionUsd { get; set; }

    /// <summary>Size of the conversation prefix as of the latest main-thread turn.</summary>
    public long ContextTokens { get; set; }

    /// <summary>Cost of re-reading that prefix once more, before any new output.</summary>
    public double NextTurnUsd { get; set; }

    /// <summary>Main-thread turns seen in this session.</summary>
    public long Turns { get; set; }

    public string Model { get; set; } = "";

    /// <summary>
    /// Reasoning effort the latest main-thread reply ran at, as the CLI records it on the
    /// assistant line ("effort"). Null until a reply carries one; older CLIs never write it.
    /// </summary>
    public string? Effort { get; set; }

    /// <summary>Timestamp on the first record of the transcript.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>Timestamp on the most recent record.</summary>
    public DateTimeOffset? LastActivityAt { get; set; }

    /// <summary>
    /// Wall-clock span the transcript covers. This is not the CLI's own duration figure, which
    /// accumulates time actually spent working; this one is simply first record to last.
    /// </summary>
    public TimeSpan? Elapsed =>
        StartedAt is { } start && LastActivityAt is { } last && last > start ? last - start : null;

    public bool HasData => Turns > 0;
}

/// <summary>
/// Tracks the marginal cost of the session attached to one terminal.
///
/// The cost dashboard reports totals after the fact; this reports the number that changes a
/// decision in the moment. Every turn re-reads the whole conversation prefix at the cache-read
/// rate, so a long session keeps paying for its own history: at 250k of context each further
/// turn costs roughly ten times what it did at 25k. Surfacing that is what makes compacting
/// or handing off to a fresh session an obvious choice rather than a guess.
///
/// Reads are incremental - only lines appended since the last call are parsed.
/// </summary>
public sealed class SessionCostMonitor
{
    private string? _path;
    private long _lastOffset;
    private TurnCost _state = new();

    public TurnCost Current => _state;

    /// <summary>The transcript currently being watched, or null when nothing is attached.</summary>
    public string? Path => _path;

    /// <summary>
    /// Point the monitor at a session transcript, discarding any previous state. The next
    /// refresh reads the file from the top so an already-running session reports a true total.
    /// </summary>
    public void Track(string? jsonlPath)
    {
        if (string.Equals(_path, jsonlPath, StringComparison.OrdinalIgnoreCase)) return;
        _path = jsonlPath;
        _lastOffset = 0;
        _state = new TurnCost();
    }

    /// <summary>
    /// Parse whatever has been appended since the last call. Returns true when the readout
    /// changed. Runs off the calling thread; safe to await from the UI.
    /// </summary>
    public Task<bool> RefreshAsync() => Task.Run(Refresh);

    private bool Refresh()
    {
        var path = _path;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

        bool changed = false;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            // The transcript is append-only, so resume from where the last read stopped rather
            // than re-scanning the whole file. At a 700ms poll on a multi-megabyte transcript,
            // re-reading from the top would cost more than the readout is worth.
            if (stream.Length < _lastOffset) _lastOffset = 0; // truncated or replaced
            if (stream.Length == _lastOffset) return false;

            stream.Seek(_lastOffset, SeekOrigin.Begin);

            int pending = (int)Math.Min(int.MaxValue, stream.Length - _lastOffset);
            var buffer = new byte[pending];
            int read = stream.Read(buffer, 0, pending);
            if (read <= 0) return false;

            // Stop at the last complete line: the tail may be a record still being written,
            // and consuming it half-formed would lose that turn permanently.
            int lastNewline = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
            if (lastNewline < 0) return false;

            var text = Encoding.UTF8.GetString(buffer, 0, lastNewline + 1);
            _lastOffset += lastNewline + 1;

            foreach (var line in text.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    if (ProcessLine(line)) changed = true;
                }
                catch
                {
                    // One malformed line must not stop the rest of the batch.
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SessionCostMonitor] Failed to read {path}: {ex.Message}");
            return false;
        }

        return changed;
    }

    private bool ProcessLine(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return false;

        // Every record carries a timestamp, user turns included, so the session starts at the
        // first line of the file rather than at the first reply.
        bool changed = ReadTimestamp(root);

        if (!root.TryGetProperty("type", out var typeProp)) return changed;
        var type = typeProp.GetString();

        // A compaction rewrites the conversation prefix without a reply of its own, so nothing
        // in the usage records says it happened. The boundary the CLI writes carries the new
        // prefix size, and taking it here is what keeps the meter from reporting the pre-compact
        // figure until the user happens to send another message.
        if (type == "system") return ReadCompactBoundary(root) || changed;

        if (type != "assistant") return changed;
        if (!root.TryGetProperty("message", out var msgProp) || msgProp.ValueKind != JsonValueKind.Object) return changed;
        if (!msgProp.TryGetProperty("usage", out var usageProp) || usageProp.ValueKind != JsonValueKind.Object) return changed;

        long input = ReadLong(usageProp, "input_tokens");
        long output = ReadLong(usageProp, "output_tokens");
        long cacheRead = ReadLong(usageProp, "cache_read_input_tokens");
        long cacheCreation = ReadLong(usageProp, "cache_creation_input_tokens");
        if (input == 0 && output == 0 && cacheRead == 0 && cacheCreation == 0) return changed;

        string model = msgProp.TryGetProperty("model", out var modelProp) && modelProp.ValueKind == JsonValueKind.String
            ? modelProp.GetString() ?? "unknown"
            : "unknown";

        double cost = CostAnalytics.EstimateCostUsd(model, input, output, cacheRead, cacheCreation);

        // Subagent turns are billed to the session, so they count toward the total. They run on
        // their own much smaller context though, so letting one set the context readout would
        // make it collapse and rebound between turns.
        bool isSidechain = root.TryGetProperty("isSidechain", out var sideProp)
                           && sideProp.ValueKind == JsonValueKind.True;

        _state.SessionUsd += cost;

        if (isSidechain) return true;

        _state.Turns++;
        _state.Model = model;
        if (root.TryGetProperty("effort", out var effortProp) && effortProp.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(effortProp.GetString()))
            _state.Effort = effortProp.GetString()!.ToLowerInvariant();
        _state.LastTurnUsd = cost;
        _state.ContextTokens = input + cacheRead + cacheCreation;
        _state.NextTurnUsd = CostAnalytics.EstimateNextTurnCostUsd(model, _state.ContextTokens);
        return true;
    }

    /// <summary>
    /// Takes the post-compaction prefix size from a "compact_boundary" record, which the CLI
    /// writes for both a typed /compact and an automatic one. Returns false for every other
    /// system record.
    /// </summary>
    private bool ReadCompactBoundary(JsonElement root)
    {
        if (!TryReadCompactBoundaryTokens(root, out long post)) return false;

        _state.ContextTokens = post;
        _state.NextTurnUsd = CostAnalytics.EstimateNextTurnCostUsd(_state.Model, post);
        return true;
    }

    /// <summary>
    /// The prefix size a "compact_boundary" system record reports, or false for any other record.
    /// </summary>
    public static bool TryReadCompactBoundaryTokens(JsonElement root, out long tokens)
    {
        tokens = 0;
        if (!root.TryGetProperty("subtype", out var subProp)
            || subProp.ValueKind != JsonValueKind.String
            || subProp.GetString() != "compact_boundary") return false;

        if (!root.TryGetProperty("compactMetadata", out var meta)
            || meta.ValueKind != JsonValueKind.Object) return false;

        tokens = ReadLong(meta, "postTokens");
        return tokens > 0;
    }

    /// <summary>
    /// The conversation prefix a main-thread assistant record was answered against - the same
    /// input + cache-read + cache-creation sum the live meter uses - so a session list can quote
    /// the figure a resume would start from without re-implementing the transcript format.
    /// Returns false for sidechain turns and for records without usage.
    /// </summary>
    public static bool TryReadPrefixTokens(JsonElement root, out long tokens, out string? model)
    {
        tokens = 0;
        model = null;
        if (!root.TryGetProperty("message", out var msgProp) || msgProp.ValueKind != JsonValueKind.Object) return false;
        if (!msgProp.TryGetProperty("usage", out var usageProp) || usageProp.ValueKind != JsonValueKind.Object) return false;
        if (root.TryGetProperty("isSidechain", out var sideProp) && sideProp.ValueKind == JsonValueKind.True) return false;

        tokens = ReadLong(usageProp, "input_tokens")
                 + ReadLong(usageProp, "cache_read_input_tokens")
                 + ReadLong(usageProp, "cache_creation_input_tokens");
        if (tokens <= 0) return false;

        if (msgProp.TryGetProperty("model", out var modelProp) && modelProp.ValueKind == JsonValueKind.String)
            model = modelProp.GetString();
        return true;
    }

    private bool ReadTimestamp(JsonElement root)
    {
        if (!root.TryGetProperty("timestamp", out var prop) || prop.ValueKind != JsonValueKind.String)
            return false;
        if (!DateTimeOffset.TryParse(prop.GetString(), out var at)) return false;

        _state.StartedAt ??= at;
        if (_state.LastActivityAt is { } last && last >= at) return false;
        _state.LastActivityAt = at;
        return true;
    }

    /// <summary>The window the CLI assumes for a model it does not recognise.</summary>
    private const long DefaultContextWindowTokens = 200_000;

    /// <summary>
    /// Model lines that answer in a million tokens. Everything else - Haiku, and anything this
    /// build has never heard of - takes <see cref="DefaultContextWindowTokens"/>, which is the
    /// fallback the CLI itself uses.
    /// </summary>
    private static readonly string[] MillionTokenModels =
    {
        "fable-5", "mythos-5", "opus-5", "opus-4-8", "opus-4-7", "opus-4-6", "sonnet-5", "sonnet-4-6",
    };

    /// <summary>The context window a model answers in, in tokens.</summary>
    public static long ContextWindowTokens(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return DefaultContextWindowTokens;
        foreach (var line in MillionTokenModels)
        {
            if (modelId.Contains(line, StringComparison.OrdinalIgnoreCase)) return 1_000_000;
        }
        return DefaultContextWindowTokens;
    }

    /// <summary>
    /// How much of the context window the conversation prefix fills, as a percentage.
    ///
    /// This is deliberately the CLI's own arithmetic, down to the rounding: it hands its status
    /// line round(tokens / window * 100) clamped to 0-100, over the model's full window with no
    /// allowance for the reserve auto-compact keeps back. Anything cleverer here would put a
    /// different number on the status bar than the one sitting on the status line above it.
    /// </summary>
    public static int ContextUsedPercent(long contextTokens, string? modelId)
    {
        long window = Math.Max(1, ContextWindowTokens(modelId));
        return (int)Math.Clamp(Math.Round(contextTokens / (double)window * 100), 0, 100);
    }

    /// <summary>
    /// The name a model goes by, from the id the transcript records ("claude-opus-5-5" →
    /// "Opus 5.5"). Worked out from the id itself rather than a table, so a model released later
    /// is named right without a new build; an id in some other shape passes through unchanged.
    /// </summary>
    public static string ModelDisplayName(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId) || modelId == "unknown") return "";

        var id = modelId.Trim();
        return ModelCatalog.DisplayName(id) ?? id;
    }

    private static long ReadLong(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var prop)) return 0;
        return prop.ValueKind switch
        {
            JsonValueKind.Number => prop.TryGetInt64(out var v) ? v : (long)prop.GetDouble(),
            _ => 0,
        };
    }
}

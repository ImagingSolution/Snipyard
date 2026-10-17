using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Snipyard.Services;

/// <summary>One artifact the CLI published from a project's sessions, at its latest version.</summary>
public sealed class ArtifactEntry
{
    public required string Id { get; init; }
    public required string Url { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>The local HTML file the last publish sent. Often in a session scratchpad, so it may be gone.</summary>
    public string? LocalPath { get; set; }

    public DateTime Updated { get; set; }
    public int Publishes { get; set; }
}

/// <summary>
/// The artifacts a project has published, read out of its session transcripts. The Artifact tool's
/// result line carries a <c>toolUseResult</c> with the url, id, title and local path, and the
/// matching <c>tool_use</c> carries the description - nothing else on disk lists them.
///
/// Transcripts run to hundreds of megabytes, so each file's findings are kept against its size and
/// write time; a transcript is only appended to, and a refresh rereads just the ones that grew.
/// </summary>
public static class ArtifactCatalog
{
    private sealed record Scan(long Length, DateTime WriteTime, List<Publish> Publishes);

    private sealed record Publish(string Id, string Url, string? Title, string? Description, string? Path, DateTime When);

    private static readonly Dictionary<string, Scan> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Newest first. Safe to call off the UI thread.</summary>
    public static List<ArtifactEntry> Load(string projectFolder)
    {
        var dir = ClaudeProjectPaths.ProjectDir(projectFolder);
        var publishes = new List<Publish>();
        try
        {
            if (Directory.Exists(dir))
                foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl"))
                    publishes.AddRange(ScanFile(file));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var byId = new Dictionary<string, ArtifactEntry>();
        foreach (var p in publishes.OrderBy(p => p.When))
        {
            if (!byId.TryGetValue(p.Id, out var entry))
                byId[p.Id] = entry = new ArtifactEntry { Id = p.Id, Url = p.Url };
            entry.Url = p.Url;
            if (!string.IsNullOrWhiteSpace(p.Title)) entry.Title = p.Title;
            if (!string.IsNullOrWhiteSpace(p.Description)) entry.Description = p.Description;
            if (!string.IsNullOrWhiteSpace(p.Path)) entry.LocalPath = p.Path;
            entry.Updated = p.When;
            entry.Publishes++;
        }

        foreach (var entry in byId.Values.Where(e => e.Title.Length == 0))
            entry.Title = entry.LocalPath != null ? Path.GetFileNameWithoutExtension(entry.LocalPath) : entry.Url;

        return byId.Values.OrderByDescending(e => e.Updated).ToList();
    }

    private static List<Publish> ScanFile(string file)
    {
        FileInfo info;
        try { info = new FileInfo(file); }
        catch { return new(); }

        lock (Cache)
        {
            if (Cache.TryGetValue(file, out var hit) && hit.Length == info.Length && hit.WriteTime == info.LastWriteTimeUtc)
                return hit.Publishes;
        }

        var found = new List<Publish>();
        var descriptions = new Dictionary<string, string?>();
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                // Cheap substring tests first: almost no line is about an artifact, and parsing
                // every one of them is what would make this slow
                if (line.Contains("\"name\":\"Artifact\"", StringComparison.Ordinal))
                    ReadToolUse(line, descriptions);
                else if (line.Contains("\"artifact_id\"", StringComparison.Ordinal) && line.Contains("\"toolUseResult\"", StringComparison.Ordinal))
                    ReadResult(line, descriptions, found);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        lock (Cache) Cache[file] = new Scan(info.Length, info.LastWriteTimeUtc, found);
        return found;
    }

    /// <summary>
    /// Remembers which Artifact calls were publishes, and their descriptions. A read of an artifact
    /// also comes back with its url, and listing it as "published here" would be wrong.
    /// </summary>
    private static void ReadToolUse(string line, Dictionary<string, string?> descriptions)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("message", out var msg) ||
                !msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                return;

            foreach (var block in content.EnumerateArray())
            {
                if (Str(block, "type") != "tool_use" || Str(block, "name") != "Artifact") continue;
                if (Str(block, "id") is not { } id || !block.TryGetProperty("input", out var input)) continue;

                var action = Str(input, "action");
                if (action != null && action != "publish") continue;
                if (input.TryGetProperty("asset", out var asset) && asset.ValueKind == JsonValueKind.True) continue;
                descriptions[id] = Str(input, "description");
            }
        }
        catch (JsonException) { }
    }

    private static void ReadResult(string line, Dictionary<string, string?> descriptions, List<Publish> found)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("toolUseResult", out var result) || result.ValueKind != JsonValueKind.Object) return;
            if (Str(result, "artifact_id") is not { } id || Str(result, "url") is not { } url) return;

            string? toolUseId = null;
            if (root.TryGetProperty("message", out var msg) &&
                msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                foreach (var block in content.EnumerateArray())
                    toolUseId ??= Str(block, "tool_use_id");

            if (toolUseId == null || !descriptions.TryGetValue(toolUseId, out var description)) return;

            var when = DateTime.TryParse(Str(root, "timestamp"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
                ? t.ToLocalTime()
                : DateTime.MinValue;
            found.Add(new Publish(id, url, Str(result, "title"), description, Str(result, "path"), when));
        }
        catch (JsonException) { }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}

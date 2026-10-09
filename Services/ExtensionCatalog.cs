using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Snipyard.Services;

public enum ExtensionKind { Mcp, Skill, Plugin }

/// <summary>
/// One MCP server, skill or plugin, as the extensions panel shows it.
///
/// <see cref="Id"/> is what a toggle writes back, and it is only meaningful when
/// <see cref="CanToggle"/> is true. Everything Snipyard can see but not switch -
/// a server a plugin brings with it, a skill file on disk - is reported with
/// CanToggle false and a <see cref="Source"/> that says who owns it, so the panel
/// can point at the row that does own the switch instead of pretending it has one.
/// </summary>
public sealed record ExtensionItem
{
    public ExtensionKind Kind { get; init; }
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    /// <summary>Human-readable origin: "project", "user", or a plugin name.</summary>
    public string Source { get; init; } = "";
    /// <summary>File to open on click, when there is one.</summary>
    public string? Path { get; init; }
    public bool Enabled { get; init; }
    public bool CanToggle { get; init; }
    public string Id { get; init; } = "";
    /// <summary>Short right-hand annotation - what a plugin contributes, or a server's transport.</summary>
    public string? Detail { get; init; }
    /// <summary>
    /// Rough tokens this adds to every session before the first prompt, or 0 when it cannot be
    /// told from the files - an MCP server's tool list only exists once it is running.
    /// </summary>
    public long Tokens { get; init; }
    /// <summary>
    /// A project or personal skill's skillOverrides value ("on", "name-only",
    /// "user-invocable-only", "off"). Null for plugin skills, which the setting does not reach.
    /// </summary>
    public string? SkillMode { get; init; }
}

/// <summary>A file Claude Code reads in full at the start of every session.</summary>
public sealed record ResidentFile(string Label, string Path, int Lines, long Tokens);

public sealed record ExtensionSnapshot
{
    public IReadOnlyList<ExtensionItem> Mcp { get; init; } = Array.Empty<ExtensionItem>();
    public IReadOnlyList<ExtensionItem> Skills { get; init; } = Array.Empty<ExtensionItem>();
    public IReadOnlyList<ExtensionItem> Plugins { get; init; } = Array.Empty<ExtensionItem>();
    public IReadOnlyList<ResidentFile> Resident { get; init; } = Array.Empty<ResidentFile>();
    /// <summary>
    /// The smallest first-turn prefix among the project's recent sessions - what the CLI
    /// actually sent before any conversation existed. Null when there is no transcript yet.
    /// </summary>
    public long? MeasuredBaseTokens { get; init; }
}

/// <summary>
/// Reads the MCP servers, skills and plugins a Claude Code session would load, and
/// flips the two switches that actually exist for them.
///
/// Two files hold enable state and neither is ours, so both writes go through
/// <see cref="Rewrite"/>: back up, edit the parsed tree so unknown keys survive, then
/// replace atomically. <c>~/.claude.json</c> is deliberately never written - Claude Code
/// rewrites it while sessions run, and a read-modify-write from here would lose whatever
/// it recorded in between. Servers that live only there are listed read-only.
/// </summary>
public static class ExtensionCatalog
{
    private const int MaxDescription = 220;

    public static string UserClaudeDir =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    private static string UserSettingsPath => System.IO.Path.Combine(UserClaudeDir, "settings.json");
    private static string InstalledPluginsPath =>
        System.IO.Path.Combine(UserClaudeDir, "plugins", "installed_plugins.json");
    private static string UserConfigPath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

    public static string ProjectSettingsPath(string projectFolder) =>
        System.IO.Path.Combine(projectFolder, ".claude", "settings.local.json");

    public static Task<ExtensionSnapshot> LoadAsync(string? projectFolder)
        => Task.Run(() => Load(projectFolder));

    private static ExtensionSnapshot Load(string? projectFolder)
    {
        var plugins = ReadPlugins();
        return new ExtensionSnapshot
        {
            Mcp = ReadMcp(projectFolder, plugins),
            Skills = ReadSkills(projectFolder, plugins),
            Plugins = plugins.Select(p => p.Item).ToList(),
            Resident = ReadResidentFiles(projectFolder),
            MeasuredBaseTokens = string.IsNullOrEmpty(projectFolder) ? null : MeasureBaseTokens(projectFolder),
        };
    }

    // ── Always-loaded files ──

    /// <summary>
    /// CLAUDE.md at both levels, the rules that apply everywhere, and the memory index - the
    /// text every session carries in full before anything is asked. A rule with "paths:" in
    /// its front matter only loads beside matching files, so it is left out.
    /// </summary>
    private static List<ResidentFile> ReadResidentFiles(string? projectFolder)
    {
        var files = new List<ResidentFile>();
        Add("~/.claude/CLAUDE.md", System.IO.Path.Combine(UserClaudeDir, "CLAUDE.md"));
        AddRules("~/.claude/rules/", System.IO.Path.Combine(UserClaudeDir, "rules"));

        if (!string.IsNullOrEmpty(projectFolder))
        {
            Add("CLAUDE.md", System.IO.Path.Combine(projectFolder, "CLAUDE.md"));
            Add(".claude/CLAUDE.md", System.IO.Path.Combine(projectFolder, ".claude", "CLAUDE.md"));
            Add("CLAUDE.local.md", System.IO.Path.Combine(projectFolder, "CLAUDE.local.md"));
            AddRules(".claude/rules/", System.IO.Path.Combine(projectFolder, ".claude", "rules"));
            Add("memory/MEMORY.md",
                System.IO.Path.Combine(ClaudeProjectPaths.MemoryDir(projectFolder), "MEMORY.md"));
        }
        return files;

        void Add(string label, string path)
        {
            string text;
            try
            {
                if (!File.Exists(path)) return;
                text = File.ReadAllText(path);
            }
            catch { return; }
            int lines = text.Length == 0 ? 0 : text.Count(c => c == '\n') + (text.EndsWith('\n') ? 0 : 1);
            files.Add(new ResidentFile(label, path, lines, TokenEstimate.Of(text)));
        }

        void AddRules(string prefix, string dir)
        {
            if (!Directory.Exists(dir)) return;
            List<string> rules;
            try { rules = Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories).ToList(); }
            catch { return; }
            rules.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in rules)
            {
                if (FrontMatterLines(rule).Any(l => l.StartsWith("paths:", StringComparison.Ordinal))) continue;
                Add(prefix + System.IO.Path.GetRelativePath(dir, rule).Replace('\\', '/'), rule);
            }
        }
    }

    /// <summary>How many recent transcripts to look at for the session-start figure.</summary>
    private const int BaseSampleSessions = 8;

    /// <summary>
    /// The first assistant turn of a fresh session was answered against the system prompt,
    /// tools, skills, CLAUDE.md and memory plus one short prompt - the fixed cost itself. A
    /// resumed session's first turn carries its whole history, so the smallest across a few
    /// recent sessions is the honest figure.
    /// </summary>
    private static long? MeasureBaseTokens(string projectFolder)
    {
        List<FileInfo> sessions;
        try
        {
            var dir = new DirectoryInfo(ClaudeProjectPaths.ProjectDir(projectFolder));
            if (!dir.Exists) return null;
            sessions = dir.EnumerateFiles("*.jsonl")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(BaseSampleSessions)
                .ToList();
        }
        catch { return null; }

        long? best = null;
        foreach (var file in sessions)
        {
            if (FirstPrefixTokens(file.FullName) is long tokens && (best == null || tokens < best))
                best = tokens;
        }
        return best;
    }

    private static long? FirstPrefixTokens(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            for (int i = 0; i < 400 && reader.ReadLine() is { } line; i++)
            {
                if (!line.Contains("\"usage\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (SessionCostMonitor.TryReadPrefixTokens(doc.RootElement, out long tokens, out _))
                        return tokens;
                }
                catch (JsonException) { }
            }
        }
        catch { }
        return null;
    }

    // ── Plugins ──

    private sealed record PluginInfo(
        string Id, string Name, string InstallPath, bool Enabled,
        List<string> SkillFiles, ExtensionItem Item);

    private static List<PluginInfo> ReadPlugins()
    {
        var result = new List<PluginInfo>();
        var installed = ReadJson(InstalledPluginsPath)?["plugins"]?.AsObject();
        if (installed == null) return result;

        var enabledMap = ReadJson(UserSettingsPath)?["enabledPlugins"]?.AsObject();
        var declared = ReadDeclaredSkills();

        foreach (var entry in installed)
        {
            var id = entry.Key;
            var installPath = FirstInstallPath(entry.Value);
            if (installPath == null) continue;

            // enabledPlugins is a loose bag - the CLI keeps unrelated feature flags in it too.
            // Only a key naming an installed plugin means anything here.
            bool enabled = enabledMap?[id]?.GetValue<bool>() ?? false;

            var manifest = ReadJson(System.IO.Path.Combine(installPath, ".claude-plugin", "plugin.json"));
            var name = manifest?["name"]?.GetValue<string>() ?? id.Split('@')[0];
            var description = Trim(manifest?["description"]?.GetValue<string>());
            var version = manifest?["version"]?.GetValue<string>();

            var skills = SkillFiles(installPath, declared.GetValueOrDefault(id));
            long tokens = skills.Sum(HeaderTokens)
                          + MarkdownFiles(System.IO.Path.Combine(installPath, "agents")).Sum(HeaderTokens)
                          + MarkdownFiles(System.IO.Path.Combine(installPath, "commands")).Sum(HeaderTokens);

            result.Add(new PluginInfo(id, name, installPath, enabled, skills, new ExtensionItem
            {
                Kind = ExtensionKind.Plugin,
                Name = name,
                Description = description,
                Source = id.Contains('@') ? id[(id.IndexOf('@') + 1)..] : "",
                Path = installPath,
                Enabled = enabled,
                CanToggle = true,
                Id = id,
                Detail = DescribeContents(installPath, version, skills.Count),
                Tokens = tokens,
            }));
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static string? FirstInstallPath(JsonNode? value)
    {
        // The file stores a list per plugin id, one entry per scope.
        var path = value is JsonArray array
            ? array.FirstOrDefault()?["installPath"]?.GetValue<string>()
            : value?["installPath"]?.GetValue<string>();
        return path != null && Directory.Exists(path) ? path : null;
    }

    /// <summary>
    /// Plugin id -&gt; the skill folders its marketplace entry names, for the marketplaces that
    /// name any. A plugin absent from this map contributes everything under its own skills
    /// folder, which is the usual arrangement.
    /// </summary>
    private static Dictionary<string, List<string>> ReadDeclaredSkills()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var known = ReadJson(
            System.IO.Path.Combine(UserClaudeDir, "plugins", "known_marketplaces.json"))?.AsObject();
        if (known == null) return map;

        foreach (var market in known)
        {
            var location = market.Value?["installLocation"]?.GetValue<string>();
            if (location == null) continue;

            var plugins = ReadJson(
                System.IO.Path.Combine(location, ".claude-plugin", "marketplace.json"))?["plugins"] as JsonArray;
            if (plugins == null) continue;

            foreach (var plugin in plugins)
            {
                var name = plugin?["name"]?.GetValue<string>();
                if (name == null || plugin?["skills"] is not JsonArray skills) continue;

                var paths = new List<string>();
                foreach (var skill in skills)
                    if (skill?.GetValue<string>() is { } relative) paths.Add(relative);
                map[name + "@" + market.Key] = paths;
            }
        }
        return map;
    }

    /// <summary>
    /// The SKILL.md files a plugin actually contributes, in order.
    ///
    /// Usually that is every skill under its install path. But several plugins can share one
    /// repository - anthropic-agent-skills ships five that way - and then only the folders its
    /// marketplace entry names are its own. Globbing there would credit every one of them with
    /// the whole repository.
    /// </summary>
    private static List<string> SkillFiles(string installPath, List<string>? declared)
    {
        if (declared == null)
            return SkillFilesUnder(System.IO.Path.Combine(installPath, "skills"));

        var named = new List<string>();
        foreach (var relative in declared)
        {
            string file;
            try
            {
                file = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(installPath, relative, "SKILL.md"));
            }
            catch { continue; }
            if (File.Exists(file)) named.Add(file);
        }
        return named;
    }

    private static List<string> SkillFilesUnder(string dir)
    {
        if (!Directory.Exists(dir)) return new List<string>();
        try
        {
            var files = Directory.EnumerateFiles(dir, "SKILL.md", SearchOption.AllDirectories).ToList();
            files.Sort(StringComparer.OrdinalIgnoreCase);
            return files;
        }
        catch { return new List<string>(); }
    }

    /// <summary>"v6.3.0 - 14 skills - 1 MCP" - what loading this plugin actually costs.</summary>
    private static string DescribeContents(string installPath, string? version, int skills)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(version) && version.Length <= 12) parts.Add("v" + version);

        if (skills > 0) parts.Add(string.Format(Loc.Get("NSkillsFmt"), skills));

        int commands = CountFiles(System.IO.Path.Combine(installPath, "commands"), "*.md");
        if (commands > 0) parts.Add(string.Format(Loc.Get("NCommandsFmt"), commands));

        int agents = CountFiles(System.IO.Path.Combine(installPath, "agents"), "*.md");
        if (agents > 0) parts.Add(string.Format(Loc.Get("NAgentsFmt"), agents));

        int mcp = ReadServerMap(System.IO.Path.Combine(installPath, ".mcp.json"))?.Count ?? 0;
        if (mcp > 0) parts.Add(string.Format(Loc.Get("NMcpFmt"), mcp));

        return string.Join("  ", parts);
    }

    private static List<string> MarkdownFiles(string dir)
    {
        if (!Directory.Exists(dir)) return new List<string>();
        try { return Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories).ToList(); }
        catch { return new List<string>(); }
    }

    /// <summary>
    /// What a skill, agent or command costs before it is used: the CLI lists each one by name
    /// and description, and reads the body only when it runs.
    /// </summary>
    private static long HeaderTokens(string file)
    {
        var (name, description) = ReadHeader(file);
        return TokenEstimate.Of(name) + TokenEstimate.Of(description);
    }

    private static int CountFiles(string dir, string pattern)
    {
        if (!Directory.Exists(dir)) return 0;
        try { return Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).Count(); }
        catch { return 0; }
    }

    // ── MCP servers ──

    private static List<ExtensionItem> ReadMcp(string? projectFolder, List<PluginInfo> plugins)
    {
        var items = new List<ExtensionItem>();

        // Project .mcp.json - the one set Snipyard can switch, via .claude/settings.local.json.
        if (!string.IsNullOrEmpty(projectFolder))
        {
            var disabled = DisabledProjectServers(projectFolder);
            var servers = ReadServerMap(System.IO.Path.Combine(projectFolder, ".mcp.json"));
            if (servers != null)
            {
                foreach (var (name, node) in servers)
                {
                    items.Add(new ExtensionItem
                    {
                        Kind = ExtensionKind.Mcp,
                        Name = name,
                        Description = DescribeServer(node),
                        Source = "project",
                        Path = System.IO.Path.Combine(projectFolder, ".mcp.json"),
                        Enabled = !disabled.Contains(name),
                        CanToggle = true,
                        Id = name,
                    });
                }
            }
        }

        // ~/.claude.json - read-only on purpose, see the class comment.
        foreach (var (name, node, scope) in UserConfigServers(projectFolder))
        {
            items.Add(new ExtensionItem
            {
                Kind = ExtensionKind.Mcp,
                Name = name,
                Description = DescribeServer(node),
                Source = scope,
                Path = UserConfigPath,
                Enabled = true,
                CanToggle = false,
                Id = name,
            });
        }

        // Whatever the enabled plugins bring with them. The plugin row owns the switch.
        foreach (var plugin in plugins)
        {
            var servers = ReadServerMap(System.IO.Path.Combine(plugin.InstallPath, ".mcp.json"));
            if (servers == null) continue;
            foreach (var (name, node) in servers)
            {
                items.Add(new ExtensionItem
                {
                    Kind = ExtensionKind.Mcp,
                    Name = name,
                    Description = DescribeServer(node),
                    Source = plugin.Name,
                    Path = System.IO.Path.Combine(plugin.InstallPath, ".mcp.json"),
                    Enabled = plugin.Enabled,
                    CanToggle = false,
                    Id = plugin.Id,
                });
            }
        }

        return items;
    }

    private static IEnumerable<(string Name, JsonNode? Node, string Scope)> UserConfigServers(string? projectFolder)
    {
        var root = ReadJson(UserConfigPath);
        if (root == null) yield break;

        foreach (var (name, node) in Pairs(root["mcpServers"]?.AsObject()))
            yield return (name, node, "user");

        if (string.IsNullOrEmpty(projectFolder)) yield break;

        // The CLI keys projects by the path it was launched with, so try both separators.
        var projects = root["projects"]?.AsObject();
        if (projects == null) yield break;
        var entry = MatchProject(projects, projectFolder);
        foreach (var (name, node) in Pairs(entry?["mcpServers"]?.AsObject()))
            yield return (name, node, "user");
    }

    private static IEnumerable<(string, JsonNode?)> Pairs(JsonObject? obj)
    {
        if (obj == null) yield break;
        foreach (var kv in obj) yield return (kv.Key, kv.Value);
    }

    private static JsonNode? MatchProject(JsonObject projects, string projectFolder)
    {
        foreach (var kv in projects)
            if (SamePath(kv.Key, projectFolder)) return kv.Value;
        return null;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a.Replace('\\', '/').TrimEnd('/'), b.Replace('\\', '/').TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Accepts both shapes in the wild: a project .mcp.json wraps its servers in
    /// "mcpServers", a plugin's .mcp.json is the bare map.
    /// </summary>
    private static Dictionary<string, JsonNode?>? ReadServerMap(string path)
    {
        var root = ReadJson(path);
        if (root == null) return null;
        var obj = root["mcpServers"]?.AsObject() ?? root.AsObject();
        var map = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var kv in obj)
            if (kv.Value is JsonObject) map[kv.Key] = kv.Value;
        return map;
    }

    private static string? DescribeServer(JsonNode? node)
    {
        if (node == null) return null;
        var type = node["type"]?.GetValue<string>();
        var url = node["url"]?.GetValue<string>();
        if (url != null) return (type ?? "http") + "  " + url;
        var command = node["command"]?.GetValue<string>();
        if (command == null) return type;
        var args = node["args"] as JsonArray;
        if (args is { Count: > 0 })
            command += " " + string.Join(" ", args.Select(a => a?.ToString() ?? ""));
        return Trim(command);
    }

    private static HashSet<string> DisabledProjectServers(string projectFolder)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var list = ReadJson(ProjectSettingsPath(projectFolder))?["disabledMcpjsonServers"] as JsonArray;
        if (list == null) return set;
        foreach (var entry in list)
            if (entry?.GetValue<string>() is { } name) set.Add(name);
        return set;
    }

    // ── Skills ──

    private static List<ExtensionItem> ReadSkills(string? projectFolder, List<PluginInfo> plugins)
    {
        var items = new List<ExtensionItem>();
        var modes = SkillModes(projectFolder);

        if (!string.IsNullOrEmpty(projectFolder))
            AddSkills(items,
                SkillFilesUnder(System.IO.Path.Combine(projectFolder, ".claude", "skills")), "project", true, modes);

        AddSkills(items, SkillFilesUnder(System.IO.Path.Combine(UserClaudeDir, "skills")), "user", true, modes);

        foreach (var plugin in plugins)
            AddSkills(items, plugin.SkillFiles, plugin.Name, plugin.Enabled, null);

        return items;
    }

    /// <param name="modes">skillOverrides for project and personal skills; null for a plugin's.</param>
    private static void AddSkills(List<ExtensionItem> items, List<string> files, string source, bool enabled,
        Dictionary<string, string>? modes)
    {
        foreach (var file in files)
        {
            var (header, description) = ReadHeader(file);
            var name = header ?? System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(file)) ?? "skill";
            var mode = modes?.GetValueOrDefault(name, SkillModeOn);
            items.Add(new ExtensionItem
            {
                Kind = ExtensionKind.Skill,
                Name = name,
                Description = Trim(description),
                Source = source,
                Path = file,
                // "off" hides a skill from Claude and the slash menu alike - as good as not loaded.
                Enabled = enabled && mode != SkillModeOff,
                CanToggle = false,
                Id = file,
                // What Claude is shown: name and description, the name alone, or nothing.
                Tokens = mode switch
                {
                    SkillModeNameOnly => TokenEstimate.Of(name),
                    SkillModeUserOnly or SkillModeOff => 0,
                    _ => TokenEstimate.Of(name) + TokenEstimate.Of(description),
                },
                SkillMode = mode,
            });
        }
    }

    public const string SkillModeOn = "on";
    public const string SkillModeNameOnly = "name-only";
    public const string SkillModeUserOnly = "user-invocable-only";
    public const string SkillModeOff = "off";

    /// <summary>
    /// skillOverrides as the CLI layers it: personal settings, then the project's shared file,
    /// then its local file - where /skills and Snipyard both write - on top.
    /// </summary>
    private static Dictionary<string, string> SkillModes(string? projectFolder)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        Merge(ReadJson(UserSettingsPath));
        if (!string.IsNullOrEmpty(projectFolder))
        {
            Merge(ReadJson(System.IO.Path.Combine(projectFolder, ".claude", "settings.json")));
            Merge(ReadJson(ProjectSettingsPath(projectFolder)));
        }
        return map;

        void Merge(JsonNode? root)
        {
            if (root?["skillOverrides"] is not JsonObject overrides) return;
            foreach (var kv in overrides)
            {
                if (kv.Value is JsonValue value && value.TryGetValue<string>(out var mode))
                    map[kv.Key] = mode;
            }
        }
    }

    /// <summary>Pulls name and the untrimmed description out of the YAML front matter.</summary>
    private static (string? Name, string? Description) ReadHeader(string file)
    {
        var lines = FrontMatterLines(file);
        return (FrontMatter(lines, "name:"), FrontMatter(lines, "description:"));
    }

    private static List<string> FrontMatterLines(string file)
    {
        var lines = new List<string>();
        try
        {
            using var reader = new StreamReader(file);
            if (reader.ReadLine()?.TrimEnd() != "---") return lines;

            for (int i = 0; i < 60; i++)
            {
                var line = reader.ReadLine();
                if (line == null || line.TrimEnd() == "---") break;
                lines.Add(line);
            }
        }
        catch { lines.Clear(); }
        return lines;
    }

    /// <summary>
    /// The value of one front-matter key. Enough YAML for the two keys we want and no more,
    /// except that a folded value ("description: &gt;") holds no text on its own line - for
    /// those the indented block underneath is what gets joined and returned.
    /// </summary>
    private static string? FrontMatter(List<string> lines, string key)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith(key, StringComparison.Ordinal)) continue;

            var value = lines[i][key.Length..].Trim();
            if (value.Length > 0 && value[0] != '>' && value[0] != '|')
                return Unquote(value);

            var folded = new List<string>();
            for (int j = i + 1; j < lines.Count; j++)
            {
                if (lines[j].Length > 0 && !char.IsWhiteSpace(lines[j][0])) break;
                var text = lines[j].Trim();
                if (text.Length > 0) folded.Add(text);
            }
            return folded.Count > 0 ? string.Join(" ", folded) : null;
        }
        return null;
    }

    private static string? Unquote(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
            value = value[1..^1];
        return value.Length == 0 ? null : value;
    }

    private static string? Trim(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        return value.Length <= MaxDescription ? value : value[..MaxDescription] + "...";
    }

    // ── Writes ──

    /// <summary>Flips a plugin in ~/.claude/settings.json. Returns null on success, else the error.</summary>
    public static string? SetPluginEnabled(string id, bool enabled) =>
        Rewrite(UserSettingsPath, root =>
        {
            var map = root["enabledPlugins"]?.AsObject();
            if (map == null)
            {
                map = new JsonObject();
                root["enabledPlugins"] = map;
            }
            map[id] = enabled;
        });

    /// <summary>
    /// Flips a project .mcp.json server by keeping it in the project's
    /// disabledMcpjsonServers list. Returns null on success, else the error.
    /// </summary>
    public static string? SetProjectMcpEnabled(string projectFolder, string server, bool enabled) =>
        Rewrite(ProjectSettingsPath(projectFolder), root =>
        {
            var list = root["disabledMcpjsonServers"] as JsonArray;
            if (list == null)
            {
                list = new JsonArray();
                root["disabledMcpjsonServers"] = list;
            }

            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i]?.GetValue<string>() == server) list.RemoveAt(i);
            if (!enabled) list.Add(server);

            // An empty allow-list is noise in a file the user also reads.
            if (list.Count == 0) root.Remove("disabledMcpjsonServers");
        });

    /// <summary>
    /// Sets one skill's skillOverrides entry in the project's settings.local.json - the file
    /// the CLI's own /skills screen writes. "on" is the default, so it removes the entry.
    /// Returns null on success, else the error.
    /// </summary>
    public static string? SetSkillMode(string projectFolder, string skill, string mode) =>
        Rewrite(ProjectSettingsPath(projectFolder), root =>
        {
            var map = root["skillOverrides"] as JsonObject;
            if (map == null)
            {
                map = new JsonObject();
                root["skillOverrides"] = map;
            }

            if (mode == SkillModeOn) map.Remove(skill);
            else map[skill] = mode;

            if (map.Count == 0) root.Remove("skillOverrides");
        });

    /// <summary>
    /// Back up, edit the parsed tree, write atomically. Editing the tree rather than
    /// re-serializing a model is what keeps every key we do not know about.
    /// </summary>
    private static string? Rewrite(string path, Action<JsonObject> edit)
    {
        try
        {
            var root = ReadJson(path)?.AsObject() ?? new JsonObject();

            if (File.Exists(path))
                File.Copy(path, path + ".snipyard-backup", overwrite: true);
            else
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

            edit(root);

            var temp = path + ".snipyard-tmp";
            using (var stream = File.Create(temp))
            {
                // Writing the node straight out keeps JsonSerializer - and the type resolver
                // it wants - out of this entirely.
                using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
                {
                    Indented = true,
                    // The files carry Japanese paths; escaping them would churn every line.
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
                root.WriteTo(writer);
            }
            File.Move(temp, path, overwrite: true);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static JsonNode? ReadJson(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonNode.Parse(File.ReadAllText(path), null, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch { return null; }
    }
}

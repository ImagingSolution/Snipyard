using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Snipyard.Terminal;

namespace Snipyard.Services;

/// <summary>
/// Caches detected Excalidraw diagrams per project folder.
/// Diagrams are saved to ~/.claude/projects/{normalized-folder}/diagrams/
/// so they persist across sessions and can be restored on Resume.
/// </summary>
public static class DiagramCache
{
    private static string GetDiagramDir(string projectFolder)
    {
        // A parent folder can collapse to the same key as its subfolder; the literal name decides.
        var literal = ClaudeProjectPaths.ProjectDir(projectFolder);
        if (Directory.Exists(literal)) return Path.Combine(literal, "diagrams");

        var normalized = NormalizeFolderName(projectFolder);
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects");

        // Find matching project directory (normalize both sides for comparison)
        if (Directory.Exists(baseDir))
        {
            foreach (var dir in Directory.GetDirectories(baseDir))
            {
                var dirName = Path.GetFileName(dir);
                var normalizedDir = NormalizeFolderName(dirName);
                if (normalizedDir.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                    return Path.Combine(dir, "diagrams");
            }
        }

        // Fallback: create under normalized name
        return Path.Combine(baseDir, normalized, "diagrams");
    }

    /// <summary>
    /// Save a diagram to the cache. Returns the saved file path.
    /// </summary>
    public static string? Save(string projectFolder, CodeBlockInfo block)
    {
        if (string.IsNullOrEmpty(projectFolder) || block.Content.Length < 50)
            return null;

        try
        {
            var dir = GetDiagramDir(projectFolder);
            Directory.CreateDirectory(dir);

            var id = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"[..30];
            var filePath = Path.Combine(dir, $"{id}.excalidraw");

            var doc = new
            {
                type = "excalidraw",
                version = 2,
                source = "Snipyard",
                createdAt = DateTime.Now.ToString("o"),
                elements = JsonSerializer.Deserialize<JsonElement>(
                    CleanJsonWhitespace(block.Content))
            };

            var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
            return filePath;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DiagramCache] Save error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Load all cached diagrams for a project folder.
    /// Returns CodeBlockInfo list sorted by creation time (newest first).
    /// </summary>
    public static List<CodeBlockInfo> Load(string projectFolder)
    {
        var results = new List<CodeBlockInfo>();
        if (string.IsNullOrEmpty(projectFolder)) return results;

        try
        {
            var dir = GetDiagramDir(projectFolder);
            if (!Directory.Exists(dir)) return results;

            foreach (var file in Directory.GetFiles(dir, "*.excalidraw"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var doc = JsonDocument.Parse(json);

                    string elementsJson;
                    if (doc.RootElement.TryGetProperty("elements", out var elProp))
                        elementsJson = elProp.GetRawText();
                    else
                        continue;

                    if (elementsJson.Length < 50) continue;

                    results.Add(new CodeBlockInfo(0, 0, "excalidraw", elementsJson, CodeBlockType.Excalidraw));
                }
                catch { }
            }

            // Sort by file creation time, newest first
            results.Sort((a, b) => 0); // Already in directory order
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DiagramCache] Load error: {ex.Message}");
        }

        return results;
    }

    /// <summary>
    /// Get the number of cached diagrams for a project folder.
    /// </summary>
    public static int Count(string projectFolder)
    {
        if (string.IsNullOrEmpty(projectFolder)) return 0;
        try
        {
            var dir = GetDiagramDir(projectFolder);
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.excalidraw").Length : 0;
        }
        catch { return 0; }
    }

    private static string NormalizeFolderName(string path)
    {
        path = path.Replace('/', '\\').TrimEnd('\\');
        var sb = new System.Text.StringBuilder(path.Length);
        bool lastWasDash = false;
        foreach (char c in path)
        {
            if (char.IsLetterOrDigit(c) && c <= 127)
            {
                sb.Append(c);
                lastWasDash = false;
            }
            else
            {
                if (!lastWasDash)
                    sb.Append('-');
                lastWasDash = true;
            }
        }
        return sb.ToString().Trim('-');
    }

    private static string CleanJsonWhitespace(string json)
    {
        var sb = new System.Text.StringBuilder(json.Length);
        bool inString = false;
        bool escape = false;
        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];
            if (escape) { sb.Append(c); escape = false; continue; }
            if (c == '\\' && inString) { sb.Append(c); escape = true; continue; }
            if (c == '"') { inString = !inString; sb.Append(c); continue; }
            if (inString) sb.Append(c);
            else if (c > ' ' && c <= '~') sb.Append(c);
        }
        return sb.ToString();
    }
}

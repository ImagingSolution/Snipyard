using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Snipyard.Services;

public enum ChatTaskStatus { Pending, InProgress, Completed }

/// <summary>One entry of Claude's task list, as it stands after the last task tool call.</summary>
public record ChatTask(string Id, string Subject, string? ActiveForm, ChatTaskStatus Status);

/// <summary>
/// Rebuilds the task list from a session's tool calls. The CLI keeps no
/// task file: older versions log TodoWrite with the whole list each time, newer ones log
/// TaskCreate / TaskUpdate as increments, so the state is replayed call by call.
/// </summary>
public static class ChatTaskTracker
{
    private static readonly Regex TaskIdInResult = new(@"#(\d+)", RegexOptions.Compiled);

    public static List<ChatTask> ExtractTasks(IEnumerable<ConversationMessage> messages)
    {
        var tasks = new List<ChatTask>();
        int nextId = 1;
        foreach (var call in AllCalls(messages))
        {
            if (call.IsError || call.InputJson == null) continue;
            if (call.Name is not ("TodoWrite" or "TaskCreate" or "TaskUpdate")) continue;
            try
            {
                using var doc = JsonDocument.Parse(call.InputJson);
                var input = doc.RootElement;
                if (input.ValueKind != JsonValueKind.Object) continue;
                switch (call.Name)
                {
                    case "TodoWrite":
                        if (!input.TryGetProperty("todos", out var todos) || todos.ValueKind != JsonValueKind.Array) break;
                        tasks.Clear();
                        int n = 1;
                        foreach (var t in todos.EnumerateArray())
                        {
                            var subject = Str(t, "content") ?? Str(t, "subject");
                            if (string.IsNullOrWhiteSpace(subject)) continue;
                            tasks.Add(new ChatTask(Str(t, "id") ?? (n++).ToString(), subject,
                                Str(t, "activeForm"), ParseStatus(Str(t, "status")) ?? ChatTaskStatus.Pending));
                        }
                        break;

                    case "TaskCreate":
                    {
                        var subject = Str(input, "subject") ?? Str(input, "title") ?? Str(input, "content");
                        if (string.IsNullOrWhiteSpace(subject)) break;
                        // The result says which number the CLI gave it ("Task #3 created…")
                        string id;
                        var m = call.Result == null ? null : TaskIdInResult.Match(call.Result);
                        if (m is { Success: true })
                        {
                            id = m.Groups[1].Value;
                            if (int.TryParse(id, out var num)) nextId = Math.Max(nextId, num + 1);
                        }
                        else id = (nextId++).ToString();
                        tasks.RemoveAll(t => t.Id == id);
                        tasks.Add(new ChatTask(id, subject, Str(input, "activeForm"), ChatTaskStatus.Pending));
                        break;
                    }

                    case "TaskUpdate":
                    {
                        var id = Str(input, "taskId") ?? Str(input, "id");
                        if (id == null) break;
                        int idx = tasks.FindIndex(t => t.Id == id);
                        if (idx < 0) break;
                        var status = Str(input, "status");
                        if (status == "deleted") { tasks.RemoveAt(idx); break; }
                        var cur = tasks[idx];
                        tasks[idx] = cur with
                        {
                            Subject = Str(input, "subject") ?? cur.Subject,
                            ActiveForm = Str(input, "activeForm") ?? cur.ActiveForm,
                            Status = ParseStatus(status) ?? cur.Status,
                        };
                        break;
                    }
                }
            }
            catch (JsonException) { }
        }
        return tasks;
    }

    /// <summary>The transcript the subagent started by this Agent call wrote, if any.</summary>
    public static string? TranscriptFor(string sessionPath, string toolUseId) =>
        ReadTranscriptIndex(sessionPath).GetValueOrDefault(toolUseId);

    /// <summary>"type — description" for an Agent call, as the subagent's title.</summary>
    public static string AgentLabel(ToolCall call)
    {
        string type = "", desc = "";
        if (call.InputJson != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(call.InputJson);
                type = Str(doc.RootElement, "subagent_type") ?? "";
                desc = Str(doc.RootElement, "description") ?? "";
            }
            catch (JsonException) { }
        }
        if (type.Length == 0) type = "general-purpose";
        return desc.Length == 0 ? type : $"{type} — {desc}";
    }

    /// <summary>tool_use id → the transcript its meta file points at, null if not written yet.</summary>
    private static Dictionary<string, string?> ReadTranscriptIndex(string sessionPath)
    {
        var map = new Dictionary<string, string?>();
        try
        {
            var dir = Path.Combine(Path.GetDirectoryName(sessionPath) ?? "",
                Path.GetFileNameWithoutExtension(sessionPath), "subagents");
            if (!Directory.Exists(dir)) return map;
            foreach (var meta in Directory.EnumerateFiles(dir, "*.meta.json"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(meta));
                    if (Str(doc.RootElement, "toolUseId") is not string id) continue;
                    var stem = meta[..^".meta.json".Length];
                    var name = Path.GetFileName(stem);
                    if (!name.StartsWith("agent-", StringComparison.Ordinal)) continue;
                    var jsonl = stem + ".jsonl";
                    map[id] = File.Exists(jsonl) ? jsonl : null;
                }
                catch { }
            }
        }
        catch { }
        return map;
    }

    private static IEnumerable<ToolCall> AllCalls(IEnumerable<ConversationMessage> messages) =>
        messages.Where(m => m.Tools != null).SelectMany(m => m.Tools!);

    private static ChatTaskStatus? ParseStatus(string? s) => s switch
    {
        "pending" => ChatTaskStatus.Pending,
        "in_progress" => ChatTaskStatus.InProgress,
        "completed" => ChatTaskStatus.Completed,
        _ => null,
    };

    // Ids come as strings in newer CLIs and as numbers in some older ones
    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;
}

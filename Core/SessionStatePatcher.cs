using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CopilotUsageFilter;

/// <summary>
/// Locates a Copilot session-state folder by conversation ID,
/// finds the assistant.message event matching interactionId + turnId,
/// and patches it with token counts.
/// </summary>
public static class SessionStatePatcher
{
    private static readonly string SessionStateRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot", "session-state");

    // Cache positive hits only. Null results are NOT cached so that a folder
    // that appears slightly after the first span is eventually found.
    private static readonly ConcurrentDictionary<string, string> s_folderCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the session folder whose name (GUID) matches conversationId.
    /// Copilot session folders are named with the session/conversation GUID.
    /// <paramref name="rootOverride"/> allows tests to supply a temp directory instead of
    /// the real <c>%USERPROFILE%\.copilot\session-state</c> path.
    /// </summary>
    public static string? FindSessionFolder(string conversationId, string? rootOverride = null)
    {
        // Use the cache only for real (non-test) lookups
        if (rootOverride == null && s_folderCache.TryGetValue(conversationId, out var cached))
            return cached;

        var root = rootOverride ?? SessionStateRoot;
        if (!Directory.Exists(root)) return null;

        // Direct match: folder name IS the conversation id
        var direct = Path.Combine(root, conversationId);
        if (Directory.Exists(direct))
        {
            if (rootOverride == null) s_folderCache[conversationId] = direct;
            return direct;
        }

        // Fallback: scan session.start events for a sessionId match
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var eventsFile = Path.Combine(dir, "events.jsonl");
            if (!File.Exists(eventsFile)) continue;
            try
            {
                using var fs = new FileStream(eventsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (!line.Contains("session.start")) continue;
                    var node = JsonNode.Parse(line);
                    var sessionId = node?["data"]?["sessionId"]?.GetValue<string>();
                    if (string.Equals(sessionId, conversationId, StringComparison.OrdinalIgnoreCase))
                    {
                        if (rootOverride == null) s_folderCache[conversationId] = dir;
                        return dir;
                    }
                }
            }
            catch { }
        }

        return null;
    }

    /// <summary>
    /// Searches the session's events.jsonl for an assistant.message whose
    /// interactionId and turnId match, then appends token fields to that line.
    /// If turnId is null, matches by interactionId alone (takes the last match).
    /// </summary>
    public static bool PatchAssistantMessage(
        string sessionFolder,
        string interactionId,
        string? turnId,
        long? inputTokens,
        long? cacheCreationTokens,
        long? cacheReadTokens,
        long? outputTokens)
    {
        var eventsFile = Path.Combine(sessionFolder, "events.jsonl");
        if (!File.Exists(eventsFile)) return false;

        string[] lines;
        try
        {
            using var fs = new FileStream(eventsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var list = new List<string>();
            string? l;
            while ((l = sr.ReadLine()) != null) list.Add(l);
            lines = list.ToArray();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Patcher] Failed to read {eventsFile}: {ex.Message}");
            return false;
        }

        // When turnId is provided, we need an exact match on both fields.
        // When turnId is absent, we patch the last assistant.message for this interactionId
        // (most recent turn = most likely to carry the final token counts).
        int bestIndex = -1;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.Contains("assistant.message")) continue;
            if (!line.Contains(interactionId)) continue;

            try
            {
                var node = JsonNode.Parse(line)?.AsObject();
                if (node == null) continue;

                if (!string.Equals(node["type"]?.GetValue<string>(), "assistant.message",
                        StringComparison.Ordinal)) continue;

                var data = node["data"]?.AsObject();
                if (data == null) continue;

                var msgInteractionId = data["interactionId"]?.GetValue<string>();
                if (!string.Equals(msgInteractionId, interactionId, StringComparison.OrdinalIgnoreCase)) continue;

                if (turnId != null)
                {
                    // Exact turn match.
                    // turnId may be a JSON string "0" or a JSON number 0 — handle both.
                    string? msgTurnId = null;
                    if (data["turnId"] is System.Text.Json.Nodes.JsonValue tv)
                    {
                        if (!tv.TryGetValue<string>(out msgTurnId) &&
                             tv.TryGetValue<long>(out var n))
                            msgTurnId = n.ToString();
                    }
                    if (!string.Equals(msgTurnId, turnId, StringComparison.OrdinalIgnoreCase)) continue;
                    bestIndex = i;
                    break; // exact match — stop
                }
                else
                {
                    bestIndex = i; // keep last match
                }
            }
            catch { }
        }

        if (bestIndex < 0) return false;

        try
        {
            var node = JsonNode.Parse(lines[bestIndex])!.AsObject();
            var data = node["data"]!.AsObject();

            if (inputTokens.HasValue && data["input_tokens"] == null)
                data["input_tokens"] = inputTokens.Value;
            if (cacheCreationTokens.HasValue && data["cache_creation_tokens"] == null)
                data["cache_creation_tokens"] = cacheCreationTokens.Value;
            if (cacheReadTokens.HasValue && data["cache_read_tokens"] == null)
                data["cache_read_tokens"] = cacheReadTokens.Value;
            if (outputTokens.HasValue && data["output_tokens"] == null)
                data["output_tokens"] = outputTokens.Value;

            lines[bestIndex] = node.ToJsonString(JsonOptions.Default);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Patcher] Failed to patch line {bestIndex}: {ex.Message}");
            return false;
        }

        try
        {
            var tmp = eventsFile + ".tmp";
            File.WriteAllLines(tmp, lines);
            File.Replace(tmp, eventsFile, null);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Patcher] Failed to write {eventsFile}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Patches the last sub-agent assistant.message whose interactionId and
    /// parentToolCallId both match, adding aggregate input token counts from the
    /// OTLP sub-agent span.  Sub-agent events already contain per-step outputTokens
    /// written by Copilot; we only add the aggregate inputTokens that are absent.
    /// Patching is skipped if the target event already has input_tokens set.
    /// </summary>
    public static bool PatchSubAgentMessage(
        string sessionFolder,
        string interactionId,
        string parentToolCallId,
        long? inputTokens,
        long? cacheCreationTokens,
        long? cacheReadTokens)
    {
        var eventsFile = Path.Combine(sessionFolder, "events.jsonl");
        if (!File.Exists(eventsFile)) return false;

        string[] lines;
        try
        {
            using var fs = new FileStream(eventsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var list = new List<string>();
            string? l;
            while ((l = sr.ReadLine()) != null) list.Add(l);
            lines = list.ToArray();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Patcher] Failed to read {eventsFile}: {ex.Message}");
            return false;
        }

        // Find the last assistant.message that matches interactionId + parentToolCallId
        // and has no turnId (sub-agent steps).  Track whether it is already patched.
        int  bestIndex     = -1;
        bool alreadyPatched = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.Contains("assistant.message")) continue;
            if (!line.Contains(interactionId))       continue;
            if (!line.Contains(parentToolCallId))    continue;

            try
            {
                var node = JsonNode.Parse(line)?.AsObject();
                if (node == null) continue;

                if (!string.Equals(node["type"]?.GetValue<string>(), "assistant.message",
                        StringComparison.Ordinal)) continue;

                var data = node["data"]?.AsObject();
                if (data == null) continue;

                var msgInteractionId = data["interactionId"]?.GetValue<string>();
                if (!string.Equals(msgInteractionId, interactionId,
                        StringComparison.OrdinalIgnoreCase)) continue;

                var msgParentCallId = data["parentToolCallId"]?.GetValue<string>();
                if (!string.Equals(msgParentCallId, parentToolCallId,
                        StringComparison.OrdinalIgnoreCase)) continue;

                // Only target sub-agent steps (events without turnId)
                if (data["turnId"] != null) continue;

                bestIndex     = i;
                alreadyPatched = data["input_tokens"] != null;
            }
            catch { }
        }

        if (bestIndex < 0 || alreadyPatched) return false;

        try
        {
            var node = JsonNode.Parse(lines[bestIndex])!.AsObject();
            var data = node["data"]!.AsObject();

            if (inputTokens.HasValue)
                data["input_tokens"] = inputTokens.Value;
            if (cacheCreationTokens.HasValue && data["cache_creation_tokens"] == null)
                data["cache_creation_tokens"] = cacheCreationTokens.Value;
            if (cacheReadTokens.HasValue && data["cache_read_tokens"] == null)
                data["cache_read_tokens"] = cacheReadTokens.Value;

            lines[bestIndex] = node.ToJsonString(JsonOptions.Default);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Patcher] Failed to patch sub-agent line {bestIndex}: {ex.Message}");
            return false;
        }

        try
        {
            var tmp = eventsFile + ".tmp";
            File.WriteAllLines(tmp, lines);
            File.Replace(tmp, eventsFile, null);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Patcher] Failed to write {eventsFile}: {ex.Message}");
            return false;
        }
    }
}

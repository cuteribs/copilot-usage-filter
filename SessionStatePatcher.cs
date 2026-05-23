using System.Text.Json;
using System.Text.Json.Nodes;

namespace CopilotUsageFilter;

/// <summary>
/// Locates a Copilot session-state folder by conversation ID,
/// finds the assistant.message event matching an interaction ID,
/// and patches it with token counts.
/// </summary>
public static class SessionStatePatcher
{
    private static readonly string SessionStateRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot", "session-state");

    /// <summary>
    /// Returns the session folder whose name (GUID) matches conversationId.
    /// Copilot session folders are named with the session/conversation GUID.
    /// </summary>
    public static string? FindSessionFolder(string conversationId)
    {
        if (!Directory.Exists(SessionStateRoot)) return null;

        // Direct match: folder name IS the conversation id
        var direct = Path.Combine(SessionStateRoot, conversationId);
        if (Directory.Exists(direct)) return direct;

        // Fallback: scan session.start events for a sessionId match
        foreach (var dir in Directory.EnumerateDirectories(SessionStateRoot))
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
                        return dir;
                }
            }
            catch { }
        }

        return null;
    }

    /// <summary>
    /// Searches the session's events.jsonl for an assistant.message whose
    /// interactionId matches <paramref name="interactionId"/>, then appends
    /// the token fields to that JSON line and rewrites the file.
    /// </summary>
    public static bool PatchAssistantMessage(
        string sessionFolder,
        string interactionId,
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
            // Read with shared access so Copilot can still write
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

        bool changed = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.Contains("assistant.message")) continue;
            if (!line.Contains(interactionId)) continue;

            try
            {
                var node = JsonNode.Parse(line)?.AsObject();
                if (node == null) continue;

                var typeVal = node["type"]?.GetValue<string>();
                if (!string.Equals(typeVal, "assistant.message", StringComparison.Ordinal)) continue;

                var data = node["data"]?.AsObject();
                if (data == null) continue;

                var msgInteractionId = data["interactionId"]?.GetValue<string>();
                if (!string.Equals(msgInteractionId, interactionId, StringComparison.OrdinalIgnoreCase)) continue;

                // Append token fields (only if not already present)
                if (inputTokens.HasValue && data["input_tokens"] == null)
                    data["input_tokens"] = inputTokens.Value;
                if (cacheCreationTokens.HasValue && data["cache_creation_tokens"] == null)
                    data["cache_creation_tokens"] = cacheCreationTokens.Value;
                if (cacheReadTokens.HasValue && data["cache_read_tokens"] == null)
                    data["cache_read_tokens"] = cacheReadTokens.Value;
                if (outputTokens.HasValue && data["output_tokens"] == null)
                    data["output_tokens"] = outputTokens.Value;

                lines[i] = node.ToJsonString(JsonOptions.Default);
                changed = true;
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Patcher] Failed to patch line {i}: {ex.Message}");
            }
        }

        if (!changed) return false;

        try
        {
            // Write atomically: write to temp then replace
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

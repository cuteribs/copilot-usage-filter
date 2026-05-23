using System.Text.Json.Nodes;
using CopilotUsageFilter;

namespace CopilotUsageFilter.Tests;

/// <summary>
/// Tests for <see cref="SessionStatePatcher"/>.
/// Each test gets its own isolated temp directory under a shared root that is
/// deleted when the fixture is disposed.
/// </summary>
public sealed class SessionStatePatcherTests : IDisposable
{
    private readonly string _root;

    public SessionStatePatcherTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cui_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Creates a session folder named <paramref name="sessionId"/> with the given JSONL content.</summary>
    private string CreateSession(string sessionId, string eventsJsonl)
    {
        var dir = Path.Combine(_root, sessionId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "events.jsonl"), eventsJsonl);
        return dir;
    }

    private static long? GetTokenField(string line, string field)
    {
        var node = JsonNode.Parse(line);
        var val  = node?["data"]?[field];
        return val?.GetValue<long>();
    }

    // ── FindSessionFolder tests ───────────────────────────────────────────────

    [Fact]
    public void FindSessionFolder_DirectNameMatch_ReturnsFolder()
    {
        CreateSession("my-session-guid", "");

        var result = SessionStatePatcher.FindSessionFolder("my-session-guid", _root);

        Assert.NotNull(result);
        Assert.Equal(Path.Combine(_root, "my-session-guid"), result);
    }

    [Fact]
    public void FindSessionFolder_FallbackScanBySessionId_Finds()
    {
        // Folder has a random name, but its session.start event carries the target sessionId
        var eventsJsonl = """{"type":"session.start","data":{"sessionId":"target-session-id"}}""";
        CreateSession("random-folder-guid", eventsJsonl);

        var result = SessionStatePatcher.FindSessionFolder("target-session-id", _root);

        Assert.NotNull(result);
        Assert.EndsWith("random-folder-guid", result);
    }

    [Fact]
    public void FindSessionFolder_NoMatch_ReturnsNull()
    {
        var result = SessionStatePatcher.FindSessionFolder("nonexistent-id", _root);
        Assert.Null(result);
    }

    [Fact]
    public void FindSessionFolder_RootDoesNotExist_ReturnsNull()
    {
        var result = SessionStatePatcher.FindSessionFolder("any-id", Path.Combine(_root, "does-not-exist"));
        Assert.Null(result);
    }

    // ── PatchAssistantMessage tests ───────────────────────────────────────────

    [Fact]
    public void PatchAssistantMessage_BasicPatch_SetsAllTokenFields()
    {
        var folder = CreateSession("s1",
            """{"type":"assistant.message","data":{"interactionId":"inter1","turnId":"0"}}""");

        var ok = SessionStatePatcher.PatchAssistantMessage(
            folder, "inter1", "0",
            inputTokens: 100, cacheCreationTokens: 20, cacheReadTokens: 5, outputTokens: 50);

        Assert.True(ok);
        var line = File.ReadAllLines(Path.Combine(folder, "events.jsonl"))[0];
        Assert.Equal(100, GetTokenField(line, "input_tokens"));
        Assert.Equal(50,  GetTokenField(line, "output_tokens"));
        Assert.Equal(20,  GetTokenField(line, "cache_creation_tokens"));
        Assert.Equal(5,   GetTokenField(line, "cache_read_tokens"));
    }

    [Fact]
    public void PatchAssistantMessage_ExistingTokenFields_NotOverwritten()
    {
        var folder = CreateSession("s2",
            """{"type":"assistant.message","data":{"interactionId":"inter1","turnId":"0","input_tokens":999,"output_tokens":888}}""");

        SessionStatePatcher.PatchAssistantMessage(
            folder, "inter1", "0",
            inputTokens: 1, cacheCreationTokens: 2, cacheReadTokens: 3, outputTokens: 4);

        var line = File.ReadAllLines(Path.Combine(folder, "events.jsonl"))[0];
        // Existing values must be preserved
        Assert.Equal(999, GetTokenField(line, "input_tokens"));
        Assert.Equal(888, GetTokenField(line, "output_tokens"));
    }

    [Fact]
    public void PatchAssistantMessage_WrongTurnId_DoesNotPatch()
    {
        var folder = CreateSession("s3",
            """{"type":"assistant.message","data":{"interactionId":"inter1","turnId":"1"}}""");

        // Requesting turnId "0" but the event has turnId "1"
        var ok = SessionStatePatcher.PatchAssistantMessage(
            folder, "inter1", "0",
            inputTokens: 100, cacheCreationTokens: null, cacheReadTokens: null, outputTokens: 50);

        Assert.False(ok);
        var line = File.ReadAllLines(Path.Combine(folder, "events.jsonl"))[0];
        Assert.Null(GetTokenField(line, "input_tokens"));
    }

    [Fact]
    public void PatchAssistantMessage_NullTurnId_PatchesLastMatch()
    {
        // Two events with the same interactionId but different turns
        var folder = CreateSession("s4", string.Join('\n',
            """{"type":"assistant.message","data":{"interactionId":"inter1","turnId":"0"}}""",
            """{"type":"assistant.message","data":{"interactionId":"inter1","turnId":"1"}}"""));

        SessionStatePatcher.PatchAssistantMessage(
            folder, "inter1", turnId: null,
            inputTokens: 100, cacheCreationTokens: null, cacheReadTokens: null, outputTokens: 50);

        var lines = File.ReadAllLines(Path.Combine(folder, "events.jsonl"));
        // Only the LAST matching line (index 1) should be patched
        Assert.Null(GetTokenField(lines[0], "input_tokens"));
        Assert.Equal(100, GetTokenField(lines[1], "input_tokens"));
    }

    [Fact]
    public void PatchAssistantMessage_NoMatchingInteractionId_ReturnsFalse()
    {
        var folder = CreateSession("s5",
            """{"type":"assistant.message","data":{"interactionId":"other-id","turnId":"0"}}""");

        var ok = SessionStatePatcher.PatchAssistantMessage(
            folder, "inter1", "0",
            inputTokens: 100, cacheCreationTokens: null, cacheReadTokens: null, outputTokens: 50);

        Assert.False(ok);
    }

    [Fact]
    public void PatchAssistantMessage_FileDoesNotExist_ReturnsFalse()
    {
        var ok = SessionStatePatcher.PatchAssistantMessage(
            Path.Combine(_root, "nonexistent-folder"), "inter1", "0",
            inputTokens: 100, cacheCreationTokens: null, cacheReadTokens: null, outputTokens: 50);

        Assert.False(ok);
    }

    [Fact]
    public void PatchAssistantMessage_TurnIdAsNumber_MatchesCorrectly()
    {
        // turnId stored as JSON number 0 (not string "0")
        var folder = CreateSession("s6",
            """{"type":"assistant.message","data":{"interactionId":"inter1","turnId":0}}""");

        var ok = SessionStatePatcher.PatchAssistantMessage(
            folder, "inter1", "0",
            inputTokens: 42, cacheCreationTokens: null, cacheReadTokens: null, outputTokens: null);

        Assert.True(ok);
        var line = File.ReadAllLines(Path.Combine(folder, "events.jsonl"))[0];
        Assert.Equal(42, GetTokenField(line, "input_tokens"));
    }

    // ── PatchSubAgentMessage tests ────────────────────────────────────────────

    [Fact]
    public void PatchSubAgentMessage_BasicPatch_SetsInputAndCacheFields()
    {
        // Two sub-agent steps for the same parentToolCallId; last one gets patched.
        var folder = CreateSession("sa1", string.Join('\n',
            """{"type":"assistant.message","data":{"interactionId":"inter1","parentToolCallId":"call_ABC","outputTokens":50}}""",
            """{"type":"assistant.message","data":{"interactionId":"inter1","parentToolCallId":"call_ABC","outputTokens":30}}"""));

        var ok = SessionStatePatcher.PatchSubAgentMessage(
            folder, "inter1", "call_ABC",
            inputTokens: 1000, cacheCreationTokens: null, cacheReadTokens: 400);

        Assert.True(ok);
        var lines = File.ReadAllLines(Path.Combine(folder, "events.jsonl"));
        // Only the last matching line is patched
        Assert.Null(GetTokenField(lines[0], "input_tokens"));
        Assert.Equal(1000, GetTokenField(lines[1], "input_tokens"));
        Assert.Equal(400,  GetTokenField(lines[1], "cache_read_tokens"));
    }

    [Fact]
    public void PatchSubAgentMessage_AlreadyPatched_Skipped()
    {
        var folder = CreateSession("sa2",
            """{"type":"assistant.message","data":{"interactionId":"inter1","parentToolCallId":"call_ABC","input_tokens":999}}""");

        var ok = SessionStatePatcher.PatchSubAgentMessage(
            folder, "inter1", "call_ABC",
            inputTokens: 1, cacheCreationTokens: null, cacheReadTokens: null);

        Assert.False(ok);
        var line = File.ReadAllLines(Path.Combine(folder, "events.jsonl"))[0];
        Assert.Equal(999, GetTokenField(line, "input_tokens")); // untouched
    }

    [Fact]
    public void PatchSubAgentMessage_WrongParentToolCallId_ReturnsFalse()
    {
        var folder = CreateSession("sa3",
            """{"type":"assistant.message","data":{"interactionId":"inter1","parentToolCallId":"call_OTHER"}}""");

        var ok = SessionStatePatcher.PatchSubAgentMessage(
            folder, "inter1", "call_ABC",
            inputTokens: 100, cacheCreationTokens: null, cacheReadTokens: null);

        Assert.False(ok);
    }

    [Fact]
    public void PatchSubAgentMessage_EventWithTurnId_Skipped()
    {
        // Events that have turnId are direct-agent turns, not sub-agent steps — skip.
        var folder = CreateSession("sa4",
            """{"type":"assistant.message","data":{"interactionId":"inter1","parentToolCallId":"call_ABC","turnId":"0"}}""");

        var ok = SessionStatePatcher.PatchSubAgentMessage(
            folder, "inter1", "call_ABC",
            inputTokens: 100, cacheCreationTokens: null, cacheReadTokens: null);

        Assert.False(ok);
    }

    [Fact]
    public void PatchSubAgentMessage_MultipleToolCallIds_PatchesOnlyMatching()
    {
        // Two separate sub-agent invocations with different parentToolCallIds.
        var folder = CreateSession("sa5", string.Join('\n',
            """{"type":"assistant.message","data":{"interactionId":"inter1","parentToolCallId":"call_A","outputTokens":10}}""",
            """{"type":"assistant.message","data":{"interactionId":"inter1","parentToolCallId":"call_B","outputTokens":20}}"""));

        SessionStatePatcher.PatchSubAgentMessage(
            folder, "inter1", "call_A",
            inputTokens: 500, cacheCreationTokens: null, cacheReadTokens: null);

        var lines = File.ReadAllLines(Path.Combine(folder, "events.jsonl"));
        Assert.Equal(500, GetTokenField(lines[0], "input_tokens"));
        Assert.Null(GetTokenField(lines[1], "input_tokens")); // call_B untouched
    }
}

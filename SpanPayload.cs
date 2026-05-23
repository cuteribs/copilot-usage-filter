using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotUsageFilter;

/// <summary>
/// Minimal model for the simplified JSON span accepted by this collector.
/// Only spans with type="span" and attributes.gen_ai.operation.name="chat" are processed.
/// </summary>
public sealed class SpanPayload
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("attributes")]
    public SpanAttributes? Attributes { get; set; }

    public static SpanPayload? TryParse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SpanPayload>(json, JsonOptions.Default);
        }
        catch
        {
            return null;
        }
    }

    public bool IsChat =>
        string.Equals(Type, "span", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Attributes?.GenAiOperationName, "chat", StringComparison.OrdinalIgnoreCase);
}

public sealed class SpanAttributes
{
    [JsonPropertyName("gen_ai.operation.name")]
    public string? GenAiOperationName { get; set; }

    [JsonPropertyName("gen_ai.conversation.id")]
    public string? ConversationId { get; set; }

    [JsonPropertyName("github.copilot.interaction_id")]
    public string? InteractionId { get; set; }

    [JsonPropertyName("gen_ai.usage.input_tokens")]
    public long? InputTokens { get; set; }

    [JsonPropertyName("gen_ai.usage.output_tokens")]
    public long? OutputTokens { get; set; }

    [JsonPropertyName("gen_ai.usage.cache_creation.input_tokens")]
    public long? CacheCreationTokens { get; set; }

    [JsonPropertyName("gen_ai.usage.cache_read.input_tokens")]
    public long? CacheReadTokens { get; set; }
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

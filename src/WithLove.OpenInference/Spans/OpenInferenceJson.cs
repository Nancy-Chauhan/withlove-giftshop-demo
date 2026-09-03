using System.Text.Json;

namespace WithLove.OpenInference.Spans;

/// <summary>A JSON document validated once for retrieval-document metadata.</summary>
public readonly struct OpenInferenceJson
{
    private readonly string? json;

    private OpenInferenceJson(string json) => this.json = json;

    /// <summary>Gets the validated JSON document.</summary>
    public string Value => json ?? throw new InvalidOperationException("The JSON value is not initialized.");

    /// <summary>Gets whether this value contains a validated JSON document.</summary>
    public bool IsInitialized => json is not null;

    /// <summary>Validates and preserves a raw JSON document.</summary>
    public static OpenInferenceJson Raw(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var _ = JsonDocument.Parse(json);
        return new OpenInferenceJson(json);
    }
}

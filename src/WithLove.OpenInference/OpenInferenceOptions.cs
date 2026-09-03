namespace WithLove.OpenInference;

/// <summary>Content-privacy settings used by WithLove application spans.</summary>
public sealed class OpenInferenceOptions
{
    /// <summary>Redacts <c>input.value</c> and omits <c>input.mime_type</c>.</summary>
    public bool? HideInputs { get; init; }

    /// <summary>Redacts <c>output.value</c> and omits <c>output.mime_type</c>.</summary>
    public bool? HideOutputs { get; init; }
}

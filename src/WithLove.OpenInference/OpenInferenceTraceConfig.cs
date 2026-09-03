namespace WithLove.OpenInference;

/// <summary>Resolved content-privacy configuration for WithLove application spans.</summary>
public sealed class OpenInferenceTraceConfig
{
    public const string RedactedValue = "__REDACTED__";
    public const string HideInputsEnvironmentVariable = "OPENINFERENCE_HIDE_INPUTS";
    public const string HideOutputsEnvironmentVariable = "OPENINFERENCE_HIDE_OUTPUTS";
    public const string CaptureMessageContentEnvironmentVariable =
        "OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT";

    public static OpenInferenceTraceConfig Default { get; } = Create();

    public bool HideInputs { get; }
    public bool HideOutputs { get; }

    private OpenInferenceTraceConfig(OpenInferenceOptions options, Func<string, string?> environment)
    {
        var captureMessageContent =
            bool.TryParse(environment(CaptureMessageContentEnvironmentVariable), out var capture)
            && capture;

        HideInputs = Resolve(
            options.HideInputs,
            HideInputsEnvironmentVariable,
            defaultValue: !captureMessageContent,
            environment);
        HideOutputs = Resolve(
            options.HideOutputs,
            HideOutputsEnvironmentVariable,
            defaultValue: !captureMessageContent,
            environment);
    }

    /// <summary>Creates an immutable configuration. Explicit options override environment variables.</summary>
    public static OpenInferenceTraceConfig Create(
        OpenInferenceOptions? options = null,
        Func<string, string?>? getEnvironmentVariable = null) =>
        new(options ?? new OpenInferenceOptions(), getEnvironmentVariable ?? Environment.GetEnvironmentVariable);

    internal PrivacyAction GetPrivacyAction(string key)
    {
        if (HideInputs && key == OpenInferenceAttributes.InputValue)
        {
            return PrivacyAction.Redact;
        }

        if (HideInputs && key == OpenInferenceAttributes.InputMimeType)
        {
            return PrivacyAction.Omit;
        }

        if (HideOutputs && key == OpenInferenceAttributes.OutputValue)
        {
            return PrivacyAction.Redact;
        }

        if (HideOutputs && key == OpenInferenceAttributes.OutputMimeType)
        {
            return PrivacyAction.Omit;
        }

        return PrivacyAction.Keep;
    }

    private static bool Resolve(
        bool? codeValue,
        string environmentVariable,
        bool defaultValue,
        Func<string, string?> environment) =>
        codeValue
        ?? (bool.TryParse(environment(environmentVariable), out var value) ? value : defaultValue);
}

internal enum PrivacyAction
{
    Keep,
    Omit,
    Redact
}

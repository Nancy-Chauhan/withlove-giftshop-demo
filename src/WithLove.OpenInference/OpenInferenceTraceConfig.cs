namespace WithLove.OpenInference;

/// <summary>Resolved content-privacy configuration for WithLove application spans.</summary>
public sealed class OpenInferenceTraceConfig
{
    /// <summary>Gets the sentinel applied to protected input and output values.</summary>
    public const string RedactedValue = "__REDACTED__";

    /// <summary>Gets the application-level environment variable that authorizes AI content capture.</summary>
    public const string CaptureAiContentEnvironmentVariable = "Telemetry__CaptureAiContent";

    /// <summary>Gets a configuration that captures input and output content.</summary>
    public static OpenInferenceTraceConfig Enabled { get; } = new(captureAiContent: true);

    /// <summary>Gets a configuration that protects input and output content.</summary>
    public static OpenInferenceTraceConfig Disabled { get; } = new(captureAiContent: false);

    /// <summary>Gets configuration resolved from the current process environment.</summary>
    public static OpenInferenceTraceConfig Default { get; } = Create();

    /// <summary>Gets whether input and output payload capture is authorized.</summary>
    public bool CaptureAiContent { get; }

    private OpenInferenceTraceConfig(bool captureAiContent)
    {
        CaptureAiContent = captureAiContent;
    }

    /// <summary>
    /// Creates an immutable configuration from the application content-capture environment
    /// variable. Missing values default to disabled.
    /// </summary>
    public static OpenInferenceTraceConfig Create(
        Func<string, string?>? getEnvironmentVariable = null)
    {
        var environment = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        var configuredValue = environment(CaptureAiContentEnvironmentVariable);
        if (configuredValue is null)
        {
            return Disabled;
        }

        if (configuredValue.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return Enabled;
        }

        if (configuredValue.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return Disabled;
        }

        throw new InvalidOperationException(
            $"Environment variable '{CaptureAiContentEnvironmentVariable}' must be 'true' or 'false'.");
    }

    internal PrivacyAction GetPrivacyAction(string key)
    {
        if (CaptureAiContent)
        {
            return PrivacyAction.Keep;
        }

        if (key == OpenInferenceAttributes.InputValue)
        {
            return PrivacyAction.Redact;
        }

        if (key == OpenInferenceAttributes.InputMimeType)
        {
            return PrivacyAction.Omit;
        }

        if (key == OpenInferenceAttributes.OutputValue)
        {
            return PrivacyAction.Redact;
        }

        if (key == OpenInferenceAttributes.OutputMimeType)
        {
            return PrivacyAction.Omit;
        }

        return PrivacyAction.Keep;
    }
}

internal enum PrivacyAction
{
    Keep,
    Omit,
    Redact
}

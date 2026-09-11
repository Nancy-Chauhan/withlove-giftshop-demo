namespace WithLove.OpenInference;

/// <summary>Resolved content-privacy configuration for WithLove application spans.</summary>
public sealed class OpenInferenceTraceConfig
{
    /// <summary>Gets the sentinel applied to protected input and output values.</summary>
    public const string RedactedValue = "__REDACTED__";

    /// <summary>Gets the OpenInference environment variable that hides input content.</summary>
    public const string HideInputsEnvironmentVariable = "OPENINFERENCE_HIDE_INPUTS";

    /// <summary>Gets the OpenInference environment variable that hides output content.</summary>
    public const string HideOutputsEnvironmentVariable = "OPENINFERENCE_HIDE_OUTPUTS";

    /// <summary>Gets the application-level environment variable that authorizes AI content capture.</summary>
    public const string CaptureAiContentEnvironmentVariable = "Telemetry__CaptureAiContent";

    /// <summary>Gets configuration resolved from the current process environment.</summary>
    public static OpenInferenceTraceConfig Default { get; } = Create();

    /// <summary>Gets whether input payloads are hidden.</summary>
    public bool HideInputs { get; }

    /// <summary>Gets whether output payloads are hidden.</summary>
    public bool HideOutputs { get; }

    private OpenInferenceTraceConfig(OpenInferenceOptions options, Func<string, string?> environment)
    {
        var applicationCapture = ResolveApplicationCapture(environment);
        if (applicationCapture is not true)
        {
            HideInputs = true;
            HideOutputs = true;
            return;
        }

        HideInputs = Resolve(
            options.HideInputs,
            HideInputsEnvironmentVariable,
            defaultValue: false,
            environment);
        HideOutputs = Resolve(
            options.HideOutputs,
            HideOutputsEnvironmentVariable,
            defaultValue: false,
            environment);
    }

    /// <summary>
    /// Creates an immutable configuration. Application capture must first be authorized; explicit
    /// options then override the corresponding OpenInference hide environment variables.
    /// </summary>
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

    private static bool? ResolveApplicationCapture(Func<string, string?> environment)
    {
        var configuredValue = environment(CaptureAiContentEnvironmentVariable);
        if (configuredValue is null)
            return null;
        if (configuredValue.Equals("true", StringComparison.OrdinalIgnoreCase))
            return true;
        if (configuredValue.Equals("false", StringComparison.OrdinalIgnoreCase))
            return false;

        throw new InvalidOperationException(
            $"Environment variable '{CaptureAiContentEnvironmentVariable}' must be 'true' or 'false'.");
    }
}

internal enum PrivacyAction
{
    Keep,
    Omit,
    Redact
}

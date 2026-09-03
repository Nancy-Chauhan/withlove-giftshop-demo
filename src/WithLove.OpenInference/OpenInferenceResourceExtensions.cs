using OpenTelemetry.Resources;

namespace WithLove.OpenInference;

/// <summary>OpenInference resource-level conventions.</summary>
public static class OpenInferenceResourceExtensions
{
    /// <summary>Adds the OpenInference project name as an OpenTelemetry resource attribute.</summary>
    /// <remarks>
    /// Resource-level project naming applies to every span produced by the resulting resource and
    /// should be configured once when building the tracer provider.
    /// </remarks>
    /// <param name="builder">The OpenTelemetry resource builder to configure.</param>
    /// <param name="projectName">The Phoenix/OpenInference project name associated with exported spans.</param>
    /// <returns>The supplied resource builder for further configuration.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="projectName"/> is empty or whitespace.</exception>
    public static ResourceBuilder AddOpenInferenceProjectName(this ResourceBuilder builder, string projectName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);

        return builder.AddAttributes(
        [
            new KeyValuePair<string, object>(OpenInferenceAttributes.OpenInferenceProjectName, projectName)
        ]);
    }
}

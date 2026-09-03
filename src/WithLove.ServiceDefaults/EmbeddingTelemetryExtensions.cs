using Microsoft.Extensions.AI;

namespace Microsoft.Extensions.Hosting;

/// <summary>Creates MEAI embedding pipelines that emit structural telemetry without input content.</summary>
public static class EmbeddingTelemetryExtensions
{
    /// <summary>
    /// Wraps an embedding generator with MEAI OpenTelemetry instrumentation on the supplied source.
    /// Input and output content remain disabled even if the process-wide MEAI capture environment
    /// variable is enabled.
    /// </summary>
    public static IEmbeddingGenerator<TInput, TEmbedding> WithOpenTelemetryInstrumentation<TInput, TEmbedding>(
        this IEmbeddingGenerator<TInput, TEmbedding> generator,
        string sourceName)
        where TEmbedding : Embedding
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        return generator.AsBuilder()
            .UseOpenTelemetry(
                sourceName: sourceName,
                configure: static telemetry => telemetry.EnableSensitiveData = false)
            .Build();
    }
}

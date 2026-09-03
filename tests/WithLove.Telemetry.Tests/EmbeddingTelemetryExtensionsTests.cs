using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;

namespace WithLove.Telemetry.Tests;

public class EmbeddingTelemetryExtensionsTests
{
    [Theory]
    [InlineData("productsApi")]
    [InlineData("workflowServer")]
    public async Task InstrumentedGenerator_EmitsMeaiEmbeddingSpanWithoutSensitiveInput(
        string sourceName)
    {
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);
        using var generator = new StubEmbeddingGenerator()
            .WithOpenTelemetryInstrumentation(sourceName);

        await generator.GenerateAsync(
            ["private-embedding-input"],
            new EmbeddingGenerationOptions { ModelId = "demo-embedding" });

        var activity = stopped.Should().ContainSingle().Subject;
        activity.Source.Name.Should().Be(sourceName);
        activity.GetTagItem("gen_ai.operation.name").Should().Be("embeddings");
        activity.GetTagItem("gen_ai.request.model").Should().Be("demo-embedding");
        var emittedText = string.Join(
            '\n',
            activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}")
                .Concat(activity.Events.Select(eventItem => eventItem.ToString())));
        emittedText.Should().NotContain("private-embedding-input");
    }

    private sealed class StubEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public EmbeddingGeneratorMetadata Metadata { get; } =
            new("test-provider", null, "demo-embedding", 1);

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                values.Select(_ => new Embedding<float>(new[] { 0.25f })).ToList()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

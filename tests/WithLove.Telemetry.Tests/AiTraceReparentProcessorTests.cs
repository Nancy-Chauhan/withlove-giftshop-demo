using System.Diagnostics;
using OpenTelemetry;
using WithLove.ServiceDefaults.Telemetry;

namespace WithLove.Telemetry.Tests;

public class AiTraceReparentProcessorTests
{
    [Fact]
    public void ReparentsAiSpanWithInfraParentOntoRoot()
    {
        using var scope = new ListenerScope();
        var processor = new AiTraceReparentProcessor();

        using var root = scope.Start("chat.turn", ("openinference.span.kind", "CHAIN"));
        processor.OnStart(root!); // root publishes its id as the reparent anchor

        using var infra = scope.Start("RunActivity:GetChatStep"); // no AI tags
        using var llm = scope.Start("chat", ("gen_ai.operation.name", "chat"));

        llm!.ParentSpanId.Should().Be(infra!.SpanId); // before: nested under the Temporal span
        processor.OnEnd(llm);

        // The reflection rewrite must land, or this fails on the current runtime.
        llm.ParentSpanId.Should().Be(root!.SpanId);
    }

    [Fact]
    public void KeepsNaturalNestingUnderAnAiParent()
    {
        using var scope = new ListenerScope();
        var processor = new AiTraceReparentProcessor();

        using var root = scope.Start("chat.turn", ("openinference.span.kind", "CHAIN"));
        processor.OnStart(root!);

        using var retriever = scope.Start("product.search", ("openinference.span.kind", "RETRIEVER"));
        using var embedding = scope.Start("embeddings", ("gen_ai.operation.name", "embeddings"));

        processor.OnEnd(embedding!);

        // EMBEDDING's parent is already an AI span (RETRIEVER), so it must not be pulled up to root.
        embedding!.ParentSpanId.Should().Be(retriever!.SpanId);
    }

    [Fact]
    public void LeavesTheRootUntouched()
    {
        using var scope = new ListenerScope();
        var processor = new AiTraceReparentProcessor();

        using var root = scope.Start("chat.turn", ("openinference.span.kind", "CHAIN"));
        var originalParent = root!.ParentSpanId;

        processor.OnStart(root);
        processor.OnEnd(root);

        root.ParentSpanId.Should().Be(originalParent);
    }

    private sealed class ListenerScope : IDisposable
    {
        private readonly ActivitySource source = new("withlove-test-reparent");
        private readonly ActivityListener listener;

        public ListenerScope()
        {
            Baggage.Current = default; // avoid anchor leakage between tests
            listener = new ActivityListener
            {
                ShouldListenTo = candidate => ReferenceEquals(candidate, source),
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                    ActivitySamplingResult.AllDataAndRecorded,
            };
            ActivitySource.AddActivityListener(listener);
        }

        public Activity? Start(string name, params (string Key, object? Value)[] tags)
        {
            var activity = source.StartActivity(name); // parents to Activity.Current
            foreach (var (key, value) in tags)
                activity?.SetTag(key, value);
            return activity;
        }

        public void Dispose()
        {
            listener.Dispose();
            source.Dispose();
        }
    }
}

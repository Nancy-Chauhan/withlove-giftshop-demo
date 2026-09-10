using System.Diagnostics;
using WithLove.OpenInference;
using WithLove.OpenInference.Spans;

namespace WithLove.Telemetry.Tests;

public class OpenInferenceScopeTests
{
    private static readonly OpenInferenceTraceConfig VisibleContent = OpenInferenceTraceConfig.Create(
        new OpenInferenceOptions { HideInputs = false, HideOutputs = false },
        static _ => null);

    [Fact]
    public void Chain_HidingMode_ExportsContextAndRedactsContent()
    {
        using var source = new ActivitySource("withlove-test-chain-private");
        Activity? stopped = null;
        using var listener = Listen(source, activity => stopped = activity);
        using var context = OpenInferenceContextScope.Push(new()
        {
            SessionId = "safe-session",
            UserId = "safe-user",
            Tags = ["withlove", "chat"],
        });
        var privacy = OpenInferenceTraceConfig.Create(new OpenInferenceOptions
        {
            HideInputs = true,
            HideOutputs = true,
        });

        using (var chain = source.StartChain("chat.turn", "raw prompt", privacy))
        {
            chain.Complete("raw completion");
        }

        stopped.Should().NotBeNull();
        stopped!.GetTagItem(OpenInferenceAttributes.OpenInferenceSpanKind).Should().Be("CHAIN");
        stopped.GetTagItem(OpenInferenceAttributes.SessionId).Should().Be("safe-session");
        stopped.GetTagItem(OpenInferenceAttributes.UserId).Should().Be("safe-user");
        stopped.GetTagItem(OpenInferenceAttributes.TagTags).Should().BeEquivalentTo(new[] { "withlove", "chat" });
        stopped.GetTagItem(OpenInferenceAttributes.InputValue).Should().Be(OpenInferenceTraceConfig.RedactedValue);
        stopped.GetTagItem(OpenInferenceAttributes.InputMimeType).Should().BeNull();
        stopped.GetTagItem(OpenInferenceAttributes.OutputValue).Should().Be(OpenInferenceTraceConfig.RedactedValue);
        stopped.GetTagItem(OpenInferenceAttributes.OutputMimeType).Should().BeNull();
    }

    [Fact]
    public void Chain_ApplicationCaptureDenialRedactsDespiteLegacyAndCodeOptIns()
    {
        using var source = new ActivitySource("withlove-test-chain-master-private");
        Activity? stopped = null;
        using var listener = Listen(source, activity => stopped = activity);
        var privacy = OpenInferenceTraceConfig.Create(
            new OpenInferenceOptions { HideInputs = false, HideOutputs = false },
            name => name switch
            {
                OpenInferenceTraceConfig.CaptureAiContentEnvironmentVariable => "false",
                OpenInferenceTraceConfig.CaptureMessageContentEnvironmentVariable => "true",
                _ => null,
            });

        using (var chain = source.StartChain("chat.turn", "raw prompt", privacy))
        {
            chain.Complete("raw completion");
        }

        stopped!.GetTagItem(OpenInferenceAttributes.InputValue)
            .Should().Be(OpenInferenceTraceConfig.RedactedValue);
        stopped.GetTagItem(OpenInferenceAttributes.InputMimeType).Should().BeNull();
        stopped.GetTagItem(OpenInferenceAttributes.OutputValue)
            .Should().Be(OpenInferenceTraceConfig.RedactedValue);
        stopped.GetTagItem(OpenInferenceAttributes.OutputMimeType).Should().BeNull();
    }

    [Fact]
    public void Chain_ExportsPlainTextInputAndCompletion()
    {
        using var source = new ActivitySource("withlove-test-chain-content");
        Activity? stopped = null;
        using var listener = Listen(source, activity => stopped = activity);

        using (var chain = source.StartChain("chat.turn", "hello", VisibleContent))
        {
            chain.Complete("world");
        }

        stopped!.GetTagItem(OpenInferenceAttributes.InputValue).Should().Be("hello");
        stopped.GetTagItem(OpenInferenceAttributes.InputMimeType).Should().Be("text/plain");
        stopped.GetTagItem(OpenInferenceAttributes.OutputValue).Should().Be("world");
        stopped.GetTagItem(OpenInferenceAttributes.OutputMimeType).Should().Be("text/plain");
    }

    [Fact]
    public void Chain_PreservesAmbientDistributedParentage()
    {
        using var source = new ActivitySource("withlove-test-parentage");
        var stopped = new List<Activity>();
        using var listener = Listen(source, stopped.Add);
        using var parent = source.StartActivity("http.request", ActivityKind.Server);

        using (source.StartChain("chat.turn")) { }

        var chain = stopped.Single(activity => activity.DisplayName == "chat.turn");
        chain.ParentSpanId.Should().Be(parent!.SpanId);
        chain.TraceId.Should().Be(parent.TraceId);
    }

    [Fact]
    public void Chain_WithoutListener_RemainsUsableAndUsesAmbientTrace()
    {
        using var parent = new Activity("ambient").Start();
        using var source = new ActivitySource("withlove-test-no-listener");

        using var chain = source.StartChain("chat.turn");
        chain.Complete("done");

        chain.Activity.Should().BeNull();
        chain.IsRecording.Should().BeFalse();
        chain.TraceId.Should().Be(parent.TraceId.ToString());
    }

    [Fact]
    public void Chain_RejectsInvalidLifecycleCalls()
    {
        using var source = new ActivitySource("withlove-test-chain-lifecycle");
        using var chain = source.StartChain("chat.turn");
        chain.Complete("first");

        chain.Invoking(scope => scope.Complete("second"))
            .Should().Throw<InvalidOperationException>();
        chain.Dispose();
        chain.Invoking(scope => scope.Fail(new InvalidOperationException("late")))
            .Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Chain_Fail_RecordsErrorStatusAndExceptionEvent()
    {
        using var source = new ActivitySource("withlove-test-chain-failure");
        Activity? stopped = null;
        using var listener = Listen(source, activity => stopped = activity);

        using (var chain = source.StartChain("chat.turn"))
        {
            chain.Fail(new InvalidOperationException("boom"), escaped: true);
        }

        stopped!.Status.Should().Be(ActivityStatusCode.Error);
        stopped.StatusDescription.Should().Be("boom");
        var exceptionEvent = stopped.Events.Should().ContainSingle(activityEvent => activityEvent.Name == "exception").Subject;
        exceptionEvent.Tags.Single(tag => tag.Key == OpenInferenceAttributes.ExceptionEscaped).Value.Should().Be(true);
    }

    [Fact]
    public void Starters_RejectBlankNamesEvenWithoutListeners()
    {
        using var source = new ActivitySource("withlove-test-invalid-name");

        source.Invoking(candidate => candidate.StartChain(" "))
            .Should().Throw<ArgumentException>();
        source.Invoking(candidate => candidate.StartRetriever(""))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Retriever_RecordsOnlyTheDocumentFieldsTheApplicationSupplies()
    {
        using var source = new ActivitySource("withlove-test-retriever-private");
        Activity? stopped = null;
        using var listener = Listen(source, activity => stopped = activity);
        var privacy = OpenInferenceTraceConfig.Create(new OpenInferenceOptions { HideInputs = true });

        using (var retriever = source.StartRetriever("product.search", "private query", privacy))
        {
            retriever.Record([new RetrievedDocument(id: "42")]);
        }

        stopped!.GetTagItem(OpenInferenceAttributes.OpenInferenceSpanKind).Should().Be("RETRIEVER");
        stopped.GetTagItem("retrieval.documents.0.document.id").Should().Be("42");
        stopped.GetTagItem("retrieval.documents.0.document.score").Should().BeNull();
        stopped.GetTagItem("retrieval.documents.0.document.content").Should().BeNull();
        stopped.GetTagItem(OpenInferenceAttributes.InputValue).Should().Be(OpenInferenceTraceConfig.RedactedValue);
    }

    [Fact]
    public void Retriever_FlattensCompleteDocumentsOntoContiguousIndices()
    {
        using var source = new ActivitySource("withlove-test-retriever-documents");
        Activity? stopped = null;
        using var listener = Listen(source, activity => stopped = activity);
        var metadata = OpenInferenceJson.Raw("{\"source\":\"catalog\"}");

        using (var retriever = source.StartRetriever("product.search", traceConfig: VisibleContent))
        {
            retriever.Record([
                new RetrievedDocument(id: "42", content: "Gift", score: 0.75, metadata: metadata),
                new RetrievedDocument(id: "84")
            ]);
        }

        stopped!.GetTagItem("retrieval.documents.0.document.id").Should().Be("42");
        stopped.GetTagItem("retrieval.documents.0.document.content").Should().Be("Gift");
        stopped.GetTagItem("retrieval.documents.0.document.score").Should().Be(0.75);
        stopped.GetTagItem("retrieval.documents.0.document.metadata").Should().Be("{\"source\":\"catalog\"}");
        stopped.GetTagItem("retrieval.documents.1.document.id").Should().Be("84");
        stopped.Tags.Should().NotContain(tag => tag.Key.StartsWith("retrieval.documents.2.", StringComparison.Ordinal));
    }

    [Fact]
    public void Retriever_Complete_ProjectsTheSameDocumentsAsJson()
    {
        using var source = new ActivitySource("withlove-test-retriever-completion");
        Activity? stopped = null;
        using var listener = Listen(source, activity => stopped = activity);

        using (var retriever = source.StartRetriever("product.search", traceConfig: VisibleContent))
        {
            retriever.Complete([
                new RetrievedDocument(id: "42", score: 0.75),
                new RetrievedDocument(content: "Gift")
            ]);
        }

        stopped!.GetTagItem(OpenInferenceAttributes.OutputValue)
            .Should().Be("[{\"id\":\"42\",\"score\":0.75},{\"content\":\"Gift\"}]");
        stopped.GetTagItem(OpenInferenceAttributes.OutputMimeType).Should().Be("application/json");
        stopped.GetTagItem("retrieval.documents.0.document.id").Should().Be("42");
        stopped.GetTagItem("retrieval.documents.1.document.content").Should().Be("Gift");
    }

    [Fact]
    public void Retriever_RejectsInvalidOrRepeatedDocumentRecordingBeforeMutation()
    {
        using var source = new ActivitySource("withlove-test-retriever-validation");
        Activity? stopped = null;
        using var listener = Listen(source, activity => stopped = activity);
        using var retriever = source.StartRetriever("product.search");

        retriever.Invoking(scope => scope.Record([new RetrievedDocument(id: "42"), null!]))
            .Should().Throw<ArgumentException>();
        retriever.Record([new RetrievedDocument(id: "42")]);
        retriever.Invoking(scope => scope.Record([new RetrievedDocument(id: "84")]))
            .Should().Throw<InvalidOperationException>();
        retriever.Dispose();
        retriever.Invoking(scope => scope.Record([new RetrievedDocument(id: "126")]))
            .Should().Throw<ObjectDisposedException>();

        stopped!.GetTagItem("retrieval.documents.0.document.id").Should().Be("42");
        stopped.GetTagItem("retrieval.documents.1.document.id").Should().BeNull();
    }

    [Fact]
    public void Retriever_EmptyResult_EmitsNoDocumentAttributes()
    {
        using var source = new ActivitySource("withlove-test-retriever-empty");
        Activity? stopped = null;
        using var listener = Listen(source, activity => stopped = activity);

        using (var retriever = source.StartRetriever("product.search"))
        {
            retriever.Record([]);
        }

        stopped!.Tags.Should().NotContain(tag =>
            tag.Key.StartsWith("retrieval.documents.", StringComparison.Ordinal));
    }

    [Fact]
    public void RetrievedDocument_RejectsEmptyNonFiniteAndUninitializedMetadata()
    {
        var empty = () => new RetrievedDocument();
        var nan = () => new RetrievedDocument(score: double.NaN);
        var infinity = () => new RetrievedDocument(score: double.PositiveInfinity);
        var uninitializedMetadata = () => new RetrievedDocument(metadata: default(OpenInferenceJson));

        empty.Should().Throw<ArgumentException>();
        nan.Should().Throw<ArgumentException>();
        infinity.Should().Throw<ArgumentException>();
        uninitializedMetadata.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void OpenInferenceJson_ValidatesRawDocumentsAndPreservesTheirBytes()
    {
        var valid = OpenInferenceJson.Raw("[1,{\"value\":true}]");
        var invalid = () => OpenInferenceJson.Raw("{not-json}");
        var uninitialized = default(OpenInferenceJson);
        var readUninitialized = () => uninitialized.Value;

        valid.Value.Should().Be("[1,{\"value\":true}]");
        valid.IsInitialized.Should().BeTrue();
        invalid.Should().Throw<System.Text.Json.JsonException>();
        uninitialized.IsInitialized.Should().BeFalse();
        readUninitialized.Should().Throw<InvalidOperationException>();
    }

    private static ActivityListener Listen(ActivitySource source, Action<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = candidate => ReferenceEquals(candidate, source),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}

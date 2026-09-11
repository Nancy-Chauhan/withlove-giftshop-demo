using System.Diagnostics;
using WithLove.OpenInference;
using WithLove.OpenInference.Spans;

namespace WithLove.Telemetry.Tests;

public class OpenInferencePrivacyAndContextTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    public void ApplicationCapturePolicy_DefaultsOffAndAcceptsBooleanValues(
        string? configuredValue,
        bool expectedCapture)
    {
        string? Environment(string name) =>
            name == OpenInferenceTraceConfig.CaptureAiContentEnvironmentVariable
                ? configuredValue
                : null;

        var configuration = OpenInferenceTraceConfig.Create(Environment);

        configuration.CaptureAiContent.Should().Be(expectedCapture);
    }

    [Fact]
    public void ApplicationCaptureDenial_OverridesStandardOptIn()
    {
        static string? Environment(string name) => name switch
        {
            OpenInferenceTraceConfig.CaptureAiContentEnvironmentVariable => "false",
            "OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT" => "true",
            _ => null
        };

        var configuration = OpenInferenceTraceConfig.Create(Environment);

        configuration.CaptureAiContent.Should().BeFalse();
    }

    [Fact]
    public void MissingApplicationPolicy_DefaultsToDisabledAndIgnoresStandardOptIn()
    {
        static string? Environment(string name) =>
            name == "OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT" ? "true" : null;

        var standardOptIn = OpenInferenceTraceConfig.Create(Environment);

        standardOptIn.CaptureAiContent.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("yes")]
    [InlineData(" true ")]
    public void ApplicationCapturePolicy_RejectsMalformedValues(string configuredValue)
    {
        var action = () => OpenInferenceTraceConfig.Create(
            name =>
                name == OpenInferenceTraceConfig.CaptureAiContentEnvironmentVariable
                    ? configuredValue
                    : null);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*Telemetry__CaptureAiContent*true*false*");
    }

    [Fact]
    public void ContextScopes_SnapshotTagsAndRestoreTheOuterContext()
    {
        using var source = new ActivitySource("withlove-test-context-nesting");
        var stopped = new List<Activity>();
        using var listener = Listen(source, stopped.Add);
        var outerTags = new[] { "outer" };

        using (OpenInferenceContextScope.Push(new()
        {
            SessionId = "outer-session",
            UserId = "outer-user",
            Tags = outerTags
        }))
        {
            outerTags[0] = "mutated";
            using (OpenInferenceContextScope.Push(new()
            {
                SessionId = "inner-session",
                UserId = "inner-user",
                Tags = ["inner"]
            }))
            {
                using (source.StartChain("inner")) { }
            }

            using (source.StartChain("outer")) { }
        }

        var inner = stopped.Single(activity => activity.DisplayName == "inner");
        inner.GetTagItem(OpenInferenceAttributes.SessionId).Should().Be("inner-session");
        inner.GetTagItem(OpenInferenceAttributes.UserId).Should().Be("inner-user");
        inner.GetTagItem(OpenInferenceAttributes.TagTags).Should().BeEquivalentTo(new[] { "inner" });

        var outer = stopped.Single(activity => activity.DisplayName == "outer");
        outer.GetTagItem(OpenInferenceAttributes.SessionId).Should().Be("outer-session");
        outer.GetTagItem(OpenInferenceAttributes.UserId).Should().Be("outer-user");
        outer.GetTagItem(OpenInferenceAttributes.TagTags).Should().BeEquivalentTo(new[] { "outer" });
    }

    [Fact]
    public void ContextScopes_RejectOutOfOrderDisposalAndRemainRecoverable()
    {
        var outer = OpenInferenceContextScope.Push(new() { SessionId = "outer" });
        var inner = OpenInferenceContextScope.Push(new() { SessionId = "inner" });

        try
        {
            outer.Invoking(scope => scope.Dispose()).Should().Throw<InvalidOperationException>();
        }
        finally
        {
            inner.Dispose();
            outer.Dispose();
        }
    }

    [Fact]
    public void EmptyAmbientIdentifiers_AreNotExported()
    {
        using var source = new ActivitySource("withlove-test-context-empty");
        Activity? stopped = null;
        using var listener = Listen(source, activity => stopped = activity);
        using var context = OpenInferenceContextScope.Push(new()
        {
            SessionId = " ",
            UserId = "",
            Tags = []
        });

        using (source.StartChain("chat.turn")) { }

        stopped!.GetTagItem(OpenInferenceAttributes.SessionId).Should().BeNull();
        stopped.GetTagItem(OpenInferenceAttributes.UserId).Should().BeNull();
        stopped.GetTagItem(OpenInferenceAttributes.TagTags).Should().BeEquivalentTo(Array.Empty<string>());
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

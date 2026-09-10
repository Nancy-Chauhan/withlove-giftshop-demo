using System.Diagnostics;
using WithLove.OpenInference;
using WithLove.OpenInference.Spans;

namespace WithLove.Telemetry.Tests;

public class OpenInferencePrivacyAndContextTests
{
    [Theory]
    [InlineData("false", "false", "false", true, true)]
    [InlineData("true", "false", "false", false, false)]
    [InlineData("true", "true", "false", true, false)]
    [InlineData("true", "false", "true", false, true)]
    [InlineData("true", "true", "true", true, true)]
    public void ApplicationCapturePolicy_AppliesMasterAuthorizationThenDirectionalRestrictions(
        string captureAiContent,
        string hideInputs,
        string hideOutputs,
        bool expectedHideInputs,
        bool expectedHideOutputs)
    {
        string? Environment(string name) => name switch
        {
            OpenInferenceTraceConfig.CaptureAiContentEnvironmentVariable => captureAiContent,
            OpenInferenceTraceConfig.HideInputsEnvironmentVariable => hideInputs,
            OpenInferenceTraceConfig.HideOutputsEnvironmentVariable => hideOutputs,
            _ => null
        };

        var configuration = OpenInferenceTraceConfig.Create(getEnvironmentVariable: Environment);

        configuration.HideInputs.Should().Be(expectedHideInputs);
        configuration.HideOutputs.Should().Be(expectedHideOutputs);
    }

    [Fact]
    public void ApplicationCaptureDenial_OverridesLegacyOptInAndExplicitUnhideOptions()
    {
        static string? Environment(string name) => name switch
        {
            OpenInferenceTraceConfig.CaptureAiContentEnvironmentVariable => "false",
            OpenInferenceTraceConfig.CaptureMessageContentEnvironmentVariable => "true",
            _ => null
        };

        var configuration = OpenInferenceTraceConfig.Create(
            new OpenInferenceOptions { HideInputs = false, HideOutputs = false },
            Environment);

        configuration.HideInputs.Should().BeTrue();
        configuration.HideOutputs.Should().BeTrue();
    }

    [Fact]
    public void MissingApplicationPolicy_RetainsLegacyAndExplicitOptionCompatibility()
    {
        static string? Environment(string name) =>
            name == OpenInferenceTraceConfig.CaptureMessageContentEnvironmentVariable ? "true" : null;

        var legacyCapture = OpenInferenceTraceConfig.Create(getEnvironmentVariable: Environment);
        var explicitDirections = OpenInferenceTraceConfig.Create(
            new OpenInferenceOptions { HideInputs = false, HideOutputs = true },
            static _ => null);

        legacyCapture.HideInputs.Should().BeFalse();
        legacyCapture.HideOutputs.Should().BeFalse();
        explicitDirections.HideInputs.Should().BeFalse();
        explicitDirections.HideOutputs.Should().BeTrue();
    }

    [Fact]
    public void ApplicationCapturePolicy_RejectsMalformedValue()
    {
        var action = () => OpenInferenceTraceConfig.Create(
            getEnvironmentVariable: name =>
                name == OpenInferenceTraceConfig.CaptureAiContentEnvironmentVariable ? "yes" : null);

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

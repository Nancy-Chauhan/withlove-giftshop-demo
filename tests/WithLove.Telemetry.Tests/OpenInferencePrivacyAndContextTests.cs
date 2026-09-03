using System.Diagnostics;
using WithLove.OpenInference;
using WithLove.OpenInference.Spans;

namespace WithLove.Telemetry.Tests;

public class OpenInferencePrivacyAndContextTests
{
    [Fact]
    public void PrivacyConfiguration_UsesExplicitValuesThenEnvironmentThenDefaults()
    {
        static string? Environment(string name) => name switch
        {
            OpenInferenceTraceConfig.HideInputsEnvironmentVariable => "true",
            OpenInferenceTraceConfig.HideOutputsEnvironmentVariable => "not-a-bool",
            _ => null
        };

        static string? CaptureEnvironment(string name) => name switch
        {
            OpenInferenceTraceConfig.CaptureMessageContentEnvironmentVariable => "true",
            _ => null
        };

        var environmentOnly = OpenInferenceTraceConfig.Create(getEnvironmentVariable: Environment);
        var captureEnabled = OpenInferenceTraceConfig.Create(
            getEnvironmentVariable: CaptureEnvironment);
        var explicitOverride = OpenInferenceTraceConfig.Create(
            new OpenInferenceOptions { HideInputs = false, HideOutputs = true },
            Environment);

        environmentOnly.HideInputs.Should().BeTrue();
        environmentOnly.HideOutputs.Should().BeTrue();
        captureEnabled.HideInputs.Should().BeFalse();
        captureEnabled.HideOutputs.Should().BeFalse();
        explicitOverride.HideInputs.Should().BeFalse();
        explicitOverride.HideOutputs.Should().BeTrue();
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

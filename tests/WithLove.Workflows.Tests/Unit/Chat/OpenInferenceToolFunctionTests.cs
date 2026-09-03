using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using WithLove.OpenInference;
using WithLove.WorkflowServer.Telemetry;

namespace WithLove.Workflows.Tests.Unit.Chat;

public class OpenInferenceToolFunctionTests
{
    [Fact]
    public async Task InvokeAsync_EnrichesTheExistingToolActivityWithoutCreatingAnotherSpan()
    {
        using var source = new ActivitySource("withlove-test-openinference-tool");
        var stopped = new List<Activity>();
        using var listener = Listen(source, stopped.Add);
        using var activity = StartToolActivity(source);
        var inner = AIFunctionFactory.Create(
            (string query) => $"Found {query}",
            "search_products",
            "Search the gift catalog.");
        var function = new OpenInferenceToolFunction(
            inner,
            "call-1",
            "safe-session",
            "operation-1",
            VisibleContent);

        var result = await function.InvokeAsync(new AIFunctionArguments
        {
            ["query"] = "keepsake",
        });

        result.Should().BeOfType<JsonElement>().Which.GetString().Should().Be("Found keepsake");
        activity.Should().NotBeNull();
        activity!.GetTagItem(OpenInferenceAttributes.OpenInferenceSpanKind).Should().Be("TOOL");
        activity.GetTagItem(OpenInferenceAttributes.ToolName).Should().Be("search_products");
        activity.GetTagItem(OpenInferenceAttributes.ToolDescription)
            .Should().Be("Search the gift catalog.");
        activity.GetTagItem(OpenInferenceAttributes.ToolId).Should().Be("call-1");
        activity.GetTagItem(OpenInferenceAttributes.SessionId).Should().Be("safe-session");
        activity.GetTagItem("chat.operation_id").Should().Be("operation-1");
        activity.GetTagItem(OpenInferenceAttributes.InputValue)
            .Should().Be("{\"query\":\"keepsake\"}");
        activity.GetTagItem(OpenInferenceAttributes.InputMimeType).Should().Be("application/json");
        activity.GetTagItem(OpenInferenceAttributes.OutputValue).Should().Be("Found keepsake");
        activity.GetTagItem(OpenInferenceAttributes.OutputMimeType).Should().Be("text/plain");
        activity.Status.Should().Be(ActivityStatusCode.Ok);

        activity.Dispose();
        stopped.Should().ContainSingle().Which.Should().BeSameAs(activity);
    }

    [Fact]
    public async Task InvokeAsync_RedactsPayloadsAndOmitsMimeTypesWhenContentIsHidden()
    {
        using var source = new ActivitySource("withlove-test-openinference-tool-private");
        using var listener = Listen(source, _ => { });
        using var activity = StartToolActivity(source);
        var inner = AIFunctionFactory.Create(
            (string query) => $"Found {query}",
            "search_products",
            "Search the gift catalog.");
        var function = new OpenInferenceToolFunction(
            inner,
            "call-1",
            "safe-session",
            "operation-1",
            OpenInferenceTraceConfig.Create(new OpenInferenceOptions
            {
                HideInputs = true,
                HideOutputs = true,
            }));

        await function.InvokeAsync(new AIFunctionArguments { ["query"] = "private" });

        activity.Should().NotBeNull();
        activity!.GetTagItem(OpenInferenceAttributes.InputValue)
            .Should().Be(OpenInferenceTraceConfig.RedactedValue);
        activity.GetTagItem(OpenInferenceAttributes.InputMimeType).Should().BeNull();
        activity.GetTagItem(OpenInferenceAttributes.OutputValue)
            .Should().Be(OpenInferenceTraceConfig.RedactedValue);
        activity.GetTagItem(OpenInferenceAttributes.OutputMimeType).Should().BeNull();
    }

    [Fact]
    public async Task InvokeAsync_RecordsExceptionAndErrorStatusOnTheExistingToolActivity()
    {
        using var source = new ActivitySource("withlove-test-openinference-tool-error");
        using var listener = Listen(source, _ => { });
        using var activity = StartToolActivity(source);
        var expected = new InvalidOperationException("catalog unavailable");
        var inner = AIFunctionFactory.Create(
            (Func<Task<string>>)(() => Task.FromException<string>(expected)),
            "search_products",
            "Search the gift catalog.");
        var function = new OpenInferenceToolFunction(
            inner,
            "call-1",
            "safe-session",
            "operation-1",
            VisibleContent);

        var action = async () => await function.InvokeAsync();

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("catalog unavailable");
        activity.Should().NotBeNull();
        activity!.Status.Should().Be(ActivityStatusCode.Error);
        var exceptionEvent = activity.Events.Should().ContainSingle(e => e.Name == "exception").Subject;
        exceptionEvent.Tags.Single(tag =>
                tag.Key == OpenInferenceAttributes.ExceptionEscaped)
            .Value.Should().Be(true);
    }

    private static OpenInferenceTraceConfig VisibleContent { get; } =
        OpenInferenceTraceConfig.Create(new OpenInferenceOptions
        {
            HideInputs = false,
            HideOutputs = false,
        });

    private static Activity? StartToolActivity(ActivitySource source) =>
        source.StartActivity(
            "execute_tool search_products",
            ActivityKind.Client,
            default(ActivityContext),
            [new KeyValuePair<string, object?>("gen_ai.operation.name", "execute_tool")]);

    private static ActivityListener Listen(ActivitySource source, Action<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = candidate => ReferenceEquals(candidate, source),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}

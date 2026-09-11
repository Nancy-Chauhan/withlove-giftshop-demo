using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using WithLove.OpenInference;

namespace WithLove.WorkflowServer.Telemetry;

/// <summary>
/// Decorates the durable-AI package's existing function-execution Activity as an OpenInference
/// TOOL span.
/// </summary>
/// <remarks>
/// The decorator intentionally does not create an Activity. The durable chat package owns the
/// execution span, its latency, and its retry attempt; this type adds AI semantics to that same
/// span so telemetry contains one physical tool execution exactly once.
/// </remarks>
internal sealed class OpenInferenceToolFunction(
    AIFunction innerFunction,
    string? toolCallId,
    string? sessionId,
    string? operationId,
    OpenInferenceTraceConfig traceConfig) : DelegatingAIFunction(innerFunction)
{
    private const string ExecuteToolOperation = "execute_tool";
    private const string GenAiOperationNameAttribute = "gen_ai.operation.name";
    private const string OperationIdAttribute = "chat.operation_id";
    private const string ToolSpanKind = "TOOL";
    private const string JsonMimeType = "application/json";
    private const string PlainTextMimeType = "text/plain";

    private readonly OpenInferenceTraceConfig traceConfig =
        traceConfig ?? throw new ArgumentNullException(nameof(traceConfig));
    private readonly JsonSerializerOptions compactJsonOptions = new(innerFunction.JsonSerializerOptions)
    {
        WriteIndented = false,
    };

    /// <inheritdoc />
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var activity = GetCurrentToolActivity();
        EnrichIdentity(activity);
        RecordInput(activity, arguments);

        try
        {
            var result = await base.InvokeCoreAsync(arguments, cancellationToken)
                .ConfigureAwait(false);
            RecordOutput(activity, result);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        catch (Exception exception)
        {
            if (activity is not null)
            {
                activity.SetStatus(ActivityStatusCode.Error, exception.Message);
                if (activity.IsAllDataRequested)
                {
                    activity.AddException(exception, new TagList
                    {
                        { OpenInferenceAttributes.ExceptionEscaped, true }
                    });
                }
            }

            throw;
        }
    }

    private static Activity? GetCurrentToolActivity()
    {
        var activity = Activity.Current;
        return activity?.IsAllDataRequested == true
               && string.Equals(
                   activity.GetTagItem(GenAiOperationNameAttribute) as string,
                   ExecuteToolOperation,
                   StringComparison.Ordinal)
            ? activity
            : null;
    }

    private void EnrichIdentity(Activity? activity)
    {
        activity?.SetTag(
            OpenInferenceAttributes.OpenInferenceSpanKind,
            ToolSpanKind);
        activity?.SetTag(OpenInferenceAttributes.ToolName, Name);

        if (!string.IsNullOrWhiteSpace(Description))
            activity?.SetTag(OpenInferenceAttributes.ToolDescription, Description);
        if (!string.IsNullOrWhiteSpace(toolCallId))
            activity?.SetTag(OpenInferenceAttributes.ToolId, toolCallId);
        if (!string.IsNullOrWhiteSpace(sessionId))
            activity?.SetTag(OpenInferenceAttributes.SessionId, sessionId);
        if (!string.IsNullOrWhiteSpace(operationId))
            activity?.SetTag(OperationIdAttribute, operationId);
    }

    private void RecordInput(Activity? activity, AIFunctionArguments arguments)
    {
        if (activity is null)
            return;

        if (!traceConfig.CaptureAiContent)
        {
            activity.SetTag(OpenInferenceAttributes.InputValue, OpenInferenceTraceConfig.RedactedValue);
            activity.SetTag(OpenInferenceAttributes.InputMimeType, null);
            return;
        }

        var values = (IReadOnlyDictionary<string, object?>)arguments;
        activity.SetTag(
            OpenInferenceAttributes.InputValue,
            JsonSerializer.Serialize(values, compactJsonOptions));
        activity.SetTag(OpenInferenceAttributes.InputMimeType, JsonMimeType);
    }

    private void RecordOutput(Activity? activity, object? result)
    {
        if (activity is null)
            return;

        if (!traceConfig.CaptureAiContent)
        {
            activity.SetTag(OpenInferenceAttributes.OutputValue, OpenInferenceTraceConfig.RedactedValue);
            activity.SetTag(OpenInferenceAttributes.OutputMimeType, null);
            return;
        }

        var text = result switch
        {
            string value => value,
            JsonElement { ValueKind: JsonValueKind.String } stringElement =>
                stringElement.GetString() ?? string.Empty,
            _ => null,
        };
        if (text is not null)
        {
            activity.SetTag(OpenInferenceAttributes.OutputValue, text);
            activity.SetTag(OpenInferenceAttributes.OutputMimeType, PlainTextMimeType);
            return;
        }

        activity.SetTag(
            OpenInferenceAttributes.OutputValue,
            result is JsonElement element
                ? element.GetRawText()
                : JsonSerializer.Serialize(
                    result,
                    result?.GetType() ?? typeof(object),
                    compactJsonOptions));
        activity.SetTag(OpenInferenceAttributes.OutputMimeType, JsonMimeType);
    }
}

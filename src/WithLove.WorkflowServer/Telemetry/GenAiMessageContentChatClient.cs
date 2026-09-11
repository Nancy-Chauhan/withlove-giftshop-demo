using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.AI;
using WithLove.OpenInference;

namespace WithLove.WorkflowServer.Telemetry;

/// <summary>
/// Adds the standard OpenTelemetry GenAI message attributes to the model activity that the
/// durable-chat package already owns.
/// </summary>
/// <remarks>
/// This client deliberately does not start an activity. Starting another activity here would
/// duplicate the model span, including its latency and token accounting. Message content is
/// attached only when the current activity is a recording GenAI chat activity and application
/// content capture is enabled.
/// </remarks>
internal sealed class GenAiMessageContentChatClient(
    IChatClient innerClient,
    OpenInferenceTraceConfig traceConfig) : DelegatingChatClient(innerClient)
{
    private const string OperationNameAttribute = "gen_ai.operation.name";
    private const string InputMessagesAttribute = "gen_ai.input.messages";
    private const string OutputMessagesAttribute = "gen_ai.output.messages";
    private const string SystemInstructionsAttribute = "gen_ai.system_instructions";
    private const string ConversationIdAttribute = "conversation.id";

    private readonly OpenInferenceTraceConfig traceConfig =
        traceConfig ?? throw new ArgumentNullException(nameof(traceConfig));

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var activity = GetCurrentChatActivity();
        RecordSessionIdentity(activity);
        var preparedMessages = RecordInput(activity, messages, options);
        var response = await InnerClient
            .GetResponseAsync(preparedMessages, options, cancellationToken)
            .ConfigureAwait(false);

        RecordOutput(activity, response);
        return response;
    }

    /// <inheritdoc />
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var activity = GetCurrentChatActivity();
        RecordSessionIdentity(activity);
        var preparedMessages = RecordInput(activity, messages, options);
        var updates = InnerClient.GetStreamingResponseAsync(
            preparedMessages,
            options,
            cancellationToken);

        return activity is null || !traceConfig.CaptureAiContent
            ? updates
            : RecordStreamingOutputAsync(activity, updates, cancellationToken);
    }

    private static Activity? GetCurrentChatActivity()
    {
        var activity = Activity.Current;
        return activity?.IsAllDataRequested == true
               && string.Equals(
                   activity.GetTagItem(OperationNameAttribute) as string,
                   "chat",
                   StringComparison.Ordinal)
            ? activity
            : null;
    }

    private static void RecordSessionIdentity(Activity? activity)
    {
        if (activity?.GetTagItem(ConversationIdAttribute) is string { Length: > 0 } sessionId)
            activity.SetTag(OpenInferenceAttributes.SessionId, sessionId);
    }

    private IEnumerable<ChatMessage> RecordInput(
        Activity? activity,
        IEnumerable<ChatMessage> messages,
        ChatOptions? options)
    {
        if (activity is null || !traceConfig.CaptureAiContent)
        {
            return messages;
        }

        var snapshot = messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
        if (!string.IsNullOrWhiteSpace(options?.Instructions))
        {
            activity.SetTag(
                SystemInstructionsAttribute,
                GenAiMessageSerializer.SerializeSystemInstructions(options.Instructions));
        }

        activity.SetTag(InputMessagesAttribute, GenAiMessageSerializer.SerializeMessages(snapshot));
        return snapshot;
    }

    private void RecordOutput(Activity? activity, ChatResponse response)
    {
        if (activity is null || !traceConfig.CaptureAiContent)
        {
            return;
        }

        activity.SetTag(
            OutputMessagesAttribute,
            GenAiMessageSerializer.SerializeMessages(response.Messages, response.FinishReason));
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> RecordStreamingOutputAsync(
        Activity activity,
        IAsyncEnumerable<ChatResponseUpdate> updates,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var capturedUpdates = new List<ChatResponseUpdate>();
        await foreach (var update in updates
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            capturedUpdates.Add(update);
            yield return update;
        }

        var response = capturedUpdates.ToChatResponse();
        activity.SetTag(
            OutputMessagesAttribute,
            GenAiMessageSerializer.SerializeMessages(response.Messages, response.FinishReason));
    }
}

/// <summary>
/// Produces the OpenTelemetry GenAI message-parts JSON representation without reflection for the
/// message and content types used by the gift-shop chat workflow.
/// </summary>
internal static class GenAiMessageSerializer
{
    private static readonly JsonSerializerOptions DynamicValueOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static string SerializeMessages(
        IEnumerable<ChatMessage> messages,
        ChatFinishReason? finishReason = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = CreateWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var message in messages)
            {
                WriteMessage(writer, message, finishReason);
            }
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static string SerializeSystemInstructions(string instructions)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = CreateWriter(buffer))
        {
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("content", instructions);
            writer.WriteEndObject();
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static Utf8JsonWriter CreateWriter(IBufferWriter<byte> output) =>
        new(output, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    private static void WriteMessage(
        Utf8JsonWriter writer,
        ChatMessage message,
        ChatFinishReason? finishReason)
    {
        writer.WriteStartObject();
        writer.WriteString("role", ToRole(message.Role));
        if (!string.IsNullOrWhiteSpace(message.AuthorName))
        {
            writer.WriteString("name", message.AuthorName);
        }

        writer.WriteStartArray("parts");
        foreach (var content in message.Contents)
        {
            WriteContent(writer, content);
        }
        writer.WriteEndArray();

        var normalizedFinishReason = NormalizeFinishReason(finishReason);
        if (normalizedFinishReason is not null)
        {
            writer.WriteString("finish_reason", normalizedFinishReason);
        }

        writer.WriteEndObject();
    }

    private static void WriteContent(Utf8JsonWriter writer, AIContent content)
    {
        switch (content)
        {
            case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                WriteSimplePart(writer, "text", text.Text);
                break;

            case TextReasoningContent reasoning when !string.IsNullOrWhiteSpace(reasoning.Text):
                WriteSimplePart(writer, "reasoning", reasoning.Text);
                break;

            case FunctionCallContent functionCall:
                writer.WriteStartObject();
                writer.WriteString("type", "tool_call");
                writer.WriteString("id", functionCall.CallId);
                writer.WriteString("name", functionCall.Name);
                writer.WritePropertyName("arguments");
                WriteDynamicValue(writer, functionCall.Arguments, emptyObjectOnFailure: true);
                writer.WriteEndObject();
                break;

            case FunctionResultContent functionResult:
                writer.WriteStartObject();
                writer.WriteString("type", "tool_call_response");
                writer.WriteString("id", functionResult.CallId);
                writer.WritePropertyName("response");
                WriteDynamicValue(writer, functionResult.Result, emptyObjectOnFailure: false);
                writer.WriteEndObject();
                break;

            default:
                writer.WriteStartObject();
                writer.WriteString("type", content.GetType().FullName);
                writer.WriteStartObject("content");
                writer.WriteEndObject();
                writer.WriteEndObject();
                break;
        }
    }

    private static void WriteSimplePart(Utf8JsonWriter writer, string type, string content)
    {
        writer.WriteStartObject();
        writer.WriteString("type", type);
        writer.WriteString("content", content);
        writer.WriteEndObject();
    }

    private static void WriteDynamicValue(
        Utf8JsonWriter writer,
        object? value,
        bool emptyObjectOnFailure)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        try
        {
            JsonSerializer.SerializeToElement(value, value.GetType(), DynamicValueOptions)
                .WriteTo(writer);
        }
        catch (NotSupportedException)
        {
            WriteFallbackValue(writer, emptyObjectOnFailure);
        }
        catch (JsonException)
        {
            WriteFallbackValue(writer, emptyObjectOnFailure);
        }
    }

    private static void WriteFallbackValue(Utf8JsonWriter writer, bool emptyObject)
    {
        if (emptyObject)
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNullValue();
        }
    }

    private static string ToRole(ChatRole role) =>
        role == ChatRole.Assistant
            ? "assistant"
            : role == ChatRole.Tool
                ? "tool"
                : role == ChatRole.System || role == new ChatRole("developer")
                    ? "system"
                    : "user";

    private static string? NormalizeFinishReason(ChatFinishReason? finishReason)
    {
        if (finishReason is null)
        {
            return null;
        }

        return finishReason == ChatFinishReason.Length
            ? "length"
            : finishReason == ChatFinishReason.ContentFilter
                ? "content_filter"
                : finishReason == ChatFinishReason.ToolCalls
                    ? "tool_call"
                    : "stop";
    }
}

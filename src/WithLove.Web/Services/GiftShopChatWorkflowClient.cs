using TemporalCommunity.Extensions.AI;
using TemporalCommunity.Extensions.AI.Exceptions;
using TemporalCommunity.Extensions.AI.Session;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using WithLove.Workflows;
using WithLove.Workflows.Chat;
using WithLove.Workflows.Workflows;

namespace WithLove.Web.Services;

public interface IGiftShopChatWorkflowClient
{
    Task EnsureStartedAsync(string workflowId);

    /// <summary>
    /// Sends one durable turn to the session workflow.
    /// </summary>
    /// <param name="workflowId">The session workflow ID.</param>
    /// <param name="operationId">
    /// The caller's correlation key for this turn. It is <em>not</em> an idempotency key and must
    /// never be used as the Temporal Update ID — see the remarks on the implementation.
    /// </param>
    /// <param name="request">The durable turn request.</param>
    Task<DurableTurnResult<GiftShopChatTurnState>> SendMessageAsync(
        string workflowId,
        string operationId,
        DurableTurnRequest<GiftShopChatRequestData, GiftShopChatTurnState> request);

    Task<IReadOnlyList<DurableSessionEntry>> GetHistoryAsync(string workflowId);

    Task ShutdownAsync(string workflowId);
}

internal sealed class GiftShopChatWorkflowClient(
    ITemporalClient temporalClient,
    IDurableChatWorkflowInputFactory workflowInputFactory) : IGiftShopChatWorkflowClient
{
    public async Task EnsureStartedAsync(string workflowId)
    {
        var input = workflowInputFactory.Create();
        await temporalClient.StartWorkflowAsync(
            (GiftShopChatWorkflow workflow) => workflow.RunAsync(input),
            new WorkflowOptions(workflowId, WorkflowConstants.DefaultTaskQueue)
            {
                IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
                IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate,
            });
    }

    /// <remarks>
    /// The turn is dispatched with the server-assigned Update ID. <paramref name="operationId"/> is
    /// deliberately not passed as <c>WorkflowUpdateOptions.Id</c>: it is minted fresh per call and
    /// never reused, so it cannot deduplicate anything. Setting it as the Update ID would advertise
    /// an at-most-once guarantee that does not exist, and no GiftShop tool performs an external
    /// mutating side effect that would need one. It stays a correlation key only — carried on
    /// <c>CorrelationId</c>, <c>RequestData.OperationId</c>, and the caller's span tag.
    /// </remarks>
    public Task<DurableTurnResult<GiftShopChatTurnState>> SendMessageAsync(
        string workflowId,
        string operationId,
        DurableTurnRequest<GiftShopChatRequestData, GiftShopChatTurnState> request)
    {
        // Fail before dispatch, in the same spirit as the two guards below: the workflow's update
        // validator rejects a request whose CorrelationId and RequestData.OperationId disagree, so
        // catching it here turns a Temporal round-trip into a local throw.
        if (!string.Equals(operationId, request.CorrelationId, StringComparison.Ordinal)
            || !string.Equals(operationId, request.RequestData?.OperationId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The operation ID must match both CorrelationId and RequestData.OperationId.",
                nameof(request));
        }

        if (request.Options is null)
        {
            throw new DurableConfigurationException(
                "DurableTurnRequest.Options cannot be null for a GiftShop durable turn.");
        }

        if (request.ChatOptions?.Tools is { Count: > 0 })
        {
            throw new DurableConfigurationException(
                "ChatOptions.Tools cannot be used for a GiftShop durable turn. " +
                "GiftShop tools are registered with AddDurableTool so Temporal can schedule " +
                "each invocation as an activity.");
        }

        var handle = temporalClient.GetWorkflowHandle<GiftShopChatWorkflow>(workflowId);
        return handle.ExecuteUpdateAsync(workflow => workflow.SendMessageAsync(request));
    }

    public Task<IReadOnlyList<DurableSessionEntry>> GetHistoryAsync(string workflowId)
    {
        var handle = temporalClient.GetWorkflowHandle<GiftShopChatWorkflow>(workflowId);
        return handle.QueryAsync(workflow => workflow.GetHistory());
    }

    public Task ShutdownAsync(string workflowId)
    {
        var handle = temporalClient.GetWorkflowHandle<GiftShopChatWorkflow>(workflowId);
        return handle.SignalAsync(workflow => workflow.RequestShutdownAsync());
    }
}

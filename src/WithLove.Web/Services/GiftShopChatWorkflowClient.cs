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

    public Task<DurableTurnResult<GiftShopChatTurnState>> SendMessageAsync(
        string workflowId,
        string operationId,
        DurableTurnRequest<GiftShopChatRequestData, GiftShopChatTurnState> request)
    {
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
        return handle.ExecuteUpdateAsync(
            workflow => workflow.SendMessageAsync(request),
            new WorkflowUpdateOptions { Id = operationId });
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

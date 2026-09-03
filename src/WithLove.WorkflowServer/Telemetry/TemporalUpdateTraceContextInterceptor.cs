using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using Temporalio.Api.Common.V1;
using Temporalio.Extensions.OpenTelemetry;
using Temporalio.Worker.Interceptors;

namespace WithLove.WorkflowServer.Telemetry;

/// <summary>
/// Makes the caller of a Temporal Update current while that Update schedules workflow activities.
/// </summary>
/// <remarks>
/// Temporal's standard tracing interceptor deliberately parents Update handling to the original
/// workflow-start trace and links the Update caller. For chat, the user-visible unit is one Update,
/// so activity work must instead inherit the Update context that the client interceptor placed in
/// <see cref="HandleUpdateInput.Headers"/>. This interceptor must be registered after the standard
/// interceptor so it is active only inside the Update body and never replaces Temporal's own
/// lifecycle spans.
/// </remarks>
internal sealed class TemporalUpdateTraceContextInterceptor : IWorkerInterceptor
{
    private readonly TraceContextReader contextReader = new();

    /// <inheritdoc />
    public WorkflowInboundInterceptor InterceptWorkflow(
        WorkflowInboundInterceptor nextInterceptor) =>
        new UpdateInboundInterceptor(nextInterceptor, contextReader);

    private sealed class UpdateInboundInterceptor(
        WorkflowInboundInterceptor next,
        TraceContextReader contextReader) : WorkflowInboundInterceptor(next)
    {
        public override async Task<object?> HandleUpdateAsync(HandleUpdateInput input)
        {
            var propagationContext = contextReader.Read(input.Headers);
            if (propagationContext is not { } context || context.ActivityContext == default)
            {
                return await base.HandleUpdateAsync(input).ConfigureAwait(true);
            }

            var previousBaggage = Baggage.Current;
            Baggage.Current = context.Baggage;
            try
            {
                using var updateContext = WorkflowDiagnosticActivity.AttachFromContext(
                    context.ActivityContext);
                return await base.HandleUpdateAsync(input).ConfigureAwait(true);
            }
            finally
            {
                Baggage.Current = previousBaggage;
            }
        }
    }

    private sealed class TraceContextReader : TracingInterceptor
    {
        internal PropagationContext? Read(IReadOnlyDictionary<string, Payload>? headers) =>
            HeadersToContext(headers);
    }
}

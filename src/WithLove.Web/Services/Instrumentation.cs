using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace WithLove.Web.Services;

public class Instrumentation : IDisposable
{
    internal const string ActivitySourceName = "shopSite";
    internal const string ActivitySourceVersion = "1.0.0";

    public ActivitySource ActivitySource { get; }
    public Meter Meter { get; }

    public Counter<long> CartOperations { get; }
    public Histogram<long> LoyaltyReserveDuration { get; }
    public Histogram<int> CartItemsAtCheckout { get; }
    public Counter<long> ChatSessionsStarted { get; }
    public Counter<long> ChatCartActions { get; }
    public Histogram<double> ChatTurnDuration { get; }
    public Counter<long> ChatTokensUsed { get; }
    public Histogram<long> ChatTurnTokens { get; }
    public Counter<long> ChatTurnsWithoutUsage { get; }

    public Instrumentation()
    {
        ActivitySource = new ActivitySource(ActivitySourceName, ActivitySourceVersion);
        Meter = new Meter(ActivitySourceName, ActivitySourceVersion);

        CartOperations = Meter.CreateCounter<long>(
            "cart.operations",
            description: "Number of cart operations, tagged by operation type");

        LoyaltyReserveDuration = Meter.CreateHistogram<long>(
            "loyalty.reserve.duration_ms",
            unit: "ms",
            description: "Time to execute ReservePointsAsync Temporal Update — blocks checkout UI");

        CartItemsAtCheckout = Meter.CreateHistogram<int>(
            "cart.items_at_checkout",
            description: "Item count in cart at the moment checkout is initiated");

        ChatSessionsStarted = Meter.CreateCounter<long>(
            "chat.session.started",
            description: "Chat sessions opened, by authentication type");

        ChatCartActions = Meter.CreateCounter<long>(
            "chat.message.cart_actions",
            description: "Cart mutations triggered by the AI assistant, by action type");

        ChatTurnDuration = Meter.CreateHistogram<double>(
            "chat.turn.duration_ms",
            unit: "ms",
            description: "End-to-end duration of a durable chat Update");

        // Token spend is the only cost in this application that scales with model behaviour rather
        // than with traffic, and a single turn can make up to 40 model calls under the workflow's
        // tool-iteration cap. The durable AI package already tags raw counts onto each gen_ai span,
        // but spans are sampled and are the wrong shape for a budget alarm — these instruments are
        // the aggregate view: a monotonic counter to bill against and a distribution to alert on.
        ChatTokensUsed = Meter.CreateCounter<long>(
            "chat.turn.tokens",
            unit: "{token}",
            description: "Model tokens billed by durable chat turns, by token type "
                         + "(input/output) and turn completion reason");

        ChatTurnTokens = Meter.CreateHistogram<long>(
            "chat.turn.token_usage",
            unit: "{token}",
            description: "Total tokens consumed by a single durable chat turn — the per-turn "
                         + "distribution a token budget is set against");

        // A turn that fails mid-loop has already burned tokens that never reach the counters above,
        // so cost tracking that silently under-reports is worse than none. This counter makes the
        // blind spot measurable: compare it against chat.turn.duration_ms to size the gap.
        ChatTurnsWithoutUsage = Meter.CreateCounter<long>(
            "chat.turn.usage_unreported",
            description: "Durable chat turns that returned no token usage, by completion reason — "
                         + "tokens these turns spent are absent from chat.turn.tokens");
    }

    public void Dispose()
    {
        ActivitySource.Dispose();
        Meter.Dispose();
    }
}

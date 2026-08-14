using Microsoft.Extensions.AI;
using TemporalCommunity.Extensions.AI.Session;

namespace WithLove.Workflows.Chat;

public static class GiftShopChatResponseProjector
{
    public const string AssistantFallback =
        "Hmm, something went sideways on my end. Mind trying that again?";

    public const string IterationLimitMessage =
        "Maximum tool-call iterations (40) exceeded; the conversation did not converge on a final answer.";

    public static string GetLastAssistantText(IEnumerable<ChatMessage> messages) =>
        messages
            .Where(message => message.Role == ChatRole.Assistant)
            .Select(message => message.Text)
            .LastOrDefault(text => !string.IsNullOrWhiteSpace(text))
        ?? string.Empty;

    public static string GetDisplayAssistantText(IEnumerable<ChatMessage> messages)
    {
        var text = GetLastAssistantText(messages);
        return string.IsNullOrWhiteSpace(text) ? AssistantFallback : text;
    }

    public static IReadOnlyList<ChatHistoryEntry> ProjectHistory(
        IEnumerable<DurableSessionEntry> history)
    {
        var projected = new List<ChatHistoryEntry>();

        foreach (var entry in history)
        {
            if (entry is DurableSessionRequest)
            {
                foreach (var message in entry.Messages.Where(message => message.Role == ChatRole.User))
                {
                    if (!string.IsNullOrWhiteSpace(message.Text))
                    {
                        projected.Add(new ChatHistoryEntry(
                            true,
                            message.Text,
                            (message.CreatedAt ?? entry.CreatedAt).UtcDateTime));
                    }
                }

                continue;
            }

            if (entry is DurableSessionResponse)
            {
                var text = GetDisplayAssistantText(entry.Messages);
                var timestamp = entry.Messages
                    .LastOrDefault(message =>
                        message.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(message.Text))
                    ?.CreatedAt;
                projected.Add(new ChatHistoryEntry(
                    false,
                    text,
                    (timestamp ?? entry.CreatedAt).UtcDateTime));
            }
        }

        return projected;
    }
}

namespace WithLove.OpenInference;

internal static class OpenInferenceKey
{
    internal static string RetrievalDocument(int index, string member)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(member);
        return $"{OpenInferenceAttributes.RetrievalDocuments}.{index}.{member}";
    }
}

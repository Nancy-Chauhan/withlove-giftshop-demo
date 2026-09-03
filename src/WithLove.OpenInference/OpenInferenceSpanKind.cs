namespace WithLove.OpenInference;

internal enum OpenInferenceSpanKind
{
    Chain,
    Retriever
}

internal static class OpenInferenceSpanKindValues
{
    internal static string ToAttributeValue(this OpenInferenceSpanKind kind) => kind switch
    {
        OpenInferenceSpanKind.Chain => "CHAIN",
        OpenInferenceSpanKind.Retriever => "RETRIEVER",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown OpenInference span kind.")
    };
}

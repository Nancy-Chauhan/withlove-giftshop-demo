namespace WithLove.OpenInference.Spans;

/// <summary>
/// The MIME types the typed scopes derive for <c>input.mime_type</c> and <c>output.mime_type</c>.
/// </summary>
/// <remarks>
/// These values are never accepted as parameters. Each is chosen by the overload that carries the
/// value itself, which is what makes it impossible for a MIME type to disagree with the value it
/// describes. Both are declared well-known values of the two attributes in the checked-in
/// conventions manifest.
/// </remarks>
internal static class OpenInferenceMimeTypes
{
    internal const string PlainText = "text/plain";

    internal const string Json = "application/json";
}

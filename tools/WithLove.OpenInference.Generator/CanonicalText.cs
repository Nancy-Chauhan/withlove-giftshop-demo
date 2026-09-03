using System.Text;

namespace WithLove.OpenInference.Generator;

public static class CanonicalText
{
    public const string NewLine = "\n";

    private static readonly UTF8Encoding Utf8WithoutBom = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static StringBuilder AppendCanonicalLine(this StringBuilder builder, string value) =>
        builder.Append(value).Append(NewLine);

    public static StringBuilder AppendCanonicalLine(this StringBuilder builder) =>
        builder.Append(NewLine);

    public static string TerminateLine(string value) => value + NewLine;

    public static byte[] GetUtf8Bytes(string value) => Utf8WithoutBom.GetBytes(value);
}

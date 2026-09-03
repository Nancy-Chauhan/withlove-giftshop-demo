namespace WithLove.OpenInference.Generator;

public static class ArtifactByteComparison
{
    public static bool MatchesCanonicalUtf8(string path, string expected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(expected);

        var expectedBytes = CanonicalText.GetUtf8Bytes(expected);
        var actualBytes = File.ReadAllBytes(path);
        return actualBytes.AsSpan().SequenceEqual(expectedBytes);
    }
}

using System.Buffers;
using System.Text;
using System.Text.Json;

namespace WithLove.OpenInference.Spans;

/// <summary>
/// Turns a document list into the JSON <c>output.value</c> projection that
/// <see cref="RetrieverScope.Complete(IReadOnlyList{RetrievedDocument})"/> emits alongside the
/// flattened attributes, and rejects lists that could not be flattened contiguously.
/// </summary>
/// <remarks>
/// Written with <see cref="Utf8JsonWriter"/> rather than <see cref="JsonSerializer"/> for two
/// reasons. It is trim- and AOT-safe without a serializer context, so the library owes callers no
/// <c>JsonTypeInfo</c> for its own model; and metadata arrives as an already-validated JSON
/// document, which a serializer would re-encode as a JSON <em>string</em> rather than splice in as
/// a value.
/// </remarks>
internal static class RetrievedDocumentProjection
{
    private const string IdName = "id";
    private const string ContentName = "content";
    private const string ScoreName = "score";
    private const string MetadataName = "metadata";

    /// <summary>
    /// Rejects a null list or a null element before any attribute has been written.
    /// </summary>
    /// <remarks>
    /// A null element is refused rather than skipped: skipping it would either shift every later
    /// document's index or leave a hole at its own, and both outcomes are the non-contiguity this
    /// design exists to prevent.
    /// </remarks>
    internal static void Validate(IReadOnlyList<RetrievedDocument> documents, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(documents, parameterName);
        for (var index = 0; index < documents.Count; index++)
        {
            if (documents[index] is null)
            {
                throw new ArgumentException(
                    $"The document at index {index} is null. Every position in a retrieval result must carry a document, "
                    + "because omitting one would break the zero-based contiguous index sequence.",
                    parameterName);
            }
        }
    }

    /// <summary>
    /// Projects a validated document list onto a JSON array, omitting members the documents do not
    /// carry so the projection and the flattened attributes describe the same thing.
    /// </summary>
    /// <remarks>
    /// Property names match the <c>document.*</c> member names and the order matches the flattening
    /// order, so the projection reads as the same data in a second shape rather than a second
    /// model.
    /// </remarks>
    internal static string Project(IReadOnlyList<RetrievedDocument> documents)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            for (var index = 0; index < documents.Count; index++)
            {
                var document = documents[index];
                writer.WriteStartObject();
                if (document.Id is { } id)
                {
                    writer.WriteString(IdName, id);
                }

                if (document.Content is { } content)
                {
                    writer.WriteString(ContentName, content);
                }

                if (document.Score is { } score)
                {
                    writer.WriteNumber(ScoreName, score);
                }

                if (document.Metadata is { } metadata)
                {
                    writer.WritePropertyName(MetadataName);
                    // The document was validated when the OpenInferenceJson instance was created,
                    // so the writer's own reparse would be the second of two identical checks.
                    writer.WriteRawValue(metadata.Value, skipInputValidation: true);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

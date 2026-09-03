namespace WithLove.OpenInference.Spans;

/// <summary>
/// One document in a retrieval result, flattened by the library into
/// <c>retrieval.documents.{index}.document.*</c>.
/// </summary>
/// <remarks>
/// <para><strong>The index is not here, and that is the point.</strong> A document carries its
/// members and nothing else; its position in the list handed to
/// <see cref="RetrieverScope.Record"/> or <see cref="RetrieverScope.Complete(IReadOnlyList{RetrievedDocument})"/>
/// is its index. Callers cannot choose, skip, repeat, or reorder indices, so the zero-based
/// contiguous numbering the specification asks for is a property of the type rather than a rule a
/// caller has to remember.</para>
/// <para><strong>Every document contributes at least one attribute.</strong> A document with no
/// members would occupy an index while emitting nothing under it, which is precisely the gap that
/// contiguity forbids - so the constructor rejects it. This is the same rule, for the same reason,
/// required by the OpenInference flattened index sequence.</para>
/// <para>Members are get-only by design. A <c>with</c> expression copies through the synthesized
/// copy constructor and would not re-run the checks below, so an initializable member would be a
/// hole in the validation this type exists to provide.</para>
/// </remarks>
public sealed record RetrievedDocument
{
    /// <summary>Creates a validated retrieval document from the members the caller has.</summary>
    /// <remarks>
    /// Every parameter is optional individually and at least one is required collectively. Members
    /// left <see langword="null"/> are absent: they are emitted as no attribute and appear in no
    /// output projection.
    /// </remarks>
    /// <param name="id">The optional unique identifier of the document, emitted as <c>document.id</c>.</param>
    /// <param name="content">The optional text of the document, emitted as <c>document.content</c>.</param>
    /// <param name="score">
    /// The optional relevance score of the document, emitted as <c>document.score</c>. It must be
    /// finite: JSON has no representation for NaN or infinity, so a non-finite score could be
    /// flattened but never projected onto <c>output.value</c>, and a value that one verb accepts
    /// and another rejects is worse than one that is refused up front.
    /// </param>
    /// <param name="metadata">
    /// The optional metadata of the document, emitted as <c>document.metadata</c>. Any valid JSON
    /// root is permitted by the OpenInference convention.
    /// </param>
    /// <exception cref="ArgumentException">
    /// No member was supplied, <paramref name="score"/> is not finite, or <paramref name="metadata"/>
    /// is the uninitialized default value.
    /// </exception>
    public RetrievedDocument(
        string? id = null,
        string? content = null,
        double? score = null,
        OpenInferenceJson? metadata = null)
    {
        if (score is { } suppliedScore && !double.IsFinite(suppliedScore))
        {
            throw new ArgumentException(
                "The document score must be a finite number; JSON cannot represent NaN or infinity.",
                nameof(score));
        }

        if (metadata is { } suppliedMetadata && !suppliedMetadata.IsInitialized)
        {
            throw new ArgumentException("The document metadata must be initialized.", nameof(metadata));
        }

        if (id is null && content is null && score is null && metadata is null)
        {
            throw new ArgumentException(
                "A retrieved document requires at least one of id, content, score, or metadata. "
                + "A document with no members would consume an index while emitting nothing beneath it, "
                + "leaving a gap in the flattened index sequence.");
        }

        Id = id;
        Content = content;
        Score = score;
        Metadata = metadata;
    }

    /// <summary>Gets the optional unique identifier of the document.</summary>
    public string? Id { get; }

    /// <summary>Gets the optional text of the document.</summary>
    public string? Content { get; }

    /// <summary>Gets the optional finite relevance score of the document.</summary>
    public double? Score { get; }

    /// <summary>Gets the optional metadata of the document, with any JSON root kind.</summary>
    public OpenInferenceJson? Metadata { get; }
}

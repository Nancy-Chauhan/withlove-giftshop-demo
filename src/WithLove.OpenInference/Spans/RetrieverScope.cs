using System.Diagnostics;

namespace WithLove.OpenInference.Spans;

/// <summary>
/// A typed scope over an OpenInference RETRIEVER span.
/// </summary>
/// <remarks>
/// <para>The retriever result is a collection, and the collection is where flattening moves inside
/// the library. A caller hands over documents; the library derives every
/// <c>retrieval.documents.{index}.document.*</c> key from list position. Nothing in this surface
/// accepts an index, so a non-contiguous, repeated, or reordered index sequence is not something a
/// caller can express through it.</para>
/// <para>Obtain one from <see cref="OpenInferenceSpanExtensions.StartRetriever"/>.</para>
/// </remarks>
public sealed class RetrieverScope : OpenInferenceScope
{
    private bool documentsRecorded;

    internal RetrieverScope(Activity? activity, OpenInferenceTraceConfig traceConfig)
        : base(activity, traceConfig)
    {
    }

    /// <summary>Flattens the retrieved documents onto this scope's span.</summary>
    /// <remarks>
    /// <para>Kind-specific attributes only: no <c>output.value</c> is projected. Use
    /// <see cref="Complete(IReadOnlyList{RetrievedDocument})"/> for documents and the output
    /// projection together, or one of the base <c>Complete</c> overloads to project an output the
    /// documents do not describe.</para>
    /// <para>An empty list is a valid result - a retrieval that found nothing - and emits no
    /// document attributes. A second call throws: recording documents twice would interleave two
    /// result sets over one index sequence.</para>
    /// </remarks>
    /// <param name="documents">The retrieved documents, in rank order. Position is index.</param>
    /// <exception cref="ArgumentNullException"><paramref name="documents"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="documents"/> contains a <see langword="null"/> element.</exception>
    /// <exception cref="InvalidOperationException">The documents were already recorded.</exception>
    /// <exception cref="ObjectDisposedException">The scope was already disposed.</exception>
    public void Record(IReadOnlyList<RetrievedDocument> documents)
    {
        RetrievedDocumentProjection.Validate(documents, nameof(documents));
        ThrowIfDisposed();
        ThrowIfDocumentsRecorded();

        // Validation is complete: from here the whole result is applied.
        EmitDocuments(documents);
    }

    /// <summary>
    /// Flattens the retrieved documents and projects them onto <c>output.value</c> as JSON.
    /// </summary>
    /// <remarks>
    /// <para>This is <see cref="Record"/> plus the output projection, which is what makes documents
    /// the only thing the caller passes. <c>output.mime_type</c> is derived from the projection and
    /// is never a parameter, so the two attributes cannot disagree.</para>
    /// <para>Both gates - documents and output - are checked before either group of attributes is
    /// written, and the projection is built before the first tag, so a rejected completion leaves
    /// no half-applied result behind. Building the projection first is an ordering guarantee rather
    /// than a failure-handling one: for a list that has passed validation the projection is total,
    /// because a document cannot carry a non-finite score or an uninitialized metadata document and
    /// the JSON writer substitutes the replacement character for unpaired surrogates rather than
    /// throwing. It is built ahead of the mutation anyway, and unconditionally, so that a document
    /// model with weaker construction-time guards could not turn this into a half-applied result or
    /// into a failure that only appears once sampling is switched on.</para>
    /// </remarks>
    /// <param name="documents">The retrieved documents, in rank order. Position is index.</param>
    /// <exception cref="ArgumentNullException"><paramref name="documents"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="documents"/> contains a <see langword="null"/> element.</exception>
    /// <exception cref="InvalidOperationException">
    /// The documents were already recorded, or the output projection was already recorded.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The scope was already disposed.</exception>
    public void Complete(IReadOnlyList<RetrievedDocument> documents)
    {
        RetrievedDocumentProjection.Validate(documents, nameof(documents));
        ThrowIfDisposed();
        ThrowIfDocumentsRecorded();
        ThrowIfOutputRecorded();
        var projection = RetrievedDocumentProjection.Project(documents);

        // Validation is complete: from here the whole result is applied.
        EmitDocuments(documents);
        CompleteCore(projection, OpenInferenceMimeTypes.Json);
    }

    private void ThrowIfDocumentsRecorded()
    {
        if (documentsRecorded)
        {
            throw new InvalidOperationException(
                "The retrieved documents were already recorded on this scope. Recording twice is a bug, not a use case.");
        }
    }

    private void EmitDocuments(IReadOnlyList<RetrievedDocument> documents)
    {
        documentsRecorded = true;
        for (var index = 0; index < documents.Count; index++)
        {
            var document = documents[index];
            if (document.Id is { } id)
            {
                SetTag(OpenInferenceKey.RetrievalDocument(index, OpenInferenceAttributes.DocumentId), id);
            }

            if (document.Content is { } content)
            {
                SetTag(OpenInferenceKey.RetrievalDocument(index, OpenInferenceAttributes.DocumentContent), content);
            }

            if (document.Score is { } score)
            {
                SetTag(OpenInferenceKey.RetrievalDocument(index, OpenInferenceAttributes.DocumentScore), score);
            }

            if (document.Metadata is { } metadata)
            {
                SetTag(
                    OpenInferenceKey.RetrievalDocument(index, OpenInferenceAttributes.DocumentMetadata),
                    metadata.Value);
            }
        }
    }
}

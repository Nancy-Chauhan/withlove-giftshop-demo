using System.Diagnostics;

namespace WithLove.OpenInference.Spans;

/// <summary>Base lifecycle for WithLove's CHAIN and RETRIEVER spans.</summary>
public abstract class OpenInferenceScope : IDisposable
{
    private readonly Activity? activity;
    private readonly OpenInferenceTraceConfig traceConfig;
    private bool outputRecorded;
    private bool disposed;

    private protected OpenInferenceScope(Activity? activity, OpenInferenceTraceConfig traceConfig)
    {
        ArgumentNullException.ThrowIfNull(traceConfig);
        this.activity = activity;
        this.traceConfig = traceConfig;
        IsRecording = activity?.IsAllDataRequested ?? false;
        TraceId = (activity ?? System.Diagnostics.Activity.Current)?.TraceId.ToString() ?? string.Empty;
    }

    /// <summary>Gets the underlying Activity so application-specific tags can be added.</summary>
    public Activity? Activity => activity;

    /// <summary>Gets the current trace identifier, including the ambient parent when no span was sampled.</summary>
    public string TraceId { get; }

    /// <summary>Gets whether the underlying span is collecting attributes.</summary>
    public bool IsRecording { get; }

    /// <summary>Projects a plain-text result onto <c>output.value</c>.</summary>
    public void Complete(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        CompleteCore(output, OpenInferenceMimeTypes.PlainText);
    }

    /// <summary>Records an exception event and marks the span as failed.</summary>
    public void Fail(Exception exception, bool escaped = false)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ThrowIfDisposed();
        activity.RecordOpenInferenceException(exception, escaped);
    }

    /// <summary>Ends the span. Repeated disposal is safe.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        activity?.Dispose();
    }

    private protected void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    private protected void ThrowIfOutputRecorded()
    {
        if (outputRecorded)
        {
            throw new InvalidOperationException("The output projection was already recorded on this scope.");
        }
    }

    private protected void SetTag(string key, string value) =>
        activity.SetOpenInferenceTag(key, value, traceConfig);

    private protected void SetTag(string key, double value) =>
        activity.SetOpenInferenceTag(key, value, traceConfig);

    private protected void CompleteCore(string outputValue, string mimeType)
    {
        ThrowIfDisposed();
        ThrowIfOutputRecorded();
        outputRecorded = true;
        SetTag(OpenInferenceAttributes.OutputValue, outputValue);
        SetTag(OpenInferenceAttributes.OutputMimeType, mimeType);
    }
}

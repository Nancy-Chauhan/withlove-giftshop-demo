namespace WithLove.OpenInference;

/// <summary>Immutable values propagated to OpenInference activities through async execution flow.</summary>
public sealed record OpenInferenceContextValues
{
    public string? SessionId { get; init; }
    public string? UserId { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
}

/// <summary>Manages ambient OpenInference context using <see cref="AsyncLocal{T}"/>.</summary>
public static class OpenInferenceContextScope
{
    private static readonly AsyncLocal<ScopeNode?> CurrentNode = new();

    /// <summary>Pushes immutable ambient OpenInference values for activities created in the current async flow.</summary>
    /// <remarks>
    /// Values are snapshotted when the scope is created. Nested scopes must be disposed in reverse order;
    /// explicit per-activity tags override ambient values when an activity starts.
    /// </remarks>
    /// <param name="values">Session, user, and tag values to make ambient.</param>
    /// <returns>A scope that restores the previous ambient values when disposed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is <see langword="null"/>.</exception>
    public static IDisposable Push(OpenInferenceContextValues values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var snapshot = values with
        {
            Tags = values.Tags is null ? null : Array.AsReadOnly(values.Tags.ToArray())
        };
        var node = new ScopeNode(CurrentNode.Value, snapshot);
        CurrentNode.Value = node;
        return node;
    }

    internal static OpenInferenceContextValues? Current => CurrentNode.Value?.Values;

    private sealed class ScopeNode(ScopeNode? parent, OpenInferenceContextValues values) : IDisposable
    {
        private bool _disposed;

        internal OpenInferenceContextValues Values { get; } = values;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (!ReferenceEquals(CurrentNode.Value, this))
            {
                throw new InvalidOperationException("OpenInference context scopes must be disposed in reverse order.");
            }

            CurrentNode.Value = parent;
            _disposed = true;
        }
    }
}

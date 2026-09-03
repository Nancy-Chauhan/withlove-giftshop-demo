using System.Security.Cryptography;
using System.Text;

namespace WithLove.Web.Telemetry;

/// <summary>
/// Creates deterministic HMAC-based labels from user and workflow identifiers for use in trace
/// attributes.
/// </summary>
/// <remarks>
/// The returned label does not contain the input identifier. The same key, version, label type,
/// and input produce the same label; changing the key or version produces a different label. This
/// type does not export telemetry or encrypt identifiers.
/// </remarks>
public sealed class TelemetryIdentity
{
    private readonly byte[] _key;
    private readonly string _keyVersion;

    private TelemetryIdentity(byte[] key, string keyVersion)
    {
        _key = key;
        _keyVersion = keyVersion;
    }

    public static TelemetryIdentity Create(string? base64Key, string? keyVersion)
    {
        if (string.IsNullOrWhiteSpace(base64Key))
            throw new InvalidOperationException("TelemetryIdentity:Key is required for chat tracing.");
        if (string.IsNullOrWhiteSpace(keyVersion))
            throw new InvalidOperationException("TelemetryIdentity:KeyVersion is required for chat tracing.");

        byte[] key;
        try { key = Convert.FromBase64String(base64Key); }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("TelemetryIdentity:Key must be valid Base64.", exception);
        }

        if (key.Length < 32)
            throw new InvalidOperationException("TelemetryIdentity:Key must decode to at least 32 bytes.");

        return new TelemetryIdentity(key, keyVersion.Trim());
    }

    public string ForSession(string rawWorkflowId) => Pseudonymize("session", rawWorkflowId);
    public string ForUser(string rawUserId) => Pseudonymize("user", rawUserId);

    private string Pseudonymize(string domain, string rawValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawValue);
        var payload = Encoding.UTF8.GetBytes($"withlove-telemetry:{domain}\0{rawValue}");
        var hash = HMACSHA256.HashData(_key, payload);
        return $"hmac-{_keyVersion}-{Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
    }
}

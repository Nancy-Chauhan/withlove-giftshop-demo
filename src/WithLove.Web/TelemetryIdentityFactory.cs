using WithLove.Web.Telemetry;

namespace WithLove.Web;

internal static class TelemetryIdentityFactory
{
    // DEMO ONLY: Development uses a committed public key to remove local setup friction. This
    // keeps raw identifiers out of exported traces, but anyone with the source and a candidate
    // identifier can reproduce its HMAC value. Every non-Development environment must supply a
    // private, stable key through configuration; the AppHost deployment stores it in Key Vault.
    private const string DevelopmentKey = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";
    private const string DevelopmentKeyVersion = "demo-v1";

    internal static TelemetryIdentity Create(
        IHostEnvironment environment,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(configuration);

        return environment.IsDevelopment()
            ? TelemetryIdentity.Create(DevelopmentKey, DevelopmentKeyVersion)
            : TelemetryIdentity.Create(
                configuration["TelemetryIdentity:Key"],
                configuration["TelemetryIdentity:KeyVersion"]);
    }
}

using WithLove.Web.Telemetry;

namespace WithLove.Web.Tests.Unit;

public class TelemetryIdentityTests
{
    private static readonly string Key = Convert.ToBase64String(
        Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());

    [Fact]
    public void Create_RejectsMissingMalformedAndShortKeys()
    {
        var missing = () => TelemetryIdentity.Create(null, "v1");
        var malformed = () => TelemetryIdentity.Create("not-base64", "v1");
        var shortKey = () => TelemetryIdentity.Create(Convert.ToBase64String(new byte[31]), "v1");

        missing.Should().Throw<InvalidOperationException>().WithMessage("*Key is required*");
        malformed.Should().Throw<InvalidOperationException>().WithMessage("*valid Base64*");
        shortKey.Should().Throw<InvalidOperationException>().WithMessage("*at least 32 bytes*");
    }

    [Fact]
    public void Create_RejectsMissingKeyVersion()
    {
        var action = () => TelemetryIdentity.Create(Key, " ");
        action.Should().Throw<InvalidOperationException>().WithMessage("*KeyVersion is required*");
    }

    [Fact]
    public void Pseudonyms_AreStableDomainSeparatedAndVersioned()
    {
        var identity = TelemetryIdentity.Create(Key, "v1");

        var firstSession = identity.ForSession("raw-123");
        var secondSession = identity.ForSession("raw-123");
        var user = identity.ForUser("raw-123");

        firstSession.Should().Be(secondSession).And.StartWith("hmac-v1-");
        firstSession.Should().NotBe(user);
        firstSession.Should().NotContain("raw-123");
    }

    [Fact]
    public void Rotation_ChangesPseudonym()
    {
        var v1 = TelemetryIdentity.Create(Key, "v1").ForSession("workflow");
        var v2 = TelemetryIdentity.Create(Convert.ToBase64String(new byte[32]), "v2").ForSession("workflow");

        v1.Should().NotBe(v2);
    }
}

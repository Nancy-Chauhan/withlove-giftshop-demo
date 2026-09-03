using FakeItEasy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace WithLove.Web.Tests.Unit;

public class TelemetryIdentityFactoryTests
{
    [Fact]
    public void Create_InDevelopment_UsesStablePublicDemoKeyWithoutConfiguration()
    {
        var environment = EnvironmentNamed(Environments.Development);
        var configuration = new ConfigurationBuilder().Build();

        var first = TelemetryIdentityFactory.Create(environment, configuration).ForUser("user-42");
        var second = TelemetryIdentityFactory.Create(environment, configuration).ForUser("user-42");

        first.Should().StartWith("hmac-demo-v1-");
        second.Should().Be(first);
        first.Should().NotContain("user-42");
    }

    [Fact]
    public void Create_OutsideDevelopment_UsesConfiguredPrivateKey()
    {
        var environment = EnvironmentNamed(Environments.Production);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TelemetryIdentity:Key"] = Convert.ToBase64String(new byte[32]),
                ["TelemetryIdentity:KeyVersion"] = "deployment-v2",
            })
            .Build();

        var pseudonym = TelemetryIdentityFactory.Create(environment, configuration).ForUser("user-42");

        pseudonym.Should().StartWith("hmac-deployment-v2-");
        pseudonym.Should().NotContain("user-42");
    }

    [Fact]
    public void Create_OutsideDevelopmentWithoutConfiguredKey_FailsStartup()
    {
        var environment = EnvironmentNamed(Environments.Production);
        var configuration = new ConfigurationBuilder().Build();

        var action = () => TelemetryIdentityFactory.Create(environment, configuration);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("TelemetryIdentity:Key is required for chat tracing.");
    }

    private static IHostEnvironment EnvironmentNamed(string name)
    {
        var environment = A.Fake<IHostEnvironment>();
        A.CallTo(() => environment.EnvironmentName).Returns(name);
        return environment;
    }
}

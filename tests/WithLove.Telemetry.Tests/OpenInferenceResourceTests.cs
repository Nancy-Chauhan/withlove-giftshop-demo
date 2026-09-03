using OpenTelemetry.Resources;
using WithLove.OpenInference;

namespace WithLove.Telemetry.Tests;

public class OpenInferenceResourceTests
{
    [Fact]
    public void AddOpenInferenceProjectName_AddsTheExactResourceAttribute()
    {
        var resource = ResourceBuilder.CreateEmpty()
            .AddOpenInferenceProjectName("withlove-giftshop")
            .Build();

        resource.Attributes.Should().ContainSingle(attribute =>
            attribute.Key == OpenInferenceAttributes.OpenInferenceProjectName
            && Equals(attribute.Value, "withlove-giftshop"));
    }

    [Fact]
    public void AddOpenInferenceProjectName_RejectsBlankNames()
    {
        var builder = ResourceBuilder.CreateEmpty();

        builder.Invoking(candidate => candidate.AddOpenInferenceProjectName(" "))
            .Should().Throw<ArgumentException>();
    }
}

using Invest.Sign.Router.Core.Engine;

namespace Invest.Sign.Router.Tests.Engine;

public class EngineOptionsTests
{
    [Fact]
    public void Defaults_are_valid()
    {
        Assert.Empty(new EngineOptions().Validate());
    }

    [Fact]
    public void Reports_invalid_values()
    {
        var options = new EngineOptions
        {
            LeaseDuration = TimeSpan.Zero,
            BatchSize = 0,
            MaxAttempts = 0,
            RetryBaseDelay = TimeSpan.FromMinutes(5),
            RetryMaxDelay = TimeSpan.FromMinutes(1),
        };

        Assert.Equal(4, options.Validate().Count);
    }
}

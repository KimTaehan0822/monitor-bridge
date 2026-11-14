using MonitorBridge.Core.Updates;

namespace MonitorBridge.Core.Tests;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("v1.0.2", "1.0.1.0", true)]
    [InlineData("v1.0.1", "1.0.1.0", false)]
    [InlineData("v1.0.0", "1.0.1", false)]
    [InlineData("v1.10.0", "1.9.9", true)]
    [InlineData("v0.3.1", "0.3.1+20261114.1200", false)]
    [InlineData("nightly", "1.0.0", false)]
    public void IsNewer_ComparesFirstThreeNumbers(string latest, string current, bool expected) =>
        Assert.Equal(expected, ReleaseVersion.IsNewer(latest, current));
}

namespace SysPulse.Tests;

public class SmokeTests
{
    [Fact]
    public void CoreAssemblyLoads() => Assert.NotNull(typeof(SysPulse.Core.AssemblyMarker).Assembly);
}

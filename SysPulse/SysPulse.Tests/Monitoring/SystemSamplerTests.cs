using SysPulse.Core.Monitoring;

namespace SysPulse.Tests.Monitoring;

public class SystemSamplerTests
{
    [Fact]
    public void ComputeCpuPercent_ReturnsExpectedBusyFraction()
    {
        // idleDelta=500, kernelDelta=500, userDelta=300 -> total=800, busy=1-500/800=0.375 -> 37.5%
        double result = SystemSampler.ComputeCpuPercent(
            prevIdle: 1000, prevKernel: 2000, prevUser: 1000,
            idle: 1500, kernel: 2500, user: 1300);

        Assert.Equal(37.5, result, precision: 6);
    }

    [Fact]
    public void ComputeCpuPercent_FullyIdle_ReturnsZero()
    {
        // idleDelta == kernelDelta (userDelta 0) -> busy fraction 0.
        double result = SystemSampler.ComputeCpuPercent(
            prevIdle: 0, prevKernel: 0, prevUser: 0,
            idle: 1000, kernel: 1000, user: 0);

        Assert.Equal(0.0, result, precision: 6);
    }

    [Fact]
    public void ComputeCpuPercent_FullyBusy_ReturnsHundred()
    {
        // idleDelta 0, kernelDelta+userDelta > 0 -> busy fraction 1.
        double result = SystemSampler.ComputeCpuPercent(
            prevIdle: 0, prevKernel: 0, prevUser: 0,
            idle: 0, kernel: 500, user: 500);

        Assert.Equal(100.0, result, precision: 6);
    }

    [Fact]
    public void ComputeCpuPercent_ClampsToHundredEvenIfMathOvershoots()
    {
        // Pathological input (idleDelta larger than kernelDelta, e.g. clock skew) must still clamp into [0,100].
        double result = SystemSampler.ComputeCpuPercent(
            prevIdle: 0, prevKernel: 0, prevUser: 0,
            idle: 0, kernel: 100, user: 0);

        Assert.InRange(result, 0.0, 100.0);
    }

    [Fact]
    public void ComputeCpuPercent_NoElapsedTime_ReturnsZero()
    {
        double result = SystemSampler.ComputeCpuPercent(
            prevIdle: 1000, prevKernel: 2000, prevUser: 1000,
            idle: 1000, kernel: 2000, user: 1000);

        Assert.Equal(0.0, result, precision: 6);
    }

    [Fact]
    public void ComputeMemoryPercent_ReturnsUsedFraction()
    {
        double result = SystemSampler.ComputeMemoryPercent(totalPhysBytes: 16_000, availPhysBytes: 4_000);

        Assert.Equal(75.0, result, precision: 6);
    }

    [Fact]
    public void ComputeMemoryPercent_ZeroTotal_ReturnsZero()
    {
        double result = SystemSampler.ComputeMemoryPercent(totalPhysBytes: 0, availPhysBytes: 0);

        Assert.Equal(0.0, result, precision: 6);
    }

    [Fact]
    public void ComputeMemoryPercent_AvailExceedsTotal_ClampsToZeroUsed()
    {
        // Defensive: should never happen from the OS, but must not underflow/throw.
        double result = SystemSampler.ComputeMemoryPercent(totalPhysBytes: 1_000, availPhysBytes: 2_000);

        Assert.Equal(0.0, result, precision: 6);
    }
}

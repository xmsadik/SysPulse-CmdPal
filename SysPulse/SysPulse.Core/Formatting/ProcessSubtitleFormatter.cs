using System.Globalization;
using SysPulse.Core.Processes;

namespace SysPulse.Core.Formatting;

/// <summary>
/// Builds the Top-5 process list's item subtitle (spec §5.5), e.g.
/// <c>CPU 38% · RAM 1.2 GB · PID 12345</c>, with a <c>· protected</c> suffix for processes
/// <see cref="Processes.ProtectedProcessList"/> never offers for killing. Kept in Core, alongside
/// <see cref="ByteFormatter"/>, so it is unit-testable without the SDK.
/// </summary>
public static class ProcessSubtitleFormatter
{
    /// <summary>
    /// Formats the subtitle for one <see cref="ProcessSample"/>.
    /// </summary>
    /// <param name="sample">The process to describe.</param>
    /// <param name="isProtected">Whether the process is on the protected list (spec §5.6).</param>
    /// <returns>The formatted subtitle.</returns>
    public static string Format(ProcessSample sample, bool isProtected)
    {
        int cpuPercent = (int)Math.Round(Math.Clamp(sample.CpuPercent, 0, 100), MidpointRounding.AwayFromZero);
        string ram = ByteFormatter.Format(sample.PrivateBytes);
        string subtitle = string.Format(CultureInfo.InvariantCulture, "CPU {0}% · RAM {1} · PID {2}", cpuPercent, ram, sample.Pid);
        return isProtected ? subtitle + " · protected" : subtitle;
    }
}

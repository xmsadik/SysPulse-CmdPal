using System;
using System.IO;
using Microsoft.CommandPalette.Extensions.Toolkit;
using SysPulse.Core.Logging;

namespace SysPulse.Logging;

/// <summary>
/// Process-wide access point to a single <see cref="FileLogger"/> instance (task requirement H2).
/// </summary>
/// <remarks>
/// <see cref="FileLogger"/> itself lives in <c>SysPulse.Core</c> so it stays unit-testable with a
/// temp directory (see <c>SysPulse.Tests.Logging.FileLoggerTests</c>). This static wrapper exists
/// only so call sites across the MSIX project (dock band, pages, commands) can log with one line
/// -- <c>Log.Info("...")</c> -- instead of threading a <see cref="FileLogger"/> reference through
/// every constructor. <see cref="Initialize"/> is called once, from
/// <see cref="SysPulseCommandsProvider"/>'s constructor (the extension's startup path); every
/// method below is a no-op until then, and never throws.
/// </remarks>
internal static class Log
{
    private static FileLogger? _logger;

    /// <summary>
    /// Creates the process-wide logger under <c>&lt;LocalState&gt;\SysPulse\logs\syspulse.log</c>.
    /// Safe to call more than once (e.g. from tests); the latest call wins.
    /// </summary>
    /// <param name="debugEnabled">
    /// Whether <see cref="Debug"/> entries are written. SysPulse has no user-facing setting for
    /// this (task requirement H2) -- callers pass the <c>#if DEBUG</c> compile-time flag.
    /// </param>
    public static void Initialize(bool debugEnabled)
    {
        try
        {
            string logDirectory = Path.Combine(Utilities.BaseSettingsPath("SysPulse"), "SysPulse", "logs");
            _logger = new FileLogger(logDirectory, debugEnabled);
        }
        catch
        {
            // Never let logging setup break extension startup.
            _logger = null;
        }
    }

    public static void Info(string message) => _logger?.Info(message);

    public static void Warning(string message) => _logger?.Warning(message);

    public static void Error(string message) => _logger?.Error(message);

    public static void Error(string message, Exception exception) => _logger?.Error(message, exception);

    public static void Debug(string message) => _logger?.Debug(message);
}

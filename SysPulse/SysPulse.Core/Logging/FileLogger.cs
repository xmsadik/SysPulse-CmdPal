using System.Globalization;

namespace SysPulse.Core.Logging;

/// <summary>Severity of a <see cref="FileLogger"/> entry.</summary>
public enum LogLevel
{
    /// <summary>Verbose diagnostic detail. Off by default (task requirement H2) — see <see cref="FileLogger"/> remarks.</summary>
    Debug,

    /// <summary>Normal lifecycle events (startup, monitor start/stop, settings changes, ...).</summary>
    Info,

    /// <summary>A recoverable problem (a sampling cycle faulted, a toast failed to show, ...).</summary>
    Warning,

    /// <summary>An operation failed outright (e.g. a kill attempt threw an unexpected exception).</summary>
    Error,
}

/// <summary>
/// A tiny, dependency-free rolling file logger (task requirement H2 / spec §7 "Logging").
/// </summary>
/// <remarks>
/// <para>
/// <b>Rolling:</b> before every write, if the primary log file is at or above
/// <see cref="RollSizeBytes"/> (~1 MB), it is renamed to <c>&lt;name&gt;.1.log</c> (overwriting
/// any previous backup — exactly one backup generation is kept) and a fresh primary file starts
/// on the next append.
/// </para>
/// <para>
/// <b>Thread-safety:</b> every public method takes an internal <see cref="Lock"/> around the
/// roll-check + append, so concurrent callers never interleave or tear a line.
/// </para>
/// <para>
/// <b>Never throws:</b> every method swallows and ignores all exceptions (a full disk, a locked
/// file, an inaccessible directory, ...) — logging must never be the reason SysPulse's real work
/// fails. Callers do not need to wrap calls in their own try/catch.
/// </para>
/// <para>
/// <b>Debug level:</b> off by default (the constructor's <c>debugEnabled</c> parameter
/// defaults to <see langword="false"/>). The MSIX host passes <c>true</c> only for
/// <c>#if DEBUG</c> builds (see <c>SysPulse.Logging.Log.Initialize</c>) — there is no user-facing
/// setting for this, per task requirement H2. No per-tick Info logging is ever emitted by the
/// host regardless of level, to keep the file small (spec §7's ~1 MB budget).
/// </para>
/// </remarks>
public sealed class FileLogger
{
    private const long RollSizeBytes = 1L * 1024 * 1024; // ~1 MB

    private readonly Lock _gate = new();
    private readonly string _logFilePath;
    private readonly string _backupFilePath;
    private readonly bool _debugEnabled;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileLogger"/> class, logging to
    /// <c>syspulse.log</c> (and, once rolled, <c>syspulse.1.log</c>) under <paramref name="logDirectory"/>.
    /// </summary>
    /// <param name="logDirectory">
    /// The directory to log into. Created if it doesn't exist (best-effort; a failure here is
    /// swallowed the same as a write failure — this constructor never throws).
    /// </param>
    /// <param name="debugEnabled">Whether <see cref="Debug"/> entries are actually written. Default <see langword="false"/>.</param>
    public FileLogger(string logDirectory, bool debugEnabled = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(logDirectory);

        _debugEnabled = debugEnabled;
        _logFilePath = Path.Combine(logDirectory, "syspulse.log");
        _backupFilePath = Path.Combine(logDirectory, "syspulse.1.log");

        try
        {
            Directory.CreateDirectory(logDirectory);
        }
        catch
        {
            // Best-effort; every Write() call below independently swallows failures too.
        }
    }

    /// <summary>The full path of the primary log file (whether or not it currently exists).</summary>
    public string LogFilePath => _logFilePath;

    /// <summary>Logs an <see cref="LogLevel.Info"/> entry.</summary>
    public void Info(string message) => Write(LogLevel.Info, message);

    /// <summary>Logs a <see cref="LogLevel.Warning"/> entry.</summary>
    public void Warning(string message) => Write(LogLevel.Warning, message);

    /// <summary>Logs an <see cref="LogLevel.Error"/> entry.</summary>
    public void Error(string message) => Write(LogLevel.Error, message);

    /// <summary>Logs an <see cref="LogLevel.Error"/> entry with an exception's details appended.</summary>
    public void Error(string message, Exception exception) =>
        Write(LogLevel.Error, exception is null ? message : $"{message}: {exception}");

    /// <summary>Logs a <see cref="LogLevel.Debug"/> entry. No-op unless the logger was constructed with <c>debugEnabled: true</c>.</summary>
    public void Debug(string message) => Write(LogLevel.Debug, message);

    private void Write(LogLevel level, string message)
    {
        if (level == LogLevel.Debug && !_debugEnabled)
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                RollIfNeeded();
                string line = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss.fff zzz} [{1}] {2}{3}",
                    DateTimeOffset.Now,
                    LevelText(level),
                    message,
                    Environment.NewLine);
                File.AppendAllText(_logFilePath, line);
            }
        }
        catch
        {
            // Never throw from logging (task requirement H2 / spec §7 resilience).
        }
    }

    private void RollIfNeeded()
    {
        try
        {
            var info = new FileInfo(_logFilePath);
            if (!info.Exists || info.Length < RollSizeBytes)
            {
                return;
            }

            File.Move(_logFilePath, _backupFilePath, overwrite: true);
        }
        catch
        {
            // If rolling fails (e.g. the backup is locked by another reader), keep appending to
            // the existing primary file rather than losing the new entry.
        }
    }

    private static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        _ => "INFO",
    };
}

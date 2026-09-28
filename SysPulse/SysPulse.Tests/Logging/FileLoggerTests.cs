using System.IO;
using System.Threading.Tasks;
using SysPulse.Core.Logging;

namespace SysPulse.Tests.Logging;

/// <summary>Covers <see cref="FileLogger"/> (task requirement H2): rolling, concurrent writes, and never throwing.</summary>
public class FileLoggerTests : IDisposable
{
    private readonly string _dir;

    public FileLoggerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SysPulseTests_FileLogger_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; never fail the test run over a locked temp file.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Info_WritesLineContainingLevelAndMessage()
    {
        var logger = new FileLogger(_dir);

        logger.Info("hello world");

        string content = File.ReadAllText(logger.LogFilePath);
        Assert.Contains("[INFO]", content, StringComparison.Ordinal);
        Assert.Contains("hello world", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Debug_DisabledByDefault_WritesNothing()
    {
        var logger = new FileLogger(_dir);

        logger.Debug("should not appear");

        Assert.False(File.Exists(logger.LogFilePath));
    }

    [Fact]
    public void Debug_WhenEnabled_Writes()
    {
        var logger = new FileLogger(_dir, debugEnabled: true);

        logger.Debug("shows up");

        string content = File.ReadAllText(logger.LogFilePath);
        Assert.Contains("[DEBUG]", content, StringComparison.Ordinal);
        Assert.Contains("shows up", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Error_WithException_IncludesExceptionDetails()
    {
        var logger = new FileLogger(_dir);
        var ex = new InvalidOperationException("boom");

        logger.Error("kill failed", ex);

        string content = File.ReadAllText(logger.LogFilePath);
        Assert.Contains("[ERROR]", content, StringComparison.Ordinal);
        Assert.Contains("kill failed", content, StringComparison.Ordinal);
        Assert.Contains("boom", content, StringComparison.Ordinal);
    }

    [Fact]
    public void RollsToBackupFile_WhenPrimaryExceedsRollSize()
    {
        var logger = new FileLogger(_dir);

        // ~1 MB roll threshold: one ~2000-byte line, ~600 times, comfortably exceeds it.
        string line = new string('x', 2000);
        for (int i = 0; i < 600; i++)
        {
            logger.Info(line);
        }

        string backupPath = Path.Combine(_dir, "syspulse.1.log");
        Assert.True(File.Exists(backupPath), "Expected a rolled-over syspulse.1.log backup to exist.");
        Assert.True(File.Exists(logger.LogFilePath), "Expected a fresh primary log file after rolling.");

        // The fresh primary file must be smaller than the pre-roll size (i.e. it actually reset).
        var primaryInfo = new FileInfo(logger.LogFilePath);
        var backupInfo = new FileInfo(backupPath);
        Assert.True(primaryInfo.Length < backupInfo.Length);
    }

    [Fact]
    public void RollsOnlyOneGeneration_OldestBackupIsOverwritten()
    {
        var logger = new FileLogger(_dir);
        string line = new string('x', 2000);

        // Roll twice over: enough writes to trigger at least two roll cycles.
        for (int i = 0; i < 1200; i++)
        {
            logger.Info(line);
        }

        // Exactly the primary + one backup should exist -- no .2.log or similar.
        string[] logFiles = Directory.GetFiles(_dir, "*.log");
        Assert.Equal(2, logFiles.Length);
    }

    [Fact]
    public async Task ConcurrentWrites_FromManyThreads_NeverThrowsAndAllLinesLand()
    {
        var logger = new FileLogger(_dir);
        const int threadCount = 8;
        const int perThread = 50;

        var tasks = new Task[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            int threadIndex = t;
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < perThread; i++)
                {
                    logger.Info($"thread {threadIndex} message {i}");
                }
            });
        }

        // Must not throw (AggregateException would surface here if any Task faulted).
        await Task.WhenAll(tasks);

        string content = File.ReadAllText(logger.LogFilePath);
        int lineCount = content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.Equal(threadCount * perThread, lineCount);
    }

    [Fact]
    public void Write_ToUnwritableDirectory_NeverThrows()
    {
        // A file path used as if it were a directory: Directory.CreateDirectory and every
        // subsequent File.AppendAllText call will fail -- FileLogger must swallow all of it.
        string blockerFile = Path.Combine(Path.GetTempPath(), "SysPulseTests_Blocker_" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blockerFile, "not a directory");
        try
        {
            string bogusDir = Path.Combine(blockerFile, "logs");
            var logger = new FileLogger(bogusDir);

            Exception? caught = Record.Exception(() =>
            {
                logger.Info("info");
                logger.Warning("warning");
                logger.Error("error");
                logger.Error("error with ex", new InvalidOperationException("x"));
                logger.Debug("debug"); // no-op (disabled by default) but must still not throw
            });

            Assert.Null(caught);
        }
        finally
        {
            File.Delete(blockerFile);
        }
    }
}

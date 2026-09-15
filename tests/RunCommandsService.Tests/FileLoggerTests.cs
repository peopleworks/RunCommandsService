using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Xunit;
using RunCommandsService;

namespace RunCommandsService.Tests;

public class FileLoggerTests : IDisposable
{
    private readonly string _tempLogDir;

    public FileLoggerTests()
    {
        _tempLogDir = Path.Combine(Path.GetTempPath(), "RunCommandsService_FileLoggerTests_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempLogDir))
            {
                Directory.Delete(_tempLogDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void FileLogger_WritesLogToFile_WhenLevelIsAllowed()
    {
        var options = new FileLoggerOptions
        {
            LogDirectory = _tempLogDir,
            MinLevel = LogLevel.Information,
            FileSizeLimit = 1024 * 1024,
            RetainDays = 7
        };

        var logger = new FileLogger("TestCategory", options);
        logger.LogInformation("Test log message");

        var targetDir = Path.IsPathRooted(_tempLogDir) 
            ? _tempLogDir 
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _tempLogDir);

        Assert.True(Directory.Exists(targetDir));

        var logFiles = Directory.GetFiles(targetDir, "log_*.txt");
        Assert.Single(logFiles);

        var content = File.ReadAllText(logFiles[0]);
        Assert.Contains("[Information] Test log message", content);
    }

    [Fact]
    public void FileLogger_IgnoresLogs_BelowMinLevel()
    {
        var options = new FileLoggerOptions
        {
            LogDirectory = _tempLogDir,
            MinLevel = LogLevel.Warning,
            FileSizeLimit = 1024 * 1024,
            RetainDays = 7
        };

        var logger = new FileLogger("TestCategory", options);
        logger.LogInformation("Ignored info log");

        var targetDir = Path.IsPathRooted(_tempLogDir) 
            ? _tempLogDir 
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _tempLogDir);

        if (Directory.Exists(targetDir))
        {
            var logFiles = Directory.GetFiles(targetDir, "log_*.txt");
            Assert.Empty(logFiles);
        }
    }

    [Fact]
    public void FileLogger_RotatesFile_WhenFileSizeLimitExceeded()
    {
        var options = new FileLoggerOptions
        {
            LogDirectory = _tempLogDir,
            MinLevel = LogLevel.Information,
            FileSizeLimit = 50, // Small limit to trigger rotation
            RetainDays = 7
        };

        var logger = new FileLogger("TestCategory", options);
        
        // First log entry will exceed 50 bytes
        logger.LogInformation("Long log message that exceeds fifty bytes threshold");
        // Second log entry should trigger rotation
        logger.LogInformation("Second log message after rotation");

        var targetDir = Path.IsPathRooted(_tempLogDir) 
            ? _tempLogDir 
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _tempLogDir);

        var logFiles = Directory.GetFiles(targetDir, "log_*.txt");

        Assert.True(logFiles.Length >= 2, $"Expected at least 2 log files due to rotation, found {logFiles.Length}");
    }
}

using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;

namespace RunCommandsService;

public class FileLogger : ILogger
{
    private readonly string _name;
    private readonly FileLoggerOptions _options;
    private static readonly object _lock = new object();
    private static DateTime _lastCleanupUtc = DateTime.MinValue;

    public FileLogger(string name, FileLoggerOptions options)
    {
        _name = name;
        _options = options;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _options.MinLevel;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _options.LogDirectory);
        var message = formatter(state, exception);
        var formattedMessage = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} [{logLevel}] {message}";
        if (exception != null)
        {
            formattedMessage += $"\nException: {exception}\nStackTrace: {exception.StackTrace}";
        }

        lock (_lock)
        {
            try
            {
                if (!Directory.Exists(logDirectory))
                {
                    Directory.CreateDirectory(logDirectory);
                }

                var todayStr = DateTime.UtcNow.ToString("yyyy-MM-dd");
                var logFile = Path.Combine(logDirectory, $"log_{todayStr}.txt");

                // Check size limit and rotate if needed
                if (_options.FileSizeLimit > 0 && File.Exists(logFile))
                {
                    var fi = new FileInfo(logFile);
                    if (fi.Length >= _options.FileSizeLimit)
                    {
                        var timestamp = DateTime.UtcNow.ToString("HHmmss_fff");
                        var rolled = Path.Combine(logDirectory, $"log_{todayStr}_{timestamp}.txt");
                        try
                        {
                            File.Move(logFile, rolled, overwrite: true);
                        }
                        catch
                        {
                            // If move fails, continue appending to current logFile
                        }
                    }
                }

                File.AppendAllText(logFile, formattedMessage + Environment.NewLine);

                // Run cleanup periodically (at most once every 1 hour)
                if ((DateTime.UtcNow - _lastCleanupUtc).TotalHours >= 1)
                {
                    _lastCleanupUtc = DateTime.UtcNow;
                    CleanupOldLogsInternal(logDirectory);
                }
            }
            catch
            {
                // Prevent logger from throwing to callers
            }
        }
    }

    private void CleanupOldLogsInternal(string logDirectory)
    {
        if (_options.RetainDays <= 0) return;

        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-_options.RetainDays);
            var files = Directory.GetFiles(logDirectory, "log_*.txt")
                .Select(f => new FileInfo(f))
                .Where(f => f.LastWriteTimeUtc < cutoff);

            foreach (var file in files)
            {
                try
                {
                    file.Delete();
                }
                catch
                {
                    // Ignore deletion errors for locked files
                }
            }
        }
        catch
        {
            // Ignore directory search errors
        }
    }
}

public class FileLoggerOptions
{
    public string LogDirectory { get; set; } = "Logs";

    public long FileSizeLimit { get; set; } = 10 * 1024 * 1024; // 10MB default

    public int RetainDays { get; set; } = 30;

    public LogLevel MinLevel { get; set; } = LogLevel.Information;
}

public class FileLoggerProvider : ILoggerProvider
{
    private readonly FileLoggerOptions _options;

    public FileLoggerProvider(FileLoggerOptions options)
    {
        _options = options;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new FileLogger(categoryName, _options);
    }

    public void Dispose()
    {
    }
}

public static class FileLoggerExtensions
{
    public static ILoggingBuilder AddFileLogger(this ILoggingBuilder builder, Action<FileLoggerOptions> configure)
    {
        var options = new FileLoggerOptions();
        configure(options);
        builder.AddProvider(new FileLoggerProvider(options));
        return builder;
    }
}

using Microsoft.Extensions.Logging;

public class FileLogger : ILogger
{
    private readonly string _name;
    private readonly FileLoggerOptions _options;
    private static readonly object _lock = new object();

    public FileLogger(string name, FileLoggerOptions options)
    {
        _name = name;
        _options = options;
    }

    public IDisposable BeginScope<TState>(TState state) => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception exception,
        Func<TState, Exception, string> formatter)
    {
        if(!IsEnabled(logLevel))
            return;

        var logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _options.LogDirectory);
        Directory.CreateDirectory(logDirectory);

        var logFile = Path.Combine(logDirectory, $"log_{DateTime.Now:yyyy-MM-dd}.txt");
        var formattedMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{logLevel}] {formatter(state, exception)}";
        if(exception != null)
        {
            formattedMessage += $"\nException: {exception}\nStackTrace: {exception.StackTrace}";
        }

        lock(_lock)
        {
            File.AppendAllText(logFile, formattedMessage + Environment.NewLine);
        }

        // Cleanup old logs
        CleanupOldLogs(logDirectory);
    }

    private void CleanupOldLogs(string logDirectory)
    {
        var files = Directory.GetFiles(logDirectory, "log_*.txt")
            .Select(f => new FileInfo(f))
            .Where(f => f.CreationTime < DateTime.Now.AddDays(-_options.RetainDays));

        foreach(var file in files)
        {
            try
            {
                file.Delete();
            } catch
            {
            } // Ignore deletion errors
        }
    }
}

public class FileLoggerOptions
{
    public string LogDirectory { get; set; }

    public long FileSizeLimit { get; set; }

    public int RetainDays { get; set; }
}

public class FileLoggerProvider : ILoggerProvider
{
    private readonly FileLoggerOptions _options;

    public FileLoggerProvider(FileLoggerOptions options) { _options = options; }

    public ILogger CreateLogger(string categoryName) { return new FileLogger(categoryName, _options); }

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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RunCommandsService.Tests;

public sealed class ExecutionHistoryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rce-history-{Guid.NewGuid():N}");
    private readonly string _databasePath;

    public ExecutionHistoryStoreTests()
    {
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "executions.db");
    }

    [Fact]
    public void Append_PersistsHistoryAcrossStoreInstances()
    {
        var store = CreateStore();
        store.Append(Event("alpha", success: true, attempts: 1, durationMs: 125));

        var reopened = CreateStore();
        var recent = reopened.GetRecent(10);

        var execution = Assert.Single(recent);
        Assert.Equal("alpha", execution.CommandId);
        Assert.True(execution.Success);
        Assert.Equal(125, execution.DurationMs);
        Assert.Equal("manual", execution.TriggerSource);
    }

    [Fact]
    public void GetMetrics_AggregatesRetriesFailuresAndDurationsPerJob()
    {
        var store = CreateStore();
        store.Append(Event("alpha", success: false, attempts: 3, durationMs: 300, exhausted: true, timedOut: true));
        store.Append(Event("alpha", success: true, attempts: 2, durationMs: 100));

        var metric = Assert.Single(store.GetMetrics());
        Assert.Equal(2, metric.TotalRuns);
        Assert.Equal(1, metric.SuccessfulRuns);
        Assert.Equal(1, metric.FailedRuns);
        Assert.Equal(2, metric.RunsWithRetries);
        Assert.Equal(3, metric.TotalRetries);
        Assert.Equal(1, metric.RetryExhaustions);
        Assert.Equal(1, metric.TimeoutCount);
        Assert.Equal(200, metric.AverageDurationMs);
        Assert.Equal(300, metric.MaxDurationMs);
        Assert.Equal(100, metric.LastDurationMs);
    }

    [Fact]
    public void GetRecent_FiltersByJobIdCaseInsensitively()
    {
        var store = CreateStore();
        store.Append(Event("Alpha", success: true, attempts: 1, durationMs: 10));
        store.Append(Event("beta", success: true, attempts: 1, durationMs: 20));

        var recent = store.GetRecent(10, "ALPHA");

        Assert.Single(recent);
        Assert.Equal("Alpha", recent[0].CommandId);
    }

    private ExecutionHistoryStore CreateStore()
    {
        var options = Options.Create(new MonitoringOptions
        {
            ExecutionHistory = new ExecutionHistoryOptions
            {
                Enabled = true,
                DatabasePath = _databasePath,
                RetentionDays = 30,
                MaxRecords = 1000
            }
        });
        return new ExecutionHistoryStore(options, NullLogger<ExecutionHistoryStore>.Instance);
    }

    private static ExecutionEvent Event(
        string commandId,
        bool success,
        int attempts,
        int durationMs,
        bool exhausted = false,
        bool timedOut = false)
    {
        var end = DateTime.UtcNow;
        return new ExecutionEvent
        {
            CommandId = commandId,
            Command = "cmd /c exit 0",
            StartUtc = end.AddMilliseconds(-durationMs),
            EndUtc = end,
            Success = success,
            ExitCode = success ? 0 : 1,
            DurationMs = durationMs,
            AttemptCount = attempts,
            MaxAttempts = 3,
            RetryExhausted = exhausted,
            TimedOut = timedOut,
            TriggerSource = "manual"
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}

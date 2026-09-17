using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;

namespace RunCommandsService;

public sealed class ExecutionHistoryOptions
{
    public bool Enabled { get; set; } = true;
    public string DatabasePath { get; set; } = "Data/executions.db";
    public int RetentionDays { get; set; } = 90;
    public int MaxRecords { get; set; } = 100_000;
}

public sealed class JobExecutionMetrics
{
    public string CommandId { get; set; } = string.Empty;
    public long TotalRuns { get; set; }
    public long SuccessfulRuns { get; set; }
    public long FailedRuns { get; set; }
    public long SkippedRuns { get; set; }
    public long RunsWithRetries { get; set; }
    public long TotalRetries { get; set; }
    public long RetryExhaustions { get; set; }
    public long TimeoutCount { get; set; }
    public double AverageDurationMs { get; set; }
    public int MaxDurationMs { get; set; }
    public int LastDurationMs { get; set; }
    public DateTime? LastEndUtc { get; set; }
}

/// <summary>Thread-safe SQLite persistence for final logical execution results.</summary>
public sealed class ExecutionHistoryStore
{
    private readonly ExecutionHistoryOptions _options;
    private readonly ILogger<ExecutionHistoryStore> _logger;
    private readonly object _gate = new();
    private readonly string _connectionString;
    private DateOnly _lastCleanupUtc;

    public ExecutionHistoryStore(IOptions<MonitoringOptions> options, ILogger<ExecutionHistoryStore> logger)
    {
        _options = options.Value.ExecutionHistory ?? new ExecutionHistoryOptions();
        _logger = logger;

        var configuredPath = string.IsNullOrWhiteSpace(_options.DatabasePath)
            ? "Data/executions.db"
            : _options.DatabasePath;
        var databasePath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(AppContext.BaseDirectory, configuredPath);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();

        if (_options.Enabled)
            Initialize(databasePath);
    }

    public bool Enabled => _options.Enabled;

    public void Append(ExecutionEvent execution)
    {
        if (!_options.Enabled)
            return;

        try
        {
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO execution_history
                    (command_id, command_text, start_utc, end_utc, exit_code, success,
                     skipped_due_to_conflict, error, duration_ms, attempt_count, max_attempts,
                     retry_exhausted, timed_out, trigger_source)
                    VALUES
                    ($commandId, $command, $startUtc, $endUtc, $exitCode, $success,
                     $skipped, $error, $durationMs, $attemptCount, $maxAttempts,
                     $retryExhausted, $timedOut, $triggerSource);
                    """;
                command.Parameters.AddWithValue("$commandId", execution.CommandId);
                command.Parameters.AddWithValue("$command", execution.Command);
                command.Parameters.AddWithValue("$startUtc", ToSqlDate(execution.StartUtc));
                command.Parameters.AddWithValue("$endUtc", ToSqlDate(execution.EndUtc));
                command.Parameters.AddWithValue("$exitCode", (object?)execution.ExitCode ?? DBNull.Value);
                command.Parameters.AddWithValue("$success", execution.Success ? 1 : 0);
                command.Parameters.AddWithValue("$skipped", execution.SkippedDueToConflict ? 1 : 0);
                command.Parameters.AddWithValue("$error", (object?)execution.Error ?? DBNull.Value);
                command.Parameters.AddWithValue("$durationMs", execution.DurationMs);
                command.Parameters.AddWithValue("$attemptCount", execution.AttemptCount);
                command.Parameters.AddWithValue("$maxAttempts", execution.MaxAttempts);
                command.Parameters.AddWithValue("$retryExhausted", execution.RetryExhausted ? 1 : 0);
                command.Parameters.AddWithValue("$timedOut", execution.TimedOut ? 1 : 0);
                command.Parameters.AddWithValue("$triggerSource", execution.TriggerSource);
                command.ExecuteNonQuery();
                CleanupIfDue(connection);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to persist execution history for {CommandId}", execution.CommandId);
        }
    }

    public IReadOnlyList<ExecutionEvent> GetRecent(int limit = 100, string? commandId = null)
    {
        if (!_options.Enabled)
            return Array.Empty<ExecutionEvent>();

        var boundedLimit = Math.Clamp(limit, 1, 5000);
        try
        {
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT command_id, command_text, start_utc, end_utc, exit_code, success,
                           skipped_due_to_conflict, error, duration_ms, attempt_count, max_attempts,
                           retry_exhausted, timed_out, trigger_source
                    FROM execution_history
                    WHERE ($commandId IS NULL OR command_id = $commandId COLLATE NOCASE)
                    ORDER BY id DESC
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$commandId", (object?)commandId ?? DBNull.Value);
                command.Parameters.AddWithValue("$limit", boundedLimit);

                var events = new List<ExecutionEvent>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    events.Add(ReadEvent(reader));
                return events;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to read execution history");
            return Array.Empty<ExecutionEvent>();
        }
    }

    public IReadOnlyList<JobExecutionMetrics> GetMetrics()
    {
        if (!_options.Enabled)
            return Array.Empty<JobExecutionMetrics>();

        try
        {
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    WITH ranked AS (
                        SELECT *, ROW_NUMBER() OVER (PARTITION BY command_id ORDER BY id DESC) AS rn
                        FROM execution_history
                    )
                    SELECT command_id,
                           COUNT(*) AS total_runs,
                           SUM(CASE WHEN success = 1 AND skipped_due_to_conflict = 0 THEN 1 ELSE 0 END) AS successful_runs,
                           SUM(CASE WHEN success = 0 AND skipped_due_to_conflict = 0 THEN 1 ELSE 0 END) AS failed_runs,
                           SUM(skipped_due_to_conflict) AS skipped_runs,
                           SUM(CASE WHEN attempt_count > 1 THEN 1 ELSE 0 END) AS runs_with_retries,
                           SUM(CASE WHEN attempt_count > 1 THEN attempt_count - 1 ELSE 0 END) AS total_retries,
                           SUM(retry_exhausted) AS retry_exhaustions,
                           SUM(timed_out) AS timeout_count,
                           COALESCE(AVG(CASE WHEN skipped_due_to_conflict = 0 THEN duration_ms END), 0) AS avg_duration,
                           COALESCE(MAX(CASE WHEN skipped_due_to_conflict = 0 THEN duration_ms END), 0) AS max_duration,
                           COALESCE(MAX(CASE WHEN rn = 1 THEN duration_ms END), 0) AS last_duration,
                           MAX(CASE WHEN rn = 1 THEN end_utc END) AS last_end_utc
                    FROM ranked
                    GROUP BY command_id
                    ORDER BY command_id COLLATE NOCASE;
                    """;

                var metrics = new List<JobExecutionMetrics>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    metrics.Add(new JobExecutionMetrics
                    {
                        CommandId = reader.GetString(0),
                        TotalRuns = reader.GetInt64(1),
                        SuccessfulRuns = reader.GetInt64(2),
                        FailedRuns = reader.GetInt64(3),
                        SkippedRuns = reader.GetInt64(4),
                        RunsWithRetries = reader.GetInt64(5),
                        TotalRetries = reader.GetInt64(6),
                        RetryExhaustions = reader.GetInt64(7),
                        TimeoutCount = reader.GetInt64(8),
                        AverageDurationMs = reader.GetDouble(9),
                        MaxDurationMs = reader.GetInt32(10),
                        LastDurationMs = reader.GetInt32(11),
                        LastEndUtc = reader.IsDBNull(12) ? null : ParseSqlDate(reader.GetString(12))
                    });
                }
                return metrics;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to calculate execution metrics");
            return Array.Empty<JobExecutionMetrics>();
        }
    }

    private void Initialize(string databasePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            lock (_gate)
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    PRAGMA journal_mode=WAL;
                    PRAGMA synchronous=NORMAL;
                    CREATE TABLE IF NOT EXISTS execution_history (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        command_id TEXT NOT NULL,
                        command_text TEXT NOT NULL,
                        start_utc TEXT NOT NULL,
                        end_utc TEXT NOT NULL,
                        exit_code INTEGER NULL,
                        success INTEGER NOT NULL,
                        skipped_due_to_conflict INTEGER NOT NULL,
                        error TEXT NULL,
                        duration_ms INTEGER NOT NULL,
                        attempt_count INTEGER NOT NULL,
                        max_attempts INTEGER NOT NULL,
                        retry_exhausted INTEGER NOT NULL,
                        timed_out INTEGER NOT NULL,
                        trigger_source TEXT NOT NULL DEFAULT 'scheduled'
                    );
                    CREATE INDEX IF NOT EXISTS ix_execution_history_end_utc ON execution_history(end_utc DESC);
                    CREATE INDEX IF NOT EXISTS ix_execution_history_command_id ON execution_history(command_id, id DESC);
                    """;
                command.ExecuteNonQuery();
                CleanupIfDue(connection, force: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to initialize SQLite execution history at {DatabasePath}", databasePath);
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void CleanupIfDue(SqliteConnection connection, bool force = false)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (!force && today == _lastCleanupUtc)
            return;

        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM execution_history WHERE end_utc < $cutoff;
            DELETE FROM execution_history
            WHERE id NOT IN (SELECT id FROM execution_history ORDER BY id DESC LIMIT $maxRecords);
            """;
        command.Parameters.AddWithValue("$cutoff", ToSqlDate(DateTime.UtcNow.AddDays(-Math.Max(1, _options.RetentionDays))));
        command.Parameters.AddWithValue("$maxRecords", Math.Max(100, _options.MaxRecords));
        command.ExecuteNonQuery();
        _lastCleanupUtc = today;
    }

    private static ExecutionEvent ReadEvent(SqliteDataReader reader) => new()
    {
        CommandId = reader.GetString(0),
        Command = reader.GetString(1),
        StartUtc = ParseSqlDate(reader.GetString(2)),
        EndUtc = ParseSqlDate(reader.GetString(3)),
        ExitCode = reader.IsDBNull(4) ? null : reader.GetInt32(4),
        Success = reader.GetInt32(5) != 0,
        SkippedDueToConflict = reader.GetInt32(6) != 0,
        Error = reader.IsDBNull(7) ? null : reader.GetString(7),
        DurationMs = reader.GetInt32(8),
        AttemptCount = reader.GetInt32(9),
        MaxAttempts = reader.GetInt32(10),
        RetryExhausted = reader.GetInt32(11) != 0,
        TimedOut = reader.GetInt32(12) != 0,
        TriggerSource = reader.GetString(13)
    };

    private static string ToSqlDate(DateTime value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTime ParseSqlDate(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}

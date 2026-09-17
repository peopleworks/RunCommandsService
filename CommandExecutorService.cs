using AsyncKeyedLock;
using Cronos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json.Serialization;

namespace RunCommandsService
{
    public sealed record ManualRunResult(bool Accepted, string? Error = null);

    public interface IManualJobRunner
    {
        ManualRunResult QueueManualRun(string jobId);
    }

    public class CommandExecutorService : BackgroundService, IManualJobRunner
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<CommandExecutorService> _logger;
        private readonly ExecutionMonitor _monitor;
        private readonly AsyncKeyedLocker<string> _concurrency;
        private readonly SchedulerOptions _schedOptions;
        private readonly IHostApplicationLifetime _applicationLifetime;
        private FileSystemWatcher? _configWatcher;
        private readonly AsyncNonKeyedLocker _parallelism;

        private List<ScheduledCommand?> _commands = new();
        private readonly Lock _lockObject = new();

        // Next run storage (UTC) by job id
        private readonly ConcurrentDictionary<string, DateTime?> _nextRunUtc = new();

        // Track invalid jobs so we log each only once until fixed
        private readonly HashSet<string> _invalidScheduleLogged = new(StringComparer.OrdinalIgnoreCase);

        private DateTime _lastReload = DateTime.MinValue;

        // Scheduler health tracking
        private DateTime _lastSchedulerHeartbeat = DateTime.MinValue;
        private int _schedulerErrorCount = 0;
        private readonly Lock _healthLock = new();


        public CommandExecutorService(
            IConfiguration configuration,
            IOptions<SchedulerOptions> schedOptions,
            ExecutionMonitor monitor,
            AsyncKeyedLocker<string> concurrency,
            IHostApplicationLifetime applicationLifetime,
            ILogger<CommandExecutorService> logger)
        {
            _configuration = configuration;
            _schedOptions = schedOptions.Value;
            _monitor = monitor;
            _concurrency = concurrency;
            _applicationLifetime = applicationLifetime;
            _logger = logger;

            _parallelism = new(Math.Max(1, _schedOptions.MaxParallelism));

            // Initialize TimeZoneHelper with logger for diagnostics
            TimeZoneHelper.Initialize(logger);

            // Register scheduler health provider with monitor
            _monitor.SetSchedulerHealthProvider(GetSchedulerHealth);

            LoadCommands();
            SetupConfigurationWatcher();
        }

        // ---------- Helpers ----------

        private static bool TryParseCron(string text, out CronExpression? cron, out string? error)
        {
            try
            {
                cron = CronExpression.Parse(text);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                cron = null;
                error = ex.Message;
                return false;
            }
        }

        private static TimeZoneInfo TZ(string tz)
        {
            if (string.IsNullOrWhiteSpace(tz))
                return TimeZoneInfo.Utc;
            return TimeZoneHelper.FindTimeZone(tz);
        }


        // Compute next run using Cronos with explicit TimeZoneInfo. Base time MUST be UTC per Cronos contract.
        private static DateTime? SafeNextOccurrenceUtc(CronExpression cron, DateTime utcNow, TimeZoneInfo tz)
        {
            try
            {
                // Cronos returns UTC when a time zone is provided and base time is UTC
                var nextUtc = cron.GetNextOccurrence(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), tz);
                return nextUtc;
            }
            catch
            {
                return null;
            }
        }

        // Local (Unspecified) → UTC with DST safety
        private static DateTime ConvertLocalToUtc(DateTime localUnspec, TimeZoneInfo tz)
        {
            // Spring-forward "skipped" local times — nudge forward to a valid instant
            if (tz.IsInvalidTime(localUnspec))
                localUnspec = localUnspec.AddHours(1);

            // For ambiguous times (fall-back), ConvertTimeToUtc() will choose standard time by default.
            return TimeZoneInfo.ConvertTimeToUtc(localUnspec, tz);
        }


        private void RefreshMonitorSnapshot()
        {
            var now = DateTime.UtcNow;
            List<ScheduledCommand> snapshot;
            lock (_lockObject)
                snapshot = _commands.OfType<ScheduledCommand>().ToList();

            var schedule = snapshot.Select(
                c =>
                {
                    string? nextRun = null;
                    if (c.Cron != null)
                    {
                        var next = SafeNextOccurrenceUtc(c.Cron, now, TZ(c.TimeZone));
                        if (next.HasValue)
                            nextRun = next.Value.ToString("o");
                    }

                    return new ScheduledCommandView
                    {
                        Id = c.Id,
                        Command = c.Command,
                        CronExpression = c.CronExpression,
                        TimeZone = string.IsNullOrWhiteSpace(c.TimeZone) ? "UTC" : c.TimeZone,
                        Enabled = c.Enabled,
                        AllowParallelRuns = c.AllowParallelRuns,
                        ConcurrencyKey = c.ConcurrencyKey ?? string.Empty,
                        MaxRuntimeMinutes = c.MaxRuntimeMinutes,
                        Retry = c.Retry ?? new RetryOptions(),
                        NextRunUtc = nextRun,
                        CustomAlertMessage = c.CustomAlertMessage
                    };
                });

            _monitor.UpdateScheduleSnapshot(schedule);
        }

        // ---------- Config loading ----------

        private void LoadCommands()
        {
            lock (_lockObject)
            {
                _commands = _configuration.GetSection("ScheduledCommands").Get<List<ScheduledCommand?>>() ??
                    new List<ScheduledCommand?>();

                var now = DateTime.UtcNow;
                var validJobs = 0;
                var invalidCronJobs = 0;
                var invalidTimezoneJobs = 0;
                var disabledJobs = 0;
                var validationIssues = new List<string>();
                var configurationValidation = ConfigValidator.Validate(_configuration);
                foreach (var problem in configurationValidation.ConfigurationProblems)
                    _logger.LogError("Configuration error: {Problem}", problem);
                foreach (var warning in configurationValidation.SecurityWarnings)
                    _logger.LogWarning("Configuration security warning: {Warning}", warning);

                var duplicateIds = _commands
                    .OfType<ScheduledCommand>()
                    .Where(c => !string.IsNullOrWhiteSpace(c.Id))
                    .GroupBy(c => c.Id.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var c in _commands)
                {
                    if (c == null)
                    {
                        invalidCronJobs++;
                        validationIssues.Add("  • Null job entry: job will be skipped");
                        if (_invalidScheduleLogged.Add("(null job)"))
                            _logger.LogError("Null job entry found. Job will be skipped until fixed.");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(c.Id))
                        c.Id = c.Command;

                    if (string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Command))
                    {
                        var invalidKey = string.IsNullOrWhiteSpace(c.Id) ? "(unnamed job)" : c.Id;
                        var missingError = string.IsNullOrWhiteSpace(c.Id) ? "missing Id and Command" : "missing Command";
                        c.Cron = null;
                        if (!string.IsNullOrWhiteSpace(c.Id))
                            _nextRunUtc[c.Id] = null;
                        if (c.Enabled)
                        {
                            invalidCronJobs++;
                            if (_invalidScheduleLogged.Add(invalidKey))
                                _logger.LogError("Job {Id}: {Error}. Job will be skipped until fixed.", invalidKey, missingError);
                            validationIssues.Add($"  • Job '{invalidKey}': {missingError}");
                        }
                        else
                        {
                            disabledJobs++;
                        }
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(c.TimeZone))
                        c.TimeZone = _schedOptions.DefaultTimeZone;

                    var jobId = c.Id;
                    if (duplicateIds.Contains(jobId.Trim()))
                    {
                        c.Cron = null;
                        _nextRunUtc[jobId] = null;
                        if (c.Enabled)
                        {
                            invalidCronJobs++;
                            const string duplicateError = "duplicate Id (job IDs are case-insensitive)";
                            if (_invalidScheduleLogged.Add(jobId))
                                _logger.LogError("Job {Id}: {Error}. Job will be skipped until fixed.", jobId, duplicateError);
                            validationIssues.Add($"  • Job '{jobId}': {duplicateError}");
                        }
                        else
                        {
                            disabledJobs++;
                        }
                        continue;
                    }

                    // Allow logging again if a previously-bad cron was fixed
                    _invalidScheduleLogged.Remove(jobId);

                    // Validate timezone with detailed result
                    var tzResult = TimeZoneHelper.FindTimeZoneWithResult(c.TimeZone);
                    if (tzResult.FellBackToUtc && c.Enabled)
                    {
                        invalidTimezoneJobs++;
                        validationIssues.Add($"  • Job '{c.Id}': Invalid timezone '{tzResult.OriginalId}' → using UTC");
                    }

                    // ---- FIX: declare & init before the condition ----
                    CronExpression? cron = null;
                    string? cronErr = null;
                    bool hasCron = !string.IsNullOrWhiteSpace(c.CronExpression);
                    bool parsed = hasCron && TryParseCron(c.CronExpression, out cron, out cronErr);

                    if (parsed && cron != null)
                    {
                        c.Cron = cron;
                        _nextRunUtc[jobId] = SafeNextOccurrenceUtc(cron, now, TZ(c.TimeZone));

                        if (c.Enabled)
                            validJobs++;
                        else
                            disabledJobs++;
                    }
                    else
                    {
                        c.Cron = null;
                        _nextRunUtc[jobId] = null;

                        // Only complain for enabled jobs
                        if (c.Enabled)
                        {
                            invalidCronJobs++;
                            var err = !hasCron ? "missing CronExpression" : $"invalid CronExpression — {cronErr}";
                            if (_invalidScheduleLogged.Add(jobId))
                                _logger.LogError("Job {Id}: {Error}. Job will be skipped until fixed.", jobId, err);

                            validationIssues.Add($"  • Job '{jobId}': {err}");
                        }
                        else
                        {
                            disabledJobs++;
                        }
                    }
                }

                RefreshMonitorSnapshot();

                // Log comprehensive startup summary
                _logger.LogInformation(
                    "Configuration loaded: {TotalJobs} total jobs | {ValidJobs} valid & enabled | {DisabledJobs} disabled | {InvalidCronJobs} invalid cron | {InvalidTimezoneJobs} timezone warnings",
                    _commands.Count,
                    validJobs,
                    disabledJobs,
                    invalidCronJobs,
                    invalidTimezoneJobs);

                if (validationIssues.Count > 0)
                {
                    _logger.LogWarning(
                        "Configuration validation issues found:\n{Issues}",
                        string.Join("\n", validationIssues));
                }

                if (validJobs == 0 && _commands.Count > 0)
                {
                    _logger.LogWarning(
                        "WARNING: No valid enabled jobs found! All {Count} jobs are either disabled or have configuration errors. Scheduler will run but execute nothing.",
                        _commands.Count);
                }
            }
        }

        private void SetupConfigurationWatcher()
        {
            string configPath = AppDomain.CurrentDomain.BaseDirectory;
            _configWatcher = new FileSystemWatcher
            {
                Path = configPath,
                Filter = "appsettings.json",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime
            };

            _configWatcher.Changed += (sender, e) =>
            {
                try
                {
                    var now = DateTime.UtcNow;
                    if ((now - _lastReload).TotalMilliseconds < 800)
                        return; // debounce
                    _lastReload = now;

                    _logger.LogInformation("Configuration file changed. Reloading commands...");
                    Thread.Sleep(300); // small settle time
                    LoadCommands();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during configuration hot-reload. Previous configuration will remain active.");
                }
            };


            _configWatcher.EnableRaisingEvents = true;
        }

        // ---------- Scheduler loop ----------

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Service started");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Update heartbeat - scheduler loop is alive
                    lock (_healthLock)
                    {
                        _lastSchedulerHeartbeat = DateTime.UtcNow;
                    }

                    // Keep dashboard fresh
                    RefreshMonitorSnapshot();

                    List<ScheduledCommand?> currentCommands;
                    lock (_lockObject)
                        currentCommands = _commands.ToList();

                    var nowUtc = DateTime.UtcNow;

                    foreach (var cmd in currentCommands)
                    {
                        if (cmd == null)
                            continue;
                        if (!cmd.Enabled)
                            continue;
                        if (cmd.Cron == null)
                            continue; // invalid cron or missing → skip

                        // compute or read stored next run
                        var due = _nextRunUtc.GetOrAdd(
                            cmd.Id,
                            _ => SafeNextOccurrenceUtc(cmd.Cron, nowUtc, TZ(cmd.TimeZone)));

                        if (!due.HasValue)
                            continue;

                        if (nowUtc >= due.Value)
                        {
                            // Visibility when things "don't run"
                            _logger.LogDebug("Due @ {Due:o} (now {Now:o}) → launching {Id}", due.Value, nowUtc, cmd.Id);

                            // Do not block the scheduler loop; fire-and-forget with internal concurrency limits
                            _ = Task.Run(() => RunCommandAsync(cmd, stoppingToken), stoppingToken);

                            // schedule next from the due time (+1s) to avoid drift / skips
                            var next = SafeNextOccurrenceUtc(cmd.Cron, due.Value.AddSeconds(1), TZ(cmd.TimeZone));
                            _nextRunUtc[cmd.Id] = next;
                            RefreshMonitorSnapshot();
                        }
                    }

                    // Reset error count on successful iteration
                    lock (_healthLock)
                    {
                        _schedulerErrorCount = 0;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _schedOptions.PollSeconds)), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Scheduler loop cancelled (shutdown).");
                    break;
                }
                catch (TaskCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Scheduler loop task canceled (shutdown).");
                    break;
                }
                catch (Exception ex)
                {
                    int errorCount;
                    lock (_healthLock)
                    {
                        _schedulerErrorCount++;
                        errorCount = _schedulerErrorCount;
                    }

                    _logger.LogError(
                        ex,
                        "Unexpected error in command execution loop (error #{ErrorCount}). Will retry in 10 seconds.",
                        errorCount);

                    // Alert if scheduler is repeatedly failing
                    if (errorCount >= 3)
                    {
                        _logger.LogCritical(
                            "CRITICAL: Scheduler loop has failed {ErrorCount} times consecutively. This may indicate a serious system issue.",
                            errorCount);
                    }

                    // Exponential backoff with cap at 60 seconds
                    var backoffSeconds = Math.Min(10 * Math.Pow(2, Math.Min(errorCount - 1, 3)), 60);
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), stoppingToken);
                    }
                    catch
                    { /* ignore */
                    }
                }
            }
        }

        /// <summary>
        /// Get scheduler health information
        /// </summary>
        public object GetSchedulerHealth()
        {
            lock (_healthLock)
            {
                var now = DateTime.UtcNow;
                var timeSinceHeartbeat = _lastSchedulerHeartbeat == DateTime.MinValue
                    ? (TimeSpan?)null
                    : now - _lastSchedulerHeartbeat;

                var isHealthy = timeSinceHeartbeat.HasValue &&
                                timeSinceHeartbeat.Value.TotalSeconds < (_schedOptions.PollSeconds * 3) &&
                                _schedulerErrorCount == 0;

                return new
                {
                    healthy = isHealthy,
                    lastHeartbeat = _lastSchedulerHeartbeat == DateTime.MinValue
                        ? null
                        : _lastSchedulerHeartbeat.ToString("o"),
                    secondsSinceHeartbeat = timeSinceHeartbeat?.TotalSeconds,
                    consecutiveErrors = _schedulerErrorCount,
                    pollIntervalSeconds = _schedOptions.PollSeconds
                };
            }
        }

        // ---------- Command runner ----------

        public ManualRunResult QueueManualRun(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
                return new ManualRunResult(false, "Job id is required.");

            ScheduledCommand? command;
            lock (_lockObject)
            {
                command = _commands.FirstOrDefault(candidate =>
                    string.Equals(candidate?.Id, jobId, StringComparison.OrdinalIgnoreCase));
            }

            if (command == null)
                return new ManualRunResult(false, "Job not found.");
            if (string.IsNullOrWhiteSpace(command.Command) || string.IsNullOrWhiteSpace(command.Id))
                return new ManualRunResult(false, "Job configuration is invalid.");

            var snapshot = command.CloneForExecution();
            _ = Task.Run(
                () => RunCommandAsync(snapshot, _applicationLifetime.ApplicationStopping, "manual"),
                _applicationLifetime.ApplicationStopping);
            return new ManualRunResult(true);
        }

        private async Task RunCommandAsync(
            ScheduledCommand command,
            CancellationToken ct,
            string triggerSource = "scheduled")
        {
            var retry = RetryPolicy.Normalize(command.Retry);
            var overallStart = DateTime.UtcNow;
            CommandAttemptResult? lastResult = null;
            var attemptsCompleted = 0;

            try
            {
                using var acquired = await _concurrency.ConditionalLockAsync(
                    command.ConcurrencyKey ?? command.Id,
                    !command.AllowParallelRuns,
                    0,
                    ct);

                if (acquired == null)
                {
                    _logger.LogWarning(
                        "Skipping {Id} due to concurrency key in use ({Key})",
                        command.Id,
                        command.ConcurrencyKey);

                    _monitor.Record(
                        new ExecutionEvent
                        {
                            CommandId = command.Id,
                            Command = command.Command,
                            StartUtc = DateTime.UtcNow,
                            EndUtc = DateTime.UtcNow,
                            Success = true,
                            SkippedDueToConflict = true,
                            TriggerSource = triggerSource
                        });
                    return;
                }

                for (var attempt = 1; attempt <= retry.MaxAttempts; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    using (await _parallelism.LockAsync(ct))
                    {
                        lastResult = await ExecuteCommandAttemptAsync(command, attempt, retry.MaxAttempts, ct);
                    }

                    attemptsCompleted = attempt;
                    if (!RetryPolicy.ShouldRetry(
                            retry,
                            attempt,
                            lastResult.Success,
                            lastResult.ExitCode,
                            lastResult.FailureKind))
                        break;

                    var delay = RetryPolicy.CalculateDelay(retry, attempt, Random.Shared.NextDouble());
                    _logger.LogWarning(
                        "Job {Id} attempt {Attempt}/{MaxAttempts} failed ({FailureKind}, ExitCode={ExitCode}). Retrying in {DelaySeconds:F1}s.",
                        command.Id,
                        attempt,
                        retry.MaxAttempts,
                        lastResult.FailureKind,
                        lastResult.ExitCode,
                        delay.TotalSeconds);
                    await Task.Delay(delay, ct);
                }

                if (lastResult == null)
                    return;

                var exhausted = !lastResult.Success &&
                                attemptsCompleted == retry.MaxAttempts &&
                                retry.MaxAttempts > 1;
                if (!lastResult.Success)
                {
                    _logger.LogError(
                        "Job {Id} failed after {Attempts}/{MaxAttempts} attempt(s). Final failure: {FailureKind}; ExitCode={ExitCode}; RetriesExhausted={RetriesExhausted}.",
                        command.Id,
                        attemptsCompleted,
                        retry.MaxAttempts,
                        lastResult.FailureKind,
                        lastResult.ExitCode,
                        exhausted);
                }
                else if (attemptsCompleted > 1)
                {
                    _logger.LogInformation(
                        "Job {Id} recovered successfully on attempt {Attempt}/{MaxAttempts}.",
                        command.Id,
                        attemptsCompleted,
                        retry.MaxAttempts);
                }

                _monitor.Record(
                    new ExecutionEvent
                    {
                        CommandId = command.Id,
                        Command = command.Command,
                        StartUtc = overallStart,
                        EndUtc = lastResult.EndUtc,
                        ExitCode = lastResult.ExitCode,
                        Success = lastResult.Success,
                        Error = lastResult.Error,
                        AttemptCount = attemptsCompleted,
                        MaxAttempts = retry.MaxAttempts,
                        RetryExhausted = exhausted,
                        TimedOut = lastResult.FailureKind == RetryFailureKind.Timeout,
                        TriggerSource = triggerSource
                    });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogInformation("Execution cancelled (shutdown) for {Id}", command.Id);
                if (attemptsCompleted > 0)
                {
                    _monitor.Record(
                        new ExecutionEvent
                        {
                            CommandId = command.Id,
                            Command = command.Command,
                            StartUtc = overallStart,
                            EndUtc = DateTime.UtcNow,
                            Success = true,
                            AttemptCount = attemptsCompleted,
                            MaxAttempts = retry.MaxAttempts,
                            TriggerSource = triggerSource
                        });
                }
            }
        }

        private async Task<CommandAttemptResult> ExecuteCommandAttemptAsync(
            ScheduledCommand command,
            int attempt,
            int maxAttempts,
            CancellationToken ct)
        {
            try
            {
                if (!command.QuietStartLog)
                    _logger.LogInformation(
                        "Executing {Id} attempt {Attempt}/{MaxAttempts}: {Command}",
                        command.Id,
                        attempt,
                        maxAttempts,
                        command.Command);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (command.MaxRuntimeMinutes is int maxMin && maxMin > 0)
                    cts.CancelAfter(TimeSpan.FromMinutes(maxMin));

                var psi = new ProcessStartInfo("cmd.exe")
                {
                    Arguments = $"/c {command.Command}",
                    RedirectStandardOutput = command.CaptureOutput,
                    RedirectStandardError = command.CaptureOutput,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                if (!process.Start())
                    throw new InvalidOperationException($"Failed to start process for {command.Id}");

                using var shutdownKiller = ct.Register(
                    () =>
                    {
                        try
                        {
                            if (!process.HasExited)
                                process.Kill(entireProcessTree: true);
                        }
                        catch
                        {
                        }
                    });

                var maxOutputChars = (command.MaxOutputKB > 0 ? command.MaxOutputKB : 512) * 1024;
                var readStdOut = command.CaptureOutput
                    ? ReadBoundedStreamAsync(process.StandardOutput, maxOutputChars)
                    : Task.FromResult<string?>(null);
                var readStdErr = command.CaptureOutput
                    ? ReadBoundedStreamAsync(process.StandardError, maxOutputChars)
                    : Task.FromResult<string?>(null);
                var timedOut = false;

                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    TryKillProcessTree(process, command.Id, timedOut: false);
                    return new CommandAttemptResult(
                        DateTime.UtcNow,
                        null,
                        true,
                        null,
                        RetryFailureKind.Shutdown);
                }
                catch (OperationCanceledException)
                {
                    timedOut = true;
                    TryKillProcessTree(process, command.Id, timedOut: true);
                    await process.WaitForExitAsync();
                }

                var output = await readStdOut;
                var error = await readStdErr;
                var exitCode = process.HasExited ? process.ExitCode : (int?)null;

                if (command.CaptureOutput && !string.IsNullOrWhiteSpace(output))
                    _logger.LogInformation("Output {Id} attempt {Attempt}:\n{Output}", command.Id, attempt, output);
                if (command.CaptureOutput && !string.IsNullOrWhiteSpace(error))
                    _logger.LogError("Errors {Id} attempt {Attempt}:\n{Error}", command.Id, attempt, error);

                var success = !timedOut && (exitCode ?? -1) == 0;
                if (command.TreatStdErrAsFailure && command.CaptureOutput && !string.IsNullOrWhiteSpace(error))
                    success = false;

                string? failure = null;
                if (timedOut)
                    failure = command.MaxRuntimeMinutes is int minutes
                        ? $"Timed out after {minutes} minute(s)"
                        : "Timed out";
                else if (!success)
                    failure = command.CaptureOutput && !string.IsNullOrWhiteSpace(error)
                        ? error
                        : $"ExitCode={exitCode}";

                return new CommandAttemptResult(
                    DateTime.UtcNow,
                    exitCode,
                    success,
                    failure,
                    timedOut ? RetryFailureKind.Timeout : RetryFailureKind.ExitCode);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return new CommandAttemptResult(
                    DateTime.UtcNow,
                    null,
                    true,
                    null,
                    RetryFailureKind.Shutdown);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error executing {Id} attempt {Attempt}/{MaxAttempts}. Command: {Command}.",
                    command.Id,
                    attempt,
                    maxAttempts,
                    command.Command);

                var errorDetails = $"{ex.GetType().Name}: {ex.Message}";
                if (ex.InnerException != null)
                    errorDetails += $" | Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";

                return new CommandAttemptResult(
                    DateTime.UtcNow,
                    null,
                    false,
                    errorDetails,
                    RetryFailureKind.Exception);
            }
        }

        private void TryKillProcessTree(Process process, string jobId, bool timedOut)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                if (timedOut)
                    _logger.LogWarning("Process {Id} killed due to timeout", jobId);
            }
            catch (Exception killEx)
            {
                _logger.LogError(killEx, "Failed to kill process tree for {Id}", jobId);
            }
        }

        private sealed record CommandAttemptResult(
            DateTime EndUtc,
            int? ExitCode,
            bool Success,
            string? Error,
            RetryFailureKind FailureKind);


        private static async Task<string?> ReadBoundedStreamAsync(TextReader reader, int maxChars)
        {
            var buffer = new char[4096];
            var sb = new System.Text.StringBuilder();
            int totalRead = 0;
            int read;

            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                if (totalRead + read > maxChars)
                {
                    int allowed = maxChars - totalRead;
                    if (allowed > 0)
                    {
                        sb.Append(buffer, 0, allowed);
                    }
                    sb.Append($"\n... [Output truncated after {maxChars} characters]");
                    break;
                }
                sb.Append(buffer, 0, read);
                totalRead += read;
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Service is stopping");
            _configWatcher?.Dispose();
            await base.StopAsync(cancellationToken);
        }
    }

    public class ScheduledCommand
    {
        public string Id { get; set; } = string.Empty;

        public string Command { get; set; } = string.Empty;

        public string CronExpression { get; set; } = string.Empty;

        public string TimeZone { get; set; } = "UTC";

        public bool Enabled { get; set; } = true;

        public int? MaxRuntimeMinutes { get; set; }

        public bool AllowParallelRuns { get; set; } = false;

        public string? ConcurrencyKey { get; set; }

        public bool AlertOnFail { get; set; } = true;

        public bool CaptureOutput { get; set; } = true;   // per-job: don't collect stdout/stderr when false

        public int MaxOutputKB { get; set; } = 512;       // per-job: limit captured stdout/stderr size in KB (default 512KB)

        public bool TreatStdErrAsFailure { get; set; } = false; // per-job: treat non-empty stderr as failure even if ExitCode == 0

        public bool QuietStartLog { get; set; } = false;  // per-job: hide "Executing ..." info line

        public string? CustomAlertMessage { get; set; }    // optional hint in alert emails

        public RetryOptions Retry { get; set; } = new();

        // runtime (not bound)
        [JsonIgnore]
        public CronExpression? Cron { get; set; }

        public ScheduledCommand CloneForExecution() => new()
        {
            Id = Id,
            Command = Command,
            CronExpression = CronExpression,
            TimeZone = TimeZone,
            Enabled = Enabled,
            MaxRuntimeMinutes = MaxRuntimeMinutes,
            AllowParallelRuns = AllowParallelRuns,
            ConcurrencyKey = ConcurrencyKey,
            AlertOnFail = AlertOnFail,
            CaptureOutput = CaptureOutput,
            MaxOutputKB = MaxOutputKB,
            TreatStdErrAsFailure = TreatStdErrAsFailure,
            QuietStartLog = QuietStartLog,
            CustomAlertMessage = CustomAlertMessage,
            Retry = RetryPolicy.Normalize(Retry),
            Cron = Cron
        };
    }

    public static class WindowsServiceHelpers
    {
        public class ServiceProperties
        {
            public string DisplayName { get; set; } = string.Empty;

            public string Description { get; set; } = string.Empty;
        }

        [SupportedOSPlatform("windows")]
        public static void SetServiceProperties(string serviceName, ServiceProperties properties)
        {
            try
            {
                using (var sc = new System.ServiceProcess.ServiceController(serviceName))
                {
                    var registryKey = Microsoft.Win32.Registry.LocalMachine
                        .OpenSubKey($"SYSTEM\\CurrentControlSet\\Services\\{serviceName}", true);

                    if (registryKey != null)
                    {
                        if (!string.IsNullOrEmpty(properties.DisplayName))
                            registryKey.SetValue("DisplayName", properties.DisplayName);

                        if (!string.IsNullOrEmpty(properties.Description))
                            registryKey.SetValue("Description", properties.Description);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error setting service properties: {ex.Message}");
            }
        }
    }
}

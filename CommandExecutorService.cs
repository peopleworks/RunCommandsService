using Cronos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;

namespace RunCommandsService
{
    public class CommandExecutorService : BackgroundService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<CommandExecutorService> _logger;
        private readonly ExecutionMonitor _monitor;
        private readonly ConcurrencyManager _concurrency;
        private readonly SchedulerOptions _schedOptions;
        private FileSystemWatcher _configWatcher;

        private List<ScheduledCommand> _commands = new();
        private readonly object _lockObject = new object();

        // Next run storage (UTC) by job id
        private readonly ConcurrentDictionary<string, DateTime?> _nextRunUtc = new();

        // Track invalid jobs so we log each only once until fixed
        private readonly HashSet<string> _invalidScheduleLogged = new(StringComparer.OrdinalIgnoreCase);

        private DateTime _lastReload = DateTime.MinValue;


        public CommandExecutorService(
            IConfiguration configuration,
            IOptions<SchedulerOptions> schedOptions,
            ExecutionMonitor monitor,
            ConcurrencyManager concurrency,
            ILogger<CommandExecutorService> logger)
        {
            _configuration = configuration;
            _schedOptions = schedOptions.Value;
            _monitor = monitor;
            _concurrency = concurrency;
            _logger = logger;

            LoadCommands();
            SetupConfigurationWatcher();
        }

        // ---------- Helpers ----------

        private static bool TryParseCron(string text, out CronExpression cron, out string error)
        {
            try
            {
                cron = CronExpression.Parse(text);
                error = null;
                return true;
            } catch(Exception ex)
            {
                cron = null;
                error = ex.Message;
                return false;
            }
        }

        private static TimeZoneInfo TZ(string tz)
        {
            if(string.IsNullOrWhiteSpace(tz))
                return TimeZoneInfo.Utc;
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(tz);
            } catch
            {
                return TimeZoneInfo.Utc;
            }
        }


        // Compute next run in the job's local wall-clock, then convert to real UTC
        private static DateTime? SafeNextOccurrenceUtc(CronExpression cron, DateTime utcNow, TimeZoneInfo tz)
        {
            try
            {
                // Convert "now" to the job's local time
                var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz);

                // Cronos (UTC-only API here) — use a "fake UTC" cursor that holds local wall-clock
                var fakeUtcCursor = DateTime.SpecifyKind(nowLocal, DateTimeKind.Utc);

                // Next LOCAL occurrence on that wall-clock timeline
                var nextLocalFakeUtc = cron.GetNextOccurrence(fakeUtcCursor);
                if (nextLocalFakeUtc == null) return null;

                // Interpret as local and convert FROM tz TO real UTC
                var nextLocal = DateTime.SpecifyKind(nextLocalFakeUtc.Value, DateTimeKind.Unspecified);
                return ConvertLocalToUtc(nextLocal, tz);
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
            lock(_lockObject)
                snapshot = _commands?.ToList() ?? new List<ScheduledCommand>();

            var schedule = snapshot.Select(
                c =>
                {
                    if(c == null)
                        c = new ScheduledCommand();

                    string nextRun = null;
                    if(c.Cron != null)
                    {
                        var next = SafeNextOccurrenceUtc(c.Cron, now, TZ(c.TimeZone));
                        if(next.HasValue)
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
                        ConcurrencyKey = c.ConcurrencyKey,
                        MaxRuntimeMinutes = c.MaxRuntimeMinutes,
                        NextRunUtc = nextRun,
                        CustomAlertMessage = c.CustomAlertMessage
                    };
                });

            _monitor.UpdateScheduleSnapshot(schedule);
        }

        // ---------- Config loading ----------

        private void LoadCommands()
        {
            lock(_lockObject)
            {
                _commands = _configuration.GetSection("ScheduledCommands").Get<List<ScheduledCommand>>() ??
                    new List<ScheduledCommand>();

                var now = DateTime.UtcNow;

                foreach(var c in _commands)
                {
                    if(string.IsNullOrWhiteSpace(c.Id))
                        c.Id = c.Command;

                    if(string.IsNullOrWhiteSpace(c.TimeZone))
                        c.TimeZone = _schedOptions.DefaultTimeZone;

                    // Allow logging again if a previously-bad cron was fixed
                    _invalidScheduleLogged.Remove(c.Id);

                    // ---- FIX: declare & init before the condition ----
                    CronExpression cron = null;
                    string cronErr = null;
                    bool hasCron = !string.IsNullOrWhiteSpace(c.CronExpression);
                    bool parsed = hasCron && TryParseCron(c.CronExpression, out cron, out cronErr);

                    if(parsed)
                    {
                        c.Cron = cron;
                        _nextRunUtc[c.Id] = SafeNextOccurrenceUtc(cron, now, TZ(c.TimeZone));
                    } else
                    {
                        c.Cron = null;
                        _nextRunUtc[c.Id] = null;

                        // Only complain for enabled jobs
                        if(c.Enabled)
                        {
                            var err = !hasCron ? "missing CronExpression" : $"invalid CronExpression — {cronErr}";
                            if(_invalidScheduleLogged.Add(c.Id))
                                _logger.LogError("Job {Id}: {Error}. Job will be skipped until fixed.", c.Id, err);
                        }
                    }
                }

                RefreshMonitorSnapshot();
                _logger.LogInformation("Loaded {Count} commands from configuration", _commands.Count);
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
                var now = DateTime.UtcNow;
                if((now - _lastReload).TotalMilliseconds < 800)
                    return; // debounce
                _lastReload = now;

                _logger.LogInformation("Configuration file changed. Reloading commands...");
                Thread.Sleep(300); // small settle time
                LoadCommands();
            };


            _configWatcher.EnableRaisingEvents = true;
        }

        // ---------- Scheduler loop ----------

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Service started");

            while(!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Keep dashboard fresh
                    RefreshMonitorSnapshot();

                    List<ScheduledCommand> currentCommands;
                    lock(_lockObject)
                        currentCommands = _commands.ToList();

                    var nowUtc = DateTime.UtcNow;

                    foreach(var cmd in currentCommands)
                    {
                        if(cmd == null)
                            continue;
                        if(!cmd.Enabled)
                            continue;
                        if(cmd.Cron == null)
                            continue; // invalid cron or missing → skip

                        // compute or read stored next run
                        var due = _nextRunUtc.GetOrAdd(
                            cmd.Id,
                            _ => SafeNextOccurrenceUtc(cmd.Cron, nowUtc, TZ(cmd.TimeZone)));

                        if(!due.HasValue)
                            continue;

                        if(nowUtc >= due.Value)
                        {
                            // Visibility when things “don’t run”
                            _logger.LogDebug("Due @ {Due:o} (now {Now:o}) → launching {Id}", due.Value, nowUtc, cmd.Id);

                            await RunCommandAsync(cmd, stoppingToken);

                            // schedule next from the due time (+1s) to avoid drift / skips
                            var next = SafeNextOccurrenceUtc(cmd.Cron, due.Value.AddSeconds(1), TZ(cmd.TimeZone));
                            _nextRunUtc[cmd.Id] = next;
                            RefreshMonitorSnapshot();
                        }
                    }

                    await Task.Delay(TimeSpan.FromSeconds(_schedOptions.PollSeconds), stoppingToken);
                } catch(OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Scheduler loop cancelled (shutdown).");
                    break;
                } catch(TaskCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Scheduler loop task canceled (shutdown).");
                    break;
                } catch(Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error in command execution loop");
                    // small backoff so we don't spin if something transient fails
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                    } catch
                    { /* ignore */
                    }
                }
            }
        }

        // ---------- Command runner ----------

        private async Task RunCommandAsync(ScheduledCommand command, CancellationToken ct)
        {
            using var acquired = await _concurrency.TryAcquireAsync(
                command.ConcurrencyKey ?? command.Id,
                command.AllowParallelRuns,
                ct);

            if(acquired == null)
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
                        SkippedDueToConflict = true
                    });
                return;
            }

            var start = DateTime.UtcNow;

            try
            {
                if(!command.QuietStartLog)
                    _logger.LogInformation("Executing {Id}: {Command}", command.Id, command.Command);

                // Linked CTS so we can differentiate shutdown vs per-job timeout.
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if(command.MaxRuntimeMinutes is int maxMin && maxMin > 0)
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
                if(!process.Start())
                    throw new InvalidOperationException($"Failed to start process for {command.Id}");

                // If the host is shutting down, be nice to the child process.
                using var shutdownKiller = ct.Register(
                    () =>
                    {
                        try
                        {
                            if(!process.HasExited)
                                process.Kill(entireProcessTree: true);
                        } catch
                        {
                        }
                    });

                Task<string> readStdOut = command.CaptureOutput
                    ? process.StandardOutput.ReadToEndAsync()
                    : Task.FromResult<string>(null);
                Task<string> readStdErr = command.CaptureOutput
                    ? process.StandardError.ReadToEndAsync()
                    : Task.FromResult<string>(null);

                try
                {
                    // Await exit; this may be canceled by timeout (cts.CancelAfter) or service shutdown (ct).
                    await process.WaitForExitAsync(cts.Token);
                } catch(OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Service is stopping: treat as normal (no error / no timeout warning).
                    try
                    {
                        if(!process.HasExited)
                            process.Kill(entireProcessTree: true);
                    } catch
                    {
                    }
                    _logger.LogInformation("Execution cancelled (shutdown) for {Id}", command.Id);

                    _monitor.Record(
                        new ExecutionEvent
                        {
                            CommandId = command.Id,
                            Command = command.Command,
                            StartUtc = start,
                            EndUtc = DateTime.UtcNow,
                            ExitCode = null,
                            Success = true, // don't count as a failure
                            Error = null
                        });
                    return; // don't continue to output handling
                } catch(OperationCanceledException)
                {
                    // Per-job timeout
                    try
                    {
                        process.Kill(entireProcessTree: true);
                        _logger.LogWarning("Process {Id} killed due to timeout", command.Id);
                    } catch(Exception killEx)
                    {
                        _logger.LogError(killEx, "Failed to kill timed out process for {Id}", command.Id);
                    }
                    await process.WaitForExitAsync(); // ensure it fully exits before we read outputs
                }

                var output = await readStdOut;
                var error = await readStdErr;
                var exitCode = process.HasExited ? process.ExitCode : (int?)null;

                if(command.CaptureOutput && !string.IsNullOrWhiteSpace(output))
                    _logger.LogInformation("Output {Id}:\n{Output}", command.Id, output);

                if(command.CaptureOutput && !string.IsNullOrWhiteSpace(error))
                    _logger.LogError("Errors {Id}:\n{Error}", command.Id, error);

                // Success rules:
                // - if we captured output and stderr has content → mark as failure
                // - otherwise rely on exit code 0
                var success = (exitCode ?? -1) == 0;
                if(command.CaptureOutput && !string.IsNullOrWhiteSpace(error))
                    success = false;

                _monitor.Record(
                    new ExecutionEvent
                    {
                        CommandId = command.Id,
                        Command = command.Command,
                        StartUtc = start,
                        EndUtc = DateTime.UtcNow,
                        ExitCode = exitCode,
                        Success = success,
                        Error =
                            command.CaptureOutput
                                    ? (string.IsNullOrWhiteSpace(error) ? null : error)
                                    : ((exitCode ?? -1) == 0 ? null : $"ExitCode={exitCode}")
                    });
            } catch(OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Catch any late shutdown cancellations outside WaitForExitAsync
                _logger.LogInformation("Execution cancelled (shutdown) for {Id}", command.Id);
                _monitor.Record(
                    new ExecutionEvent
                    {
                        CommandId = command.Id,
                        Command = command.Command,
                        StartUtc = start,
                        EndUtc = DateTime.UtcNow,
                        ExitCode = null,
                        Success = true,
                        Error = null
                    });
            } catch(TaskCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogInformation("Execution task cancelled (shutdown) for {Id}", command.Id);
                _monitor.Record(
                    new ExecutionEvent
                    {
                        CommandId = command.Id,
                        Command = command.Command,
                        StartUtc = start,
                        EndUtc = DateTime.UtcNow,
                        ExitCode = null,
                        Success = true,
                        Error = null
                    });
            } catch(Exception ex)
            {
                _logger.LogError(ex, "Error executing {Id}", command.Id);
                _monitor.Record(
                    new ExecutionEvent
                    {
                        CommandId = command.Id,
                        Command = command.Command,
                        StartUtc = start,
                        EndUtc = DateTime.UtcNow,
                        ExitCode = null,
                        Success = false,
                        Error = ex.ToString()
                    });
            }
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
        public string Id { get; set; }

        public string Command { get; set; }

        public string CronExpression { get; set; }

        public string TimeZone { get; set; }

        public bool Enabled { get; set; } = true;

        public int? MaxRuntimeMinutes { get; set; }

        public bool AllowParallelRuns { get; set; } = false;

        public string ConcurrencyKey { get; set; }

        public bool AlertOnFail { get; set; } = true;

        public bool CaptureOutput { get; set; } = true;   // per-job: don't collect stdout/stderr when false

        public bool QuietStartLog { get; set; } = false;  // per-job: hide "Executing ..." info line

        public string CustomAlertMessage { get; set; }    // optional hint in alert emails

        // runtime (not bound)
        public CronExpression Cron { get; set; }
    }

    public static class WindowsServiceHelpers
    {
        public class ServiceProperties
        {
            public string DisplayName { get; set; }

            public string Description { get; set; }
        }

        [SupportedOSPlatform("windows")]
        public static void SetServiceProperties(string serviceName, ServiceProperties properties)
        {
            try
            {
                using(var sc = new System.ServiceProcess.ServiceController(serviceName))
                {
                    var registryKey = Microsoft.Win32.Registry.LocalMachine
                        .OpenSubKey($"SYSTEM\\CurrentControlSet\\Services\\{serviceName}", true);

                    if(registryKey != null)
                    {
                        if(!string.IsNullOrEmpty(properties.DisplayName))
                            registryKey.SetValue("DisplayName", properties.DisplayName);

                        if(!string.IsNullOrEmpty(properties.Description))
                            registryKey.SetValue("Description", properties.Description);
                    }
                }
            } catch(Exception ex)
            {
                Console.WriteLine($"Error setting service properties: {ex.Message}");
            }
        }
    }
}

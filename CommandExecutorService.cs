using Cronos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

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
        private readonly ConcurrentDictionary<string, DateTime?> _nextRunUtc = new();

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

        private void LoadCommands()
        {
            lock (_lockObject)
            {
                _commands = _configuration.GetSection("ScheduledCommands").Get<List<ScheduledCommand>>() ?? new List<ScheduledCommand>();
                var now = DateTime.UtcNow;

                foreach (var c in _commands)
                {
                    if (string.IsNullOrWhiteSpace(c.Id)) c.Id = c.Command;
                    if (string.IsNullOrWhiteSpace(c.TimeZone)) c.TimeZone = _schedOptions.DefaultTimeZone;
                    c.Cron = CronExpression.Parse(c.CronExpression);
                    _nextRunUtc[c.Id] = c.Cron.GetNextOccurrence(now, TZ(c.TimeZone));
                }
                PublishScheduleSnapshot();
                _logger.LogInformation("Loaded {Count} commands from configuration", _commands.Count);
            }
        }

        private void PublishScheduleSnapshot()
        {
            var snapshot = _commands.Select(c => new ScheduledCommandView
            {
                Id = c.Id,
                Command = c.Command,
                CronExpression = c.CronExpression,
                TimeZone = c.TimeZone,
                Enabled = c.Enabled,
                AllowParallelRuns = c.AllowParallelRuns,
                ConcurrencyKey = c.ConcurrencyKey,
                MaxRuntimeMinutes = c.MaxRuntimeMinutes,
                NextRunUtc = _nextRunUtc.TryGetValue(c.Id, out var next) ? next?.ToString("o") : null,
                CustomAlertMessage = c.CustomAlertMessage
            });
            _monitor.UpdateScheduleSnapshot(snapshot);
        }

        private static TimeZoneInfo TZ(string tz)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(tz); }
            catch { return TimeZoneInfo.Utc; }
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
                _logger.LogInformation("Configuration file changed. Reloading commands...");
                Thread.Sleep(500);
                LoadCommands();
            };

            _configWatcher.EnableRaisingEvents = true;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Service started");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    List<ScheduledCommand> currentCommands;
                    lock (_lockObject) { currentCommands = _commands.ToList(); }

                    var nowUtc = DateTime.UtcNow;
                    foreach (var cmd in currentCommands.Where(c => c.Enabled))
                    {
                        var due = _nextRunUtc.GetOrAdd(cmd.Id, _ => cmd.Cron.GetNextOccurrence(nowUtc, TZ(cmd.TimeZone)));
                        if (due.HasValue && nowUtc >= due.Value)
                        {
                            _ = RunCommandAsync(cmd, stoppingToken);
                            // schedule next
                            _nextRunUtc[cmd.Id] = cmd.Cron.GetNextOccurrence(nowUtc.AddSeconds(1), TZ(cmd.TimeZone));
                            PublishScheduleSnapshot();
                        }
                    }

                    await Task.Delay(TimeSpan.FromSeconds(_schedOptions.PollSeconds), stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in command execution loop");
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, _schedOptions.PollSeconds)), stoppingToken);
                }
            }
        }

        private async Task RunCommandAsync(ScheduledCommand command, CancellationToken ct)
        {
            using var acquired = await _concurrency.TryAcquireAsync(command.ConcurrencyKey ?? command.Id, command.AllowParallelRuns, ct);
            if (acquired == null)
            {
                _logger.LogWarning("Skipping {Id} due to concurrency key in use ({Key})", command.Id, command.ConcurrencyKey);
                _monitor.Record(new ExecutionEvent
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
                _logger.LogInformation("Executing {Id}: {Command}", command.Id, command.Command);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (command.MaxRuntimeMinutes.HasValue && command.MaxRuntimeMinutes.Value > 0)
                {
                    cts.CancelAfter(TimeSpan.FromMinutes(command.MaxRuntimeMinutes.Value));
                }

                var psi = new ProcessStartInfo("cmd.exe")
                {
                    Arguments = $"/c {command.Command}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                process.Start();

                var readStdOut = process.StandardOutput.ReadToEndAsync();
                var readStdErr = process.StandardError.ReadToEndAsync();

                await Task.WhenAny(Task.Run(() => process.WaitForExit(), cts.Token), Task.Delay(Timeout.Infinite, cts.Token))
                          .ContinueWith(_ => Task.CompletedTask);

                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                        _logger.LogWarning("Process {Id} killed due to timeout", command.Id);
                    }
                    catch (Exception killEx)
                    {
                        _logger.LogError(killEx, "Failed to kill timed out process for {Id}", command.Id);
                    }
                }

                var output = await readStdOut;
                var error = await readStdErr;
                var exitCode = process.HasExited ? process.ExitCode : (int?)null;

                if (!string.IsNullOrWhiteSpace(output))
                    _logger.LogInformation("Output {Id}:\n{Output}", command.Id, output);

                if (!string.IsNullOrWhiteSpace(error))
                    _logger.LogError("Errors {Id}:\n{Error}", command.Id, error);

                var success = (exitCode ?? -1) == 0 && string.IsNullOrWhiteSpace(error);

                _monitor.Record(new ExecutionEvent
                {
                    CommandId = command.Id,
                    Command = command.Command,
                    StartUtc = start,
                    EndUtc = DateTime.UtcNow,
                    ExitCode = exitCode,
                    Success = success,
                    Error = string.IsNullOrWhiteSpace(error) ? null : error
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing {Id}", command.Id);
                _monitor.Record(new ExecutionEvent
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
        public string CustomAlertMessage { get; set; } // NEW

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

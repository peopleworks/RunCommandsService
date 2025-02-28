using Cronos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Linq;

namespace RunCommandsService
{
    public class CommandExecutorService : BackgroundService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<CommandExecutorService> _logger;
        private FileSystemWatcher _configWatcher;
        private List<ScheduledCommand> _commands;
        private readonly object _lockObject = new object();

        public CommandExecutorService(IConfiguration configuration, ILogger<CommandExecutorService> logger)
        {
            _configuration = configuration;
            _logger = logger;
            LoadCommands();
            SetupConfigurationWatcher();
        }

        private void LoadCommands()
        {
            lock(_lockObject)
            {
                _commands = _configuration.GetSection("ScheduledCommands").Get<List<ScheduledCommand>>() ??
                    new List<ScheduledCommand>();
                _logger.LogInformation($"Loaded {_commands.Count} commands from configuration");
            }
        }

        private void SetupConfigurationWatcher()
        {
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory);
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
                    lock (_lockObject)
                    {
                        currentCommands = _commands.ToList();
                    }

                    foreach (var command in currentCommands)
                    {
                        try
                        {
                            var cronExpression = CronExpression.Parse(command.CronExpression);
                            var currentTime = DateTime.UtcNow;
                            var nextOccurrence = cronExpression.GetNextOccurrence(currentTime);

                            _logger.LogInformation($"Current time (UTC): {currentTime}");
                            _logger.LogInformation($"Next execution for command '{command.Command}' scheduled at (UTC): {nextOccurrence}");

                            if (nextOccurrence.HasValue)
                            {
                                if (!command.LastExecuted.HasValue || nextOccurrence.Value <= currentTime)
                                {
                                    _logger.LogInformation($"Executing command '{command.Command}' now");
                                    await ExecuteCommand(command);
                                    command.LastExecuted = currentTime;
                                }
                                else
                                {
                                    var timeUntilNext = nextOccurrence.Value - currentTime;
                                    _logger.LogInformation($"Time until next execution: {timeUntilNext.TotalMinutes:F2} minutes");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Error processing command: {command.Command}");
                        }
                    }

                    // Check every 30 seconds
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in command execution loop");
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
            }
        }
        private async Task ExecuteCommand(ScheduledCommand command)
        {
            try
            {
                _logger.LogInformation($"Starting command execution: {command.Command}");

                var processInfo = new ProcessStartInfo("cmd.exe")
                {
                    Arguments = $"/c {command.Command}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };


                _logger.LogDebug($"Process info configured for command: {command.Command}");

                using var process = Process.Start(processInfo);
                if(process == null)
                {
                    throw new InvalidOperationException("Failed to start process.");
                }

                string output = await process.StandardOutput.ReadToEndAsync();
                string error = await process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync();

                _logger.LogInformation($"Command completed. Exit code: {process.ExitCode}");
                _logger.LogInformation($"Command output: {output}");

                if(!string.IsNullOrEmpty(error))
                {
                    _logger.LogError($"Command errors: {error}");
                }
            } catch(Exception ex)
            {
                _logger.LogError(ex, $"Error executing command: {command.Command}");
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
        public string Command { get; set; }

        public string CronExpression { get; set; }

        public DateTime? LastExecuted { get; set; }
    }

    public static class WindowsServiceHelpers
    {
        public class ServiceProperties
        {
            public string DisplayName { get; set; }

            public string Description { get; set; }
        }

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

using AsyncKeyedLock;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using RunCommandsService;
using ServiceHelpers = RunCommandsService.WindowsServiceHelpers;

public class Program
{
    public static int Main(string[] args)
    {
        // --validate / --check: validate the configuration and exit without executing anything.
        if (args != null && Array.Exists(args, a =>
                string.Equals(a, "--validate", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "--check", StringComparison.OrdinalIgnoreCase)))
        {
            return RunValidation(args);
        }

        CreateHostBuilder(args).Build().Run();
        return 0;
    }

    private static int RunValidation(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddEnvironmentVariables()
            .AddCommandLine(args)
            .Build();

        var report = ConfigValidator.Validate(configuration);
        Console.Write(ConfigValidator.FormatReport(report));
        return report.AllValid ? 0 : 1;
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args) // loads appsettings.json with reloadOnChange:true
            .UseWindowsService(options =>
            {
                options.ServiceName = "Scheduled Command Executor";
                ServiceHelpers.SetServiceProperties(
                    "Scheduled Command Executor",
                    new ServiceHelpers.ServiceProperties
                    {
                        DisplayName = "Scheduled Command Executor Service",
                        Description = "Executes cron-based commands with monitoring, alerts, and safe concurrency"
                    });
            })
            // Ensure relative paths resolve to the service folder (important when running as a Windows Service)
            .UseContentRoot(AppDomain.CurrentDomain.BaseDirectory)
            .ConfigureLogging((hostContext, logging) =>
            {
                logging.ClearProviders();
                logging.AddFileLogger(options =>
                {
                    options.LogDirectory = "Logs";
                    options.FileSizeLimit = 10 * 1024 * 1024; // 10MB
                    options.RetainDays = 30;
                    // Respect configured Logging:LogLevel:Default if present
                    try
                    {
                        var lvl = hostContext.Configuration["Logging:LogLevel:Default"];
                        if(!string.IsNullOrWhiteSpace(lvl) && Enum.TryParse<LogLevel>(lvl, true, out var parsed))
                            options.MinLevel = parsed;
                    } catch { }
                });
            })
            .ConfigureServices((hostContext, services) =>
            {
                // Options bindings (use the config already loaded by CreateDefaultBuilder)
                services.Configure<SchedulerOptions>(hostContext.Configuration.GetSection("Scheduler"));
                services.Configure<MonitoringOptions>(hostContext.Configuration.GetSection("Monitoring"));
                // Webhook notifier options (if used)
                services.Configure<WebhookOptions>(hostContext.Configuration.GetSection("Monitoring:Notifiers:Webhook"));

                // Core singletons
                services.AddSingleton(new AsyncKeyedLocker<string>());
                services.AddSingleton<ExecutionMonitor>();

                // Hosted services
                services.AddHostedService<Monitoring>();               // dashboard + APIs
                services.AddHostedService<CommandExecutorService>();   // scheduler/executor

                // Back-compat (noop). Keep only if your solution still references it.
                services.AddHostedService<HealthHttpServerService>();
            });
}

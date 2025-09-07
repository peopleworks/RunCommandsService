using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RunCommandsService;
using static RunCommandsService.WindowsServiceHelpers;

public class Program
{
    public static void Main(string[] args) => CreateHostBuilder(args).Build().Run();

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args) // loads appsettings.json with reloadOnChange:true
            .UseWindowsService(options =>
            {
                options.ServiceName = "Scheduled Command Executor";
                WindowsServiceHelpers.SetServiceProperties(
                    "Scheduled Command Executor",
                    new ServiceProperties
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
                });
            })
            .ConfigureServices((hostContext, services) =>
            {
                // Options bindings (use the config already loaded by CreateDefaultBuilder)
                services.Configure<SchedulerOptions>(hostContext.Configuration.GetSection("Scheduler"));
                services.Configure<MonitoringOptions>(hostContext.Configuration.GetSection("Monitoring"));
                // If you later add a webhook notifier section:
                // services.Configure<WebhookOptions>(hostContext.Configuration.GetSection("Notifiers:Webhook"));

                // Core singletons
                services.AddSingleton<ConcurrencyManager>();
                services.AddSingleton<ExecutionMonitor>();

                // Hosted services
                services.AddHostedService<Monitoring>();               // dashboard + APIs
                services.AddHostedService<CommandExecutorService>();   // scheduler/executor

                // Back-compat (noop). Keep only if your solution still references it.
                services.AddHostedService<HealthHttpServerService>();
            });
}

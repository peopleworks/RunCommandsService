using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RunCommandsService;
using static RunCommandsService.WindowsServiceHelpers;

public class Program
{
    public static void Main(string[] args) => CreateHostBuilder(args).Build().Run();

    public static IHostBuilder CreateHostBuilder(string[] args) => Host.CreateDefaultBuilder(args)
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
            services.Configure<List<RunCommandsService.ScheduledCommand>>(hostContext.Configuration.GetSection("ScheduledCommands"));
            services.Configure<SchedulerOptions>(hostContext.Configuration.GetSection("Scheduler"));
            services.Configure<MonitoringOptions>(hostContext.Configuration.GetSection("Monitoring"));

            // Notifiers / Monitor
            var opt = hostContext.Configuration.GetSection("Monitoring").Get<MonitoringOptions>() ?? new MonitoringOptions();
            var notifiers = new List<IAlertNotifier>();
            if (opt.Notifiers.Email.Enabled) notifiers.Add(new EmailNotifier(opt.Notifiers.Email));
            if (opt.Notifiers.Webhook.Enabled) notifiers.Add(new WebhookNotifier(opt.Notifiers.Webhook));
            services.AddSingleton<IAlertNotifier>(new CompositeNotifier(notifiers));
            services.AddSingleton<ExecutionMonitor>();

            // Concurrency manager
            var schedOpt = hostContext.Configuration.GetSection("Scheduler").Get<SchedulerOptions>() ?? new SchedulerOptions();
            services.AddSingleton(new ConcurrencyManager(schedOpt.MaxParallelism));

            // Hosted services
            services.AddHostedService<CommandExecutorService>();
            services.AddHostedService<HealthHttpServerService>();
        })
        .ConfigureAppConfiguration((hostContext, config) =>
        {
            config.SetBasePath(AppDomain.CurrentDomain.BaseDirectory);
            config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        });
}

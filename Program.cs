using Cronos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RunCommandsService;
using System.Diagnostics;
using static RunCommandsService.WindowsServiceHelpers;


/* 
 https://crontab.cronhub.io/
Cron expression	Schedule
* * * * *	    Every minute
0 * * * *	    Every hour
0 0 * * *	    Every day at 12:00 AM
0 0 * * FRI	    At 12:00 AM, only on Friday
0 0 1 * *	    At 12:00 AM, on day 1 of the month
*/
public class Program
{
    public static void Main(string[] args) { CreateHostBuilder(args).Build().Run(); }

    public static IHostBuilder CreateHostBuilder(string[] args) => Host.CreateDefaultBuilder(args)
        .UseWindowsService(
            options =>
            {
                options.ServiceName = "ScheduledCommandExecutor";
                WindowsServiceHelpers.SetServiceProperties(
                    "ScheduledCommandExecutor",
                    new ServiceProperties
                    {
                        DisplayName = "Scheduled Command Executor Service",
                        Description =
                            "Service that executes scheduled commands based on cron expressions defined in appsettings.json"
                    });
            })
        .ConfigureLogging(
            (hostContext, logging) =>
            {
                logging.ClearProviders();
                logging.AddFileLogger(
                    options =>
                    {
                        options.LogDirectory = "Logs";
                        options.FileSizeLimit = 10 * 1024 * 1024; // 10MB
                        options.RetainDays = 30;
                    });
            })
        .ConfigureServices(
            (hostContext, services) =>
            {
                services.Configure<List<ScheduledCommand>>(hostContext.Configuration.GetSection("ScheduledCommands"));
                services.AddHostedService<CommandExecutorService>();
            })
        .ConfigureAppConfiguration(
            (hostContext, config) =>
            {
                config.SetBasePath(AppDomain.CurrentDomain.BaseDirectory);
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
            });
}



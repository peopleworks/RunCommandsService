using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RunCommandsService
{
    /// <summary>
    /// Backward-compatible no-op service. The real HTTP endpoint is served by Monitoring.
    /// This class exists only to satisfy older Program.cs registrations.
    /// </summary>
    public class HealthHttpServerService : IHostedService
    {
        private readonly ILogger<HealthHttpServerService> _logger;
        public HealthHttpServerService(ILogger<HealthHttpServerService> logger) { _logger = logger; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("HealthHttpServerService (noop) started");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("HealthHttpServerService (noop) stopped");
            return Task.CompletedTask;
        }
    }
}

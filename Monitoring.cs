using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RunCommandsService
{
    public class SchedulerOptions
    {
        public int PollSeconds { get; set; } = 5;
        public int MaxParallelism { get; set; } = 1;
        public string DefaultTimeZone { get; set; } = "UTC";
    }

    public class MonitoringOptions
    {
        public bool EnableHttpEndpoint { get; set; } = false;
        public List<string> HttpPrefixes { get; set; } = new();
        public AlertThresholds AlertOn { get; set; } = new();
        public NotifiersOptions Notifiers { get; set; } = new();
    }

    public class AlertThresholds
    {
        public int ConsecutiveFailures { get; set; } = 3;
        public int ExecutionTimeMsThreshold { get; set; } = 60000;
    }

    public class NotifiersOptions
    {
        public EmailOptions Email { get; set; } = new();
        public WebhookOptions Webhook { get; set; } = new();
    }

    public class EmailOptions
    {
        public bool Enabled { get; set; }
        public string SmtpHost { get; set; } = "";
        public int SmtpPort { get; set; } = 25;
        public bool UseSsl { get; set; } = true;
        public string User { get; set; } = "";
        public string Password { get; set; } = "";
        public string From { get; set; } = "";
        public List<string> To { get; set; } = new();
    }

    public class WebhookOptions
    {
        public bool Enabled { get; set; }
        public string Url { get; set; } = "";
    }

    public class ExecutionEvent
    {
        public string CommandId { get; set; } = "";
        public string Command { get; set; } = "";
        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }
        public int? ExitCode { get; set; }
        public bool Success { get; set; }
        public bool SkippedDueToConflict { get; set; }
        public string? Error { get; set; }
        public long DurationMs => (long)(EndUtc - StartUtc).TotalMilliseconds;
    }

    public interface IAlertNotifier
    {
        Task NotifyAsync(string subject, string message, CancellationToken ct = default);
    }

    public class CompositeNotifier : IAlertNotifier
    {
        private readonly List<IAlertNotifier> _notifiers = new();

        public CompositeNotifier(IEnumerable<IAlertNotifier> notifiers) => _notifiers.AddRange(notifiers);

        public Task NotifyAsync(string subject, string message, CancellationToken ct = default)
        {
            var tasks = _notifiers.Select(n => SafeNotify(n, subject, message, ct));
            return Task.WhenAll(tasks);
        }

        private static async Task SafeNotify(IAlertNotifier n, string s, string m, CancellationToken ct)
        {
            try { await n.NotifyAsync(s, m, ct); } catch { /* swallow */ }
        }
    }

    public class EmailNotifier : IAlertNotifier
    {
        private readonly EmailOptions _opt;
        public EmailNotifier(EmailOptions opt) { _opt = opt; }

        public Task NotifyAsync(string subject, string message, CancellationToken ct = default)
        {
            if (!_opt.Enabled || string.IsNullOrWhiteSpace(_opt.SmtpHost) || !_opt.To.Any())
                return Task.CompletedTask;

            using var client = new SmtpClient(_opt.SmtpHost, _opt.SmtpPort)
            {
                EnableSsl = _opt.UseSsl
            };
            if (!string.IsNullOrEmpty(_opt.User))
            {
                client.Credentials = new NetworkCredential(_opt.User, _opt.Password);
            }
            foreach (var to in _opt.To)
            {
                var mail = new MailMessage(_opt.From, to, subject, message);
                client.Send(mail);
            }
            return Task.CompletedTask;
        }
    }

    public class WebhookNotifier : IAlertNotifier
    {
        private readonly WebhookOptions _opt;
        private static readonly HttpClient _http = new HttpClient();

        public WebhookNotifier(WebhookOptions opt) { _opt = opt; }

        public async Task NotifyAsync(string subject, string message, CancellationToken ct = default)
        {
            if (!_opt.Enabled || string.IsNullOrWhiteSpace(_opt.Url)) return;
            var payload = new { subject, message };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            try
            {
                await _http.PostAsync(_opt.Url, content, ct);
            }
            catch { /* ignore notifier failures */ }
        }
    }

    public class ExecutionMonitor
    {
        private readonly MonitoringOptions _options;
        private readonly IAlertNotifier _notifier;
        private readonly ILogger<ExecutionMonitor> _logger;

        private readonly ConcurrentQueue<ExecutionEvent> _events = new();
        private readonly ConcurrentDictionary<string, int> _consecutiveFailures = new();

        public ExecutionMonitor(IOptions<MonitoringOptions> options, IAlertNotifier notifier, ILogger<ExecutionMonitor> logger)
        {
            _options = options.Value;
            _notifier = notifier;
            _logger = logger;
        }

        public void Record(ExecutionEvent ev)
        {
            _events.Enqueue(ev);
            while (_events.Count > 1000 && _events.TryDequeue(out _)) { }

            if (ev.SkippedDueToConflict) return;

            if (!ev.Success)
            {
                var failures = _consecutiveFailures.AddOrUpdate(ev.CommandId, 1, (_, v) => v + 1);
                if (failures >= _options.AlertOn.ConsecutiveFailures)
                {
                    _ = _notifier.NotifyAsync(
                        $"[ALERT] {ev.CommandId} failing consecutively ({failures})",
                        $"Command: {ev.Command}\nLast error: {ev.Error}\nDuration: {ev.DurationMs}ms\nTime: {DateTime.UtcNow:o}");
                    _logger.LogWarning("Alert triggered for {CommandId}: {Failures} consecutive failures", ev.CommandId, failures);
                }
            }
            else
            {
                _consecutiveFailures.TryRemove(ev.CommandId, out _);
                if (ev.DurationMs >= _options.AlertOn.ExecutionTimeMsThreshold)
                {
                    _ = _notifier.NotifyAsync(
                        $"[WARN] {ev.CommandId} exceeded duration threshold",
                        $"Command: {ev.Command}\nDuration: {ev.DurationMs}ms (threshold: {_options.AlertOn.ExecutionTimeMsThreshold})\nTime: {DateTime.UtcNow:o}");
                }
            }
        }

        public object Snapshot()
        {
            var arr = _events.ToArray();
            return new
            {
                nowUtc = DateTime.UtcNow,
                recentCount = arr.Length,
                recent = arr.TakeLast(100).ToArray(),
                consecutiveFailures = _consecutiveFailures.ToDictionary(k => k.Key, v => v.Value)
            };
        }
    }

    public class HealthHttpServerService : BackgroundService
    {
        private readonly MonitoringOptions _options;
        private readonly ExecutionMonitor _monitor;
        private readonly ILogger<HealthHttpServerService> _logger;
        private HttpListener? _listener;

        public HealthHttpServerService(IOptions<MonitoringOptions> options, ExecutionMonitor monitor, ILogger<HealthHttpServerService> logger)
        {
            _options = options.Value;
            _monitor = monitor;
            _logger = logger;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.EnableHttpEndpoint || _options.HttpPrefixes.Count == 0)
            {
                _logger.LogInformation("Health HTTP endpoint disabled.");
                return Task.CompletedTask;
            }

            _listener = new HttpListener();
            foreach (var p in _options.HttpPrefixes)
                _listener.Prefixes.Add(p);

            _listener.Start();
            _ = Task.Run(() => AcceptLoop(stoppingToken), stoppingToken);
            _logger.LogInformation("Health HTTP endpoint listening on {Prefixes}", string.Join(", ", _options.HttpPrefixes));
            return Task.CompletedTask;
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && _listener != null && _listener.IsListening)
                {
                    var ctx = await _listener.GetContextAsync();
                    _ = Task.Run(() => Handle(ctx), ct);
                }
            }
            catch (ObjectDisposedException) { }
            catch (HttpListenerException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Health endpoint error");
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            try
            {
                var snap = _monitor.Snapshot();
                var json = JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true });
                var bytes = Encoding.UTF8.GetBytes(json);
                ctx.Response.ContentType = "application/json";
                ctx.Response.StatusCode = 200;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch
            {
                try { ctx.Response.StatusCode = 500; } catch { }
            }
            finally
            {
                try { ctx.Response.OutputStream.Close(); } catch { }
            }
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _listener?.Close();
            return base.StopAsync(cancellationToken);
        }
    }

    public class ConcurrencyManager
    {
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new();
        private readonly SemaphoreSlim _global;
        public ConcurrencyManager(int maxParallelism)
        {
            if (maxParallelism <= 0) maxParallelism = 1;
            _global = new SemaphoreSlim(maxParallelism, maxParallelism);
        }

        public async Task<IDisposable?> TryAcquireAsync(string key, bool allowParallelRuns, CancellationToken ct)
        {
            await _global.WaitAsync(ct);
            if (allowParallelRuns) return new Releaser(_global, null);

            var sem = _keyLocks.GetOrAdd(key ?? "_default_", _ => new SemaphoreSlim(1, 1));
            var locked = await sem.WaitAsync(0, ct);
            if (!locked)
            {
                _global.Release();
                return null;
            }
            return new Releaser(_global, sem);
        }

        private sealed class Releaser : IDisposable
        {
            private readonly SemaphoreSlim _global;
            private readonly SemaphoreSlim? _key;
            public Releaser(SemaphoreSlim g, SemaphoreSlim? k) { _global = g; _key = k; }
            public void Dispose()
            {
                _key?.Release();
                _global.Release();
            }
        }
    }
}

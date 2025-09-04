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
using System.IO;
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
        public DashboardOptions Dashboard { get; set; } = new();
    }

    public class DashboardOptions
    {
        public bool Enabled { get; set; } = true;
        public string Title { get; set; } = "Scheduled Command Executor";
        public int AutoRefreshSeconds { get; set; } = 5;
        public bool ShowRawJsonToggle { get; set; } = true;
        public string HtmlPath { get; set; } = "dashboard.html"; // external HTML file
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

        public string SubjectTemplate { get; set; } = "[${AlertType}] ${CommandId} (${ConsecutiveFailures}x) — ${DurationMs}ms";
        public string BodyTemplate { get; set; } =
            "Command: ${Command}\nStarted: ${StartUtc:o}\nEnded: ${EndUtc:o}\nExitCode: ${ExitCode}\nDuration: ${DurationMs}ms\nError: ${Error}\nMessage: ${CustomMessage}";
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
            => Task.WhenAll(_notifiers.Select(n => SafeNotify(n, subject, message, ct)));
        private static async Task SafeNotify(IAlertNotifier n, string s, string m, CancellationToken ct)
        { try { await n.NotifyAsync(s, m, ct); } catch { } }
    }

    public class EmailNotifier : IAlertNotifier
    {
        private readonly EmailOptions _opt;
        public EmailNotifier(EmailOptions opt) { _opt = opt; }
        public Task NotifyAsync(string subject, string message, CancellationToken ct = default)
        {
            if (!_opt.Enabled || string.IsNullOrWhiteSpace(_opt.SmtpHost) || !_opt.To.Any())
                return Task.CompletedTask;
            using var client = new SmtpClient(_opt.SmtpHost, _opt.SmtpPort) { EnableSsl = _opt.UseSsl };
            if (!string.IsNullOrEmpty(_opt.User))
                client.Credentials = new NetworkCredential(_opt.User, _opt.Password);
            foreach (var to in _opt.To)
                using (var mail = new MailMessage(_opt.From, to, subject, message)) client.Send(mail);
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
            try { await _http.PostAsync(_opt.Url, content, ct); } catch { }
        }
    }

    public class ScheduledCommandView
    {
        public string Id { get; set; } = "";
        public string Command { get; set; } = "";
        public string CronExpression { get; set; } = "";
        public string TimeZone { get; set; } = "UTC";
        public bool Enabled { get; set; } = true;
        public bool AllowParallelRuns { get; set; } = false;
        public string ConcurrencyKey { get; set; } = "";
        public int? MaxRuntimeMinutes { get; set; }
        public string? NextRunUtc { get; set; }
        public string? CustomAlertMessage { get; set; }
    }

    public class ExecutionMonitor
    {
        private readonly MonitoringOptions _options;
        private readonly IAlertNotifier _notifier;
        private readonly ILogger<ExecutionMonitor> _logger;

        private readonly ConcurrentQueue<ExecutionEvent> _events = new();
        private readonly ConcurrentDictionary<string, int> _consecutiveFailures = new();
        private readonly ConcurrentDictionary<string, string?> _customAlertMessages = new();
        private readonly ConcurrentDictionary<string, ScheduledCommandView> _schedule = new();

        public ExecutionMonitor(IOptions<MonitoringOptions> options, IAlertNotifier notifier, ILogger<ExecutionMonitor> logger)
        {
            _options = options.Value;
            _notifier = notifier;
            _logger = logger;
        }

        public void UpdateScheduleSnapshot(IEnumerable<ScheduledCommandView> commands)
        {
            _schedule.Clear();
            foreach (var c in commands)
            {
                _schedule[c.Id] = c;
                _customAlertMessages[c.Id] = c.CustomAlertMessage;
            }
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
                    var (sub, body) = BuildEmail(ev, "Fail", failures);
                    _ = _notifier.NotifyAsync(sub, body);
                    _logger.LogWarning("Alert triggered for {CommandId}: {Failures} consecutive failures", ev.CommandId, failures);
                }
            }
            else
            {
                _consecutiveFailures.TryRemove(ev.CommandId, out _);
                if (ev.DurationMs >= _options.AlertOn.ExecutionTimeMsThreshold)
                {
                    var (sub, body) = BuildEmail(ev, "Slow", null);
                    _ = _notifier.NotifyAsync(sub, body);
                }
            }
        }

        private (string subject, string body) BuildEmail(ExecutionEvent ev, string alertType, int? consecutiveFailures)
        {
            var email = _options.Notifiers.Email;
            _customAlertMessages.TryGetValue(ev.CommandId, out var custom);
            var tokens = new Dictionary<string, string?>
            {
                ["${AlertType}"] = alertType,
                ["${CommandId}"] = ev.CommandId,
                ["${Command}"] = ev.Command,
                ["${StartUtc}"] = ev.StartUtc.ToString("u"),
                ["${StartUtc:o}"] = ev.StartUtc.ToString("o"),
                ["${EndUtc}"] = ev.EndUtc.ToString("u"),
                ["${EndUtc:o}"] = ev.EndUtc.ToString("o"),
                ["${ExitCode}"] = ev.ExitCode?.ToString(),
                ["${DurationMs}"] = ev.DurationMs.ToString(),
                ["${Error}"] = ev.Error,
                ["${ConsecutiveFailures}"] = consecutiveFailures?.ToString(),
                ["${CustomMessage}"] = custom ?? ""
            };
            string sub = ReplaceTokens(email.SubjectTemplate, tokens);
            string body = ReplaceTokens(email.BodyTemplate, tokens);
            return (sub, body);
        }

        private static string ReplaceTokens(string template, IDictionary<string, string?> tokens)
        {
            string result = template ?? "";
            foreach (var kv in tokens) result = result.Replace(kv.Key, kv.Value ?? "");
            return result;
        }

        public object Snapshot()
        {
            var arr = _events.ToArray();
            return new
            {
                nowUtc = DateTime.UtcNow,
                recentCount = arr.Length,
                recent = arr.TakeLast(200).ToArray(),
                consecutiveFailures = _consecutiveFailures.ToDictionary(k => k.Key, v => v.Value),
                scheduled = _schedule.Values.OrderBy(s => s.Id).ToArray()
            };
        }
    }

    public class HealthHttpServerService : BackgroundService
    {
        private readonly MonitoringOptions _options;
        private readonly ExecutionMonitor _monitor;
        private readonly ILogger<HealthHttpServerService> _logger;
        private HttpListener? _listener;

        // dashboard html (external file)
        private string? _dashboardHtmlCache;
        private FileSystemWatcher? _htmlWatcher;
        private string _dashboardPath = "";

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
            foreach (var p in _options.HttpPrefixes) _listener.Prefixes.Add(p);
            _listener.Start();
            _ = Task.Run(() => AcceptLoop(stoppingToken), stoppingToken);
            _logger.LogInformation("Health HTTP endpoint listening on {Prefixes}", string.Join(", ", _options.HttpPrefixes));

            _dashboardPath = ResolveHtmlPath(_options.Dashboard.HtmlPath);
            SetupHtmlWatcher(_dashboardPath);

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
            catch (Exception ex) { _logger.LogError(ex, "Health endpoint error"); }
        }

        private void Handle(HttpListenerContext ctx)
        {
            try
            {
                var path = ctx.Request.Url?.AbsolutePath?.ToLowerInvariant() ?? "/";
                if (path == "/" && _options.Dashboard.Enabled)
                {
                    var html = GetDashboardHtml();
                    var bytes = Encoding.UTF8.GetBytes(html);
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    ctx.Response.StatusCode = 200;
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                }
                else if (path == "/api/health")
                {
                    var snap = _monitor.Snapshot();
                    var json = JsonSerializer.Serialize(
                        snap,
                        new JsonSerializerOptions
                        {
                            WriteIndented = true,
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase
                        });
                    var bytes = Encoding.UTF8.GetBytes(json);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.StatusCode = 200;
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                }
            }
            catch { try { ctx.Response.StatusCode = 500; } catch { } }
            finally { try { ctx.Response.OutputStream.Close(); } catch { } }
        }

        private string GetDashboardHtml()
        {
            if (_dashboardHtmlCache != null) return _dashboardHtmlCache;

            try
            {
                if (File.Exists(_dashboardPath))
                {
                    var raw = File.ReadAllText(_dashboardPath, Encoding.UTF8);
                    _dashboardHtmlCache = ApplyDashboardTokens(raw);
                    return _dashboardHtmlCache;
                }
                _logger.LogWarning("Dashboard HTML not found at {Path}. Serving fallback page.", _dashboardPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed loading dashboard HTML from {Path}", _dashboardPath);
            }

            // Fallback
            return _dashboardHtmlCache = $@"<!doctype html>
<html><head><meta charset=""utf-8""><title>{WebUtility.HtmlEncode(_options.Dashboard.Title)}</title></head>
<body>
<h1>{WebUtility.HtmlEncode(_options.Dashboard.Title)}</h1>
<p>Dashboard file not found at <code>{WebUtility.HtmlEncode(_dashboardPath)}</code>.</p>
<p>API is available at <a href=""/api/health"">/api/health</a>.</p>
</body></html>";
        }

        private string ApplyDashboardTokens(string html)
        {
            string toggle = _options.Dashboard.ShowRawJsonToggle
                ? "<label><input id=\"toggleRaw\" type=\"checkbox\" checked> Show raw JSON</label>"
                : "";
            return html
                .Replace("{{TITLE}}", WebUtility.HtmlEncode(_options.Dashboard.Title))
                .Replace("{{AUTO_REFRESH_SECONDS}}", _options.Dashboard.AutoRefreshSeconds.ToString())
                .Replace("{{RAW_TOGGLE}}", toggle);
        }

        private static string ResolveHtmlPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) path = "dashboard.html";
            return Path.IsPathRooted(path)
                ? path
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
        }

        private void SetupHtmlWatcher(string fullPath)
        {
            try
            {
                var dir = Path.GetDirectoryName(fullPath);
                var file = Path.GetFileName(fullPath);
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file)) return;

                _htmlWatcher = new FileSystemWatcher(dir, file)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size
                };
                _htmlWatcher.Changed += (_, __) =>
                {
                    _dashboardHtmlCache = null; // reload on next request
                    _logger.LogInformation("Dashboard HTML reloaded from disk.");
                };
                _htmlWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to watch dashboard HTML for changes.");
            }
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _htmlWatcher?.Dispose();
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

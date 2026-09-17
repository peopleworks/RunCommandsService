using Cronos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;


namespace RunCommandsService
{
    #region Options & DTOs
    public class MonitoringOptions
    {
        public bool EnableHttpEndpoint { get; set; } = true;

        public List<string> HttpPrefixes { get; set; } = new() { "http://localhost:5058/" };

        /// <summary>Maximum JSON request size accepted by write and cron-preview APIs.</summary>
        public int MaxRequestBodyBytes { get; set; } = 64 * 1024;

        public AlertThresholds AlertOn { get; set; } = new();

        public NotifiersOptions Notifiers { get; set; } = new();

        public DashboardOptions Dashboard { get; set; } = new();

        public ExecutionHistoryOptions ExecutionHistory { get; set; } = new();

        // Admin key for administrative APIs
        public string? AdminKey { get; set; }
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
        public int ConsecutiveFailures { get; set; } = 2;

        public int SlowRunMs { get; set; } = 120000;

        // Backcompat with older config key name
        public int ExecutionTimeMsThreshold
        {
            get => SlowRunMs;
            set => SlowRunMs = value;
        }

        public bool EmailOnFail { get; set; } = true;

        public bool EmailOnConsecutiveFailures { get; set; } = true;
    }

    public class NotifiersOptions
    {
        public EmailOptions Email { get; set; } = new();
        public WebhookOptions Webhook { get; set; } = new();
    }

    public class EmailOptions
    {
        public bool Enabled { get; set; } = false;

        public string From { get; set; } = string.Empty;

        public List<string> To { get; set; } = new();

        public string SmtpHost { get; set; } = string.Empty;

        public int SmtpPort { get; set; } = 587;

        public bool UseSsl { get; set; } = true;

        public string User { get; set; } = string.Empty;

        public string Password { get; set; } = string.Empty;

        public string SubjectTemplate
        {
            get;
            set;
        } = "[${AlertType}] ${CommandId} (${ConsecutiveFailures}x) — ${DurationMs}ms";

        public string BodyTemplate
        {
            get;
            set;
        } =
@"Command: ${Command}
Started: ${StartUtc:o}
Ended:   ${EndUtc:o}
ExitCode: ${ExitCode}
Duration: ${DurationMs}ms
Error:    ${Error}
Attempts: ${AttemptCount}/${MaxAttempts}
Message:  ${CustomMessage}";
    }

    public class ExecutionEvent
    {
        public string CommandId { get; set; } = string.Empty;

        public string Command { get; set; } = string.Empty;

        public DateTime StartUtc { get; set; }

        public DateTime EndUtc { get; set; }

        public int? ExitCode { get; set; }

        public bool Success { get; set; }

        public bool SkippedDueToConflict { get; set; }

        public string? Error { get; set; }

        public int DurationMs { get; set; }

        public int AttemptCount { get; set; } = 1;

        public int MaxAttempts { get; set; } = 1;

        public bool RetryExhausted { get; set; }

        public bool TimedOut { get; set; }

        public string TriggerSource { get; set; } = "scheduled";
    }

    public class ScheduledCommandView
    {
        public string Id { get; set; } = string.Empty;

        public string Command { get; set; } = string.Empty;

        public string CronExpression { get; set; } = string.Empty;

        public string TimeZone { get; set; } = "UTC";

        public bool Enabled { get; set; }

        public bool AllowParallelRuns { get; set; }

        public string ConcurrencyKey { get; set; } = string.Empty;

        public int? MaxRuntimeMinutes { get; set; }

        public RetryOptions Retry { get; set; } = new();

        public string? NextRunUtc { get; set; }

        public string? CustomAlertMessage { get; set; }
    }

    public record CronPreviewReq(string Cron, string TimeZone);

    public class ScheduledCommandPayload
    {
        public string Id { get; set; } = string.Empty;

        public string Command { get; set; } = string.Empty;

        public string CronExpression { get; set; } = string.Empty;

        public string TimeZone { get; set; } = "UTC";

        public bool Enabled { get; set; }

        public bool AllowParallelRuns { get; set; }

        public string? ConcurrencyKey { get; set; }

        public int? MaxRuntimeMinutes { get; set; }

        public RetryOptions Retry { get; set; } = new();

        public string? NextRunUtc { get; set; }

        public string? NextRunLocal { get; set; }

        public string? CustomAlertMessage { get; set; }
    }

    public sealed class ConfigurationImportRequest
    {
        public string Mode { get; set; } = "replace";
        public List<ScheduledCommand> ScheduledCommands { get; set; } = new();
    }
    #endregion

    #region Notifiers
    public interface IAlertNotifier
    {
        Task NotifyAsync(string subject, string message, CancellationToken ct = default);
    }

    public class CompositeNotifier : IAlertNotifier
    {
        private readonly List<IAlertNotifier> _notifiers = new();
        public CompositeNotifier(IEnumerable<IAlertNotifier> notifiers) => _notifiers.AddRange(notifiers);

        public Task NotifyAsync(string subject, string message, CancellationToken ct = default) => Task.WhenAll(
            _notifiers.Select(n => SafeNotify(n, subject, message, ct)));

        private static async Task SafeNotify(IAlertNotifier n, string s, string m, CancellationToken ct)
        {
            try
            {
                await n.NotifyAsync(s, m, ct);
            } catch
            {
            }
        }
    }

    public class EmailNotifier : IAlertNotifier
    {
        private readonly EmailOptions _opt;
        public EmailNotifier(EmailOptions opt) { _opt = opt; }

        public Task NotifyAsync(string subject, string message, CancellationToken ct = default)
        {
            if(!_opt.Enabled || string.IsNullOrWhiteSpace(_opt.SmtpHost) || _opt.To == null || _opt.To.Count == 0)
                return Task.CompletedTask;

            using var client = new SmtpClient(_opt.SmtpHost, _opt.SmtpPort) { EnableSsl = _opt.UseSsl };
            if(!string.IsNullOrEmpty(_opt.User))
                client.Credentials = new NetworkCredential(_opt.User, _opt.Password);

            foreach(var to in _opt.To)
            {
                using var mail = new MailMessage(_opt.From, to, subject, message);
                client.Send(mail);
            }
            return Task.CompletedTask;
        }
    }
    #endregion

    #region Execution Monitor
    public class ExecutionMonitor
    {
        private readonly MonitoringOptions _options = new();
        private readonly IAlertNotifier _notifier;
        private readonly ILogger<ExecutionMonitor> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ExecutionHistoryStore _historyStore;

        private readonly ConcurrentQueue<ExecutionEvent> _events = new();
        private readonly ConcurrentDictionary<string, int> _consecutiveFailures = new();
        private readonly object _scheduleLock = new();
        private List<ScheduledCommandView> _scheduleSnapshot = new();
        private Func<object>? _schedulerHealthProvider;

        public ExecutionMonitor(
            IOptions<MonitoringOptions> options,
            ILogger<ExecutionMonitor> logger,
            ILoggerFactory loggerFactory,
            ExecutionHistoryStore historyStore)
        {
            _options = options.Value ?? new MonitoringOptions();
            _logger = logger;
            _loggerFactory = loggerFactory;
            _historyStore = historyStore;
            var notifiers = new List<IAlertNotifier> { new EmailNotifier(_options.Notifiers.Email) };
            try
            {
                if(_options?.Notifiers?.Webhook != null)
                {
                    var whLogger = _loggerFactory.CreateLogger<WebhookNotifier>();
                    notifiers.Add(new WebhookNotifier(Microsoft.Extensions.Options.Options.Create(_options.Notifiers.Webhook), whLogger));
                }
            }
            catch { /* ignore wiring errors */ }
            _notifier = new CompositeNotifier(notifiers);

            foreach (var execution in _historyStore.GetRecent(5000).Reverse())
            {
                _events.Enqueue(execution);
                ApplyConsecutiveFailure(execution);
            }
        }

        public void UpdateScheduleSnapshot(IEnumerable<ScheduledCommandView> snapshot)
        {
            lock(_scheduleLock)
            {
                _scheduleSnapshot = snapshot?.ToList() ?? new List<ScheduledCommandView>();
            }
        }

        public void SetSchedulerHealthProvider(Func<object> healthProvider)
        {
            _schedulerHealthProvider = healthProvider;
        }

        public void Record(ExecutionEvent ev)
        {
            ev.DurationMs = (int)(ev.EndUtc - ev.StartUtc).TotalMilliseconds;
            _events.Enqueue(ev);
            while(_events.Count > 5000 && _events.TryDequeue(out _))
            {
            } // cap memory

            _historyStore.Append(ev);

            // update consecutive failures
            var failureCount = ApplyConsecutiveFailure(ev);
            if (ev.SkippedDueToConflict)
                return;
            if (!ev.Success)
            {
                if(_options.AlertOn.EmailOnFail)
                    FireAlert("Failure", ev, failureCount);
                if(_options.AlertOn.EmailOnConsecutiveFailures && failureCount >= _options.AlertOn.ConsecutiveFailures)
                    FireAlert($"Consecutive failures ({failureCount})", ev, failureCount);
            }

            // slow run?
            if(ev.DurationMs >= _options.AlertOn.SlowRunMs && _options.AlertOn.SlowRunMs > 0)
            {
                FireAlert("Slow run", ev, _consecutiveFailures.GetValueOrDefault(ev.CommandId, 0));
            }
        }

        private int ApplyConsecutiveFailure(ExecutionEvent ev)
        {
            if (ev.SkippedDueToConflict)
                return _consecutiveFailures.GetValueOrDefault(ev.CommandId, 0);
            if (ev.Success)
            {
                _consecutiveFailures[ev.CommandId] = 0;
                return 0;
            }

            return _consecutiveFailures.AddOrUpdate(ev.CommandId, 1, (_, value) => value + 1);
        }

        private void FireAlert(string alertType, ExecutionEvent ev, int consecutiveFailCount)
        {
            try
            {
                var schedule = _scheduleSnapshot.FirstOrDefault(s => s.Id == ev.CommandId);
                string subject = _options.Notifiers.Email.SubjectTemplate
                    .Replace("${AlertType}", alertType)
                    .Replace("${CommandId}", ev.CommandId)
                    .Replace("${ConsecutiveFailures}", consecutiveFailCount.ToString())
                    .Replace("${DurationMs}", ev.DurationMs.ToString());

                string body = _options.Notifiers.Email.BodyTemplate
                    .Replace("${Command}", ev.Command ?? string.Empty)
                    .Replace("${StartUtc}", ev.StartUtc.ToString("o"))
                    .Replace("${EndUtc}", ev.EndUtc.ToString("o"))
                    .Replace("${ExitCode}", ev.ExitCode?.ToString() ?? "null")
                    .Replace("${DurationMs}", ev.DurationMs.ToString())
                    .Replace("${AttemptCount}", ev.AttemptCount.ToString())
                    .Replace("${MaxAttempts}", ev.MaxAttempts.ToString())
                    .Replace("${Error}", ev.Error ?? string.Empty)
                    .Replace("${CustomMessage}", schedule?.CustomAlertMessage ?? string.Empty);

                _ = _notifier.NotifyAsync(subject, body);
            } catch(Exception ex)
            {
                _logger.LogError(ex, "Error while sending alert");
            }
        }


        public object GetHealthPayload()
        {
            var recent = _events.Reverse().Take(100).ToList(); // newest first

            List<ScheduledCommandView> schedule;
            lock(_scheduleLock)
                schedule = _scheduleSnapshot.ToList();

            // Build a concrete, serializer-friendly list
            var scheduledForPayload = new List<ScheduledCommandPayload>(schedule.Count);
            foreach(var s in schedule)
            {
                string? nextLocal = null;
                try
                {
                    if(!string.IsNullOrWhiteSpace(s.NextRunUtc) &&
                        DateTime.TryParse(
                            s.NextRunUtc,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                            out var nextUtc))
                    {
                        var tzId = string.IsNullOrWhiteSpace(s.TimeZone) ? "UTC" : s.TimeZone;
                        var tz = TimeZoneHelper.FindTimeZone(tzId);
                        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nextUtc, DateTimeKind.Utc), tz);
                        nextLocal = $"{local:yyyy-MM-dd HH:mm:ss} ({tz.Id})";
                    }
                } catch
                {
                    // leave nextLocal = null on any failure
                }

                scheduledForPayload.Add(
                    new ScheduledCommandPayload
                    {
                        Id = s.Id,
                        Command = s.Command,
                        CronExpression = s.CronExpression,
                        TimeZone = s.TimeZone,
                        Enabled = s.Enabled,
                        AllowParallelRuns = s.AllowParallelRuns,
                        ConcurrencyKey = s.ConcurrencyKey,
                        MaxRuntimeMinutes = s.MaxRuntimeMinutes,
                        Retry = s.Retry,
                        NextRunUtc = s.NextRunUtc,
                        NextRunLocal = nextLocal,
                        CustomAlertMessage = s.CustomAlertMessage
                    });
            }

            return new
            {
                version = GetProductVersion(),
                nowUtc = DateTime.UtcNow.ToString("o"),
                recentCount = recent.Count,
                recent,
                metrics = _historyStore.GetMetrics(),
                history = new { enabled = _historyStore.Enabled },
                consecutiveFailures = _consecutiveFailures.ToDictionary(kv => kv.Key, kv => kv.Value),
                scheduled = scheduledForPayload,

                // Scheduler health status
                schedulerHealth = _schedulerHealthProvider?.Invoke(),

                // NEW: simple UI hints for the dashboard
                ui = new
                {
                    showRawJsonToggle = _options?.Dashboard?.ShowRawJsonToggle == true
                }
            };
        }


        private static string GetProductVersion()
        {
            try
            {
                var asm = typeof(Monitoring).Assembly;
                var info = asm.GetCustomAttributes(
                    typeof(System.Reflection.AssemblyInformationalVersionAttribute),
                    false)
                    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                    .FirstOrDefault()?.InformationalVersion;
                if(!string.IsNullOrWhiteSpace(info))
                    return info;

                // Fall back to AssemblyVersion
                return asm.GetName().Version?.ToString() ?? "unknown";
            } catch
            {
                return "unknown";
            }
        }
    }
    #endregion

    #region HTTP Monitoring Host
    public class Monitoring : IHostedService
    {
        private readonly IConfiguration _configuration;
        private readonly IOptions<MonitoringOptions> _options;
        private readonly ExecutionMonitor _monitor;
        private readonly ILogger<Monitoring> _logger;
        private readonly IManualJobRunner _manualJobRunner;
        private readonly ExecutionHistoryStore _historyStore;

        private HttpListener? _listener;
        private string _dashboardPath = string.Empty;
        private FileSystemWatcher? _htmlWatcher;
        private static readonly object ConfigWriteLock = new();
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public Monitoring(
            IConfiguration configuration,
            IOptions<MonitoringOptions> options,
            ExecutionMonitor monitor,
            IManualJobRunner manualJobRunner,
            ExecutionHistoryStore historyStore,
            ILogger<Monitoring> logger)
        {
            _configuration = configuration;
            _options = options;
            _monitor = monitor;
            _manualJobRunner = manualJobRunner;
            _historyStore = historyStore;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (!_options.Value.EnableHttpEndpoint)
                return Task.CompletedTask;

            _listener = new HttpListener();

            var prefixes = (_options.Value.HttpPrefixes ?? new List<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .Select(p => p.EndsWith("/") ? p : p + "/")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (prefixes.Count == 0)
                prefixes.Add("http://localhost:5058/");

            // ✅ Use the normalized list
            foreach (var p in prefixes)
                _listener.Prefixes.Add(p);

            try
            {
                _listener.Start();
                _ = AcceptLoop(cancellationToken);
                _logger.LogInformation("Health HTTP endpoint listening on {Prefixes}", string.Join(", ", prefixes));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start HTTP endpoint");
            }

            _dashboardPath = ResolveHtmlPath(_options.Value.Dashboard.HtmlPath);
            SetupHtmlWatcher(_dashboardPath);
            return Task.CompletedTask;
        }


        public Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                _htmlWatcher?.Dispose();
                _listener?.Stop();
                _listener?.Close();
            } catch
            {
            }
            return Task.CompletedTask;
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            try
            {
                while(!ct.IsCancellationRequested && _listener != null && _listener.IsListening)
                {
                    var ctx = await _listener.GetContextAsync();
                    _ = Task.Run(() => Handle(ctx), ct);
                }
            } catch(Exception ex)
            {
                if(!ct.IsCancellationRequested)
                    _logger.LogError(ex, "HTTP accept loop error");
            }
        }

        // -------------- SYNCHRONOUS HANDLER (NO await HERE) ----------------
        private void Handle(HttpListenerContext ctx)
        {
            try
            {
                ApplySecurityHeaders(ctx.Response);
                string path = ctx.Request.Url?.AbsolutePath ?? string.Empty;
                var pathLower = path.ToLowerInvariant();
                if(path == "/" || path == "/dashboard")
                {
                    ServeDashboard(ctx);
                } else if(pathLower == "/api/health")
                {
                    var payload = _monitor.GetHealthPayload();
                    WriteJson(ctx, payload, 200);
                } else if(pathLower == "/api/logs")
                {
                    ServeLogsTail(ctx);
                } else if(pathLower == "/api/history" && ctx.Request.HttpMethod == "GET")
                {
                    ServeHistory(ctx);
                } else if(pathLower == "/api/config/export" && ctx.Request.HttpMethod == "GET")
                {
                    if (!RequireAuthorization(ctx))
                        return;
                    ExportConfiguration(ctx);
                } else if(pathLower == "/api/config/import" && ctx.Request.HttpMethod == "POST")
                {
                    if (!RequireAuthorization(ctx))
                        return;
                    var body = ReadBody(ctx);
                    var request = JsonSerializer.Deserialize<ConfigurationImportRequest>(body, JsonOptions)
                                  ?? throw new JsonException("Body must contain an import object.");
                    ImportConfiguration(ctx, request);
                } else if(TryGetManualRunJobId(path, out var manualJobId) && ctx.Request.HttpMethod == "POST")
                {
                    if (!RequireAuthorization(ctx))
                        return;
                    QueueManualRun(ctx, manualJobId);
                } else if(pathLower == "/api/jobs" && ctx.Request.HttpMethod == "GET")
                {
                    var jobs = ReadJobsRaw();
                    WriteJson(ctx, new { ok = true, jobs }, 200);
                } else if((pathLower == "/api/jobs/validatecron" || pathLower == "/api/jobs/validatecron/" || pathLower == "/api/jobs/validatecron") && ctx.Request.HttpMethod == "POST"
                    || (path == "/api/jobs/validateCron" && ctx.Request.HttpMethod == "POST"))
                {
                    var body = ReadBody(ctx);
                    var dto = JsonSerializer.Deserialize<CronPreviewReq>(body)
                              ?? throw new JsonException("Body must contain cron and timeZone.");
                    ValidateCron(ctx, dto);
                } else if(pathLower == "/api/jobs" && ctx.Request.HttpMethod == "POST")
                {
                    if(!RequireAuthorization(ctx))
                        return;
                    var body = ReadBody(ctx);
                    var job = JsonSerializer.Deserialize<Dictionary<string, object?>>(body)
                              ?? throw new JsonException("Body must contain a job object.");
                    CreateJob(ctx, job);
                } else if(pathLower.StartsWith("/api/jobs/") &&
                    (ctx.Request.HttpMethod == "PUT" || ctx.Request.HttpMethod == "DELETE"))
                {
                    if(!RequireAuthorization(ctx))
                        return;
                    var id = path.Split('/').Last();
                    if(ctx.Request.HttpMethod == "DELETE")
                    {
                        DeleteJob(ctx, id);
                    } else
                    {
                        var body = ReadBody(ctx);
                        var job = JsonSerializer.Deserialize<Dictionary<string, object?>>(body)
                                  ?? throw new JsonException("Body must contain a job object.");
                        UpdateJob(ctx, id, job);
                    }
                } else
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                }
            } catch(RequestBodyTooLargeException ex)
            {
                WriteJson(ctx, new { ok = false, error = ex.Message }, 413);
            } catch(UnsupportedMediaTypeException ex)
            {
                WriteJson(ctx, new { ok = false, error = ex.Message }, 415);
            } catch(JsonException ex)
            {
                WriteJson(ctx, new { ok = false, error = $"Invalid JSON: {ex.Message}" }, 400);
            } catch(Exception ex)
            {
                try
                {
                    _logger.LogError(ex, "HTTP handler error");
                    ctx.Response.StatusCode = 500;
                    var b = Encoding.UTF8.GetBytes("Internal error");
                    ctx.Response.OutputStream.Write(b, 0, b.Length);
                    ctx.Response.Close();
                } catch
                {
                }
            }
        }

        private static void ApplySecurityHeaders(HttpListenerResponse response)
        {
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["X-Frame-Options"] = "DENY";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["Content-Security-Policy"] =
                "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; " +
                "img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        }
        #endregion

        #region Dashboard & Logs (sync)
        private void ServeDashboard(HttpListenerContext ctx)
        {
            try
            {
                if (!_options.Value.Dashboard.Enabled)
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    return;
                }

                string html;
                try
                {
                    if (File.Exists(_dashboardPath))
                        html = File.ReadAllText(_dashboardPath, Encoding.UTF8);
                    else
                        html = "<!doctype html><meta charset=\"utf-8\"><title>Dashboard</title><h1>Dashboard file not found</h1>";
                }
                catch
                {
                    html = "<!doctype html><meta charset=\"utf-8\"><title>Dashboard</title><h1>Error reading dashboard.html</h1>";
                }

                html = (html ?? string.Empty)
                    .Replace("{{TITLE}}", _options.Value.Dashboard.Title ?? "Scheduled Command Executor")
                    .replaceInsensitive("{{AUTO_REFRESH_SECONDS}}", (_options.Value.Dashboard.AutoRefreshSeconds > 0 ? _options.Value.Dashboard.AutoRefreshSeconds : 5).ToString())
                    .Replace(
                        "{{RAW_TOGGLE}}",
                        _options.Value.Dashboard.ShowRawJsonToggle
                            ? "<label class=\"pill\"><input id=\"toggleRaw\" type=\"checkbox\" checked> Show raw JSON</label>"
                            : string.Empty);

                var bytes = Encoding.UTF8.GetBytes(html);
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.StatusCode = 200;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
            }
            catch (Exception ex)
            {
                try
                {
                    _logger.LogError(ex, "Error serving dashboard");
                    ctx.Response.StatusCode = 500;
                    var b = Encoding.UTF8.GetBytes("<h1>Internal error</h1>");
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    ctx.Response.OutputStream.Write(b, 0, b.Length);
                    ctx.Response.Close();
                }
                catch { /* swallow */ }
            }
        }


        private void ServeLogsTail(HttpListenerContext ctx)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new List<string>();

            // Daily FileLogger (log_*.txt) in base
            candidates.AddRange(SafeGetFiles(baseDir, "log_*.txt"));

            var logDir = Path.Combine(baseDir, "log");
            var logsDir = Path.Combine(baseDir, "logs");
            var LogsDir = Path.Combine(baseDir, "Logs");
            if(Directory.Exists(logDir))
                candidates.AddRange(SafeGetFiles(logDir, "*.txt"));
            if(Directory.Exists(logsDir))
                candidates.AddRange(SafeGetFiles(logsDir, "*.txt"));
            if(Directory.Exists(LogsDir))
                candidates.AddRange(SafeGetFiles(LogsDir, "*.txt"));

            if(candidates.Count == 0)
            {
                var msg = Encoding.UTF8.GetBytes("(no logs found in base/log/logs)");
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                ctx.Response.StatusCode = 200;
                ctx.Response.OutputStream.Write(msg, 0, msg.Length);
                ctx.Response.Close();
                return;
            }

            var latest = candidates.Select(f => new FileInfo(f)).OrderByDescending(fi => fi.LastWriteTimeUtc).First()
                .FullName;

            int tailKb = 128;
            int.TryParse(ctx.Request.QueryString["tailKb"], out tailKb);
            if(tailKb <= 0 || tailKb > 4096)
                tailKb = 128;

            byte[] buf;
            using(var fs = new FileStream(latest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long read = Math.Min(fs.Length, tailKb * 1024L);
                buf = new byte[read];
                fs.Seek(-read, SeekOrigin.End);
                fs.ReadExactly(buf);
            }

            ctx.Response.ContentType = "text/plain; charset=utf-8";
            ctx.Response.StatusCode = 200;
            ctx.Response.OutputStream.Write(buf, 0, buf.Length);
            ctx.Response.Close();
        }
        #endregion

        #region Job Builder helpers (sync)
        private void ValidateCron(HttpListenerContext ctx, CronPreviewReq dto)
        {
            try
            {
                if(dto == null || string.IsNullOrWhiteSpace(dto.Cron))
                {
                    WriteJson(ctx, new { ok = false, error = "Missing 'cron' field." }, 400);
                    return;
                }

                CronExpression cron;
                try
                {
                    cron = CronExpression.Parse(dto.Cron);
                } catch(Exception ex)
                {
                    WriteJson(ctx, new { ok = false, error = $"Invalid cron: {ex.Message}" }, 400);
                    return;
                }

                var tzId = string.IsNullOrWhiteSpace(dto.TimeZone) ? "UTC" : dto.TimeZone;
                TimeZoneInfo tz = TimeZoneHelper.FindTimeZone(tzId);

                var results = new List<string>(5);
                var cursorUtc = DateTime.UtcNow;

                for(int i = 0; i < 5; i++)
                {
                    // Cronos expects UTC base time when a time zone is provided
                    var nextUtc = cron.GetNextOccurrence(DateTime.SpecifyKind(cursorUtc, DateTimeKind.Utc), tz);
                    if(nextUtc == null)
                        break;

                    results.Add(nextUtc.Value.ToString("o"));
                    cursorUtc = nextUtc.Value.AddSeconds(1);
                }

                WriteJson(ctx, new { ok = true, timeZone = tz.Id, next = results }, 200);
            } catch(Exception ex)
            {
                WriteJson(ctx, new { ok = false, error = ex.Message }, 400);
            }
        }

        private static DateTime ConvertLocalToUtc(DateTime localUnspec, TimeZoneInfo tz)
        {
            if(tz.IsInvalidTime(localUnspec))
                localUnspec = localUnspec.AddHours(1);

            return TimeZoneInfo.ConvertTimeToUtc(localUnspec, tz);
        }


        private bool IsAuthorized(HttpListenerContext ctx)
        {
            var expected = _options.Value.AdminKey ?? _configuration["Monitoring:AdminKey"];
            if (SecretMasker.IsDefaultSecret(expected))
            {
                _logger.LogWarning("API administrative action rejected: Monitoring:AdminKey is using a default or unconfigured secret value. Please set a strong random key in configuration.");
                return false;
            }
            var got = ctx.Request.Headers["X-Admin-Key"];
            return SecretMasker.FixedTimeEquals(expected, got);
        }

        private bool RequireAuthorization(HttpListenerContext ctx)
        {
            if (IsAuthorized(ctx))
                return true;

            WriteJson(ctx, new { ok = false, error = "Unauthorized" }, 401);
            return false;
        }

        private static bool TryGetManualRunJobId(string path, out string jobId)
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 4 &&
                string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(segments[1], "jobs", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(segments[3], "run", StringComparison.OrdinalIgnoreCase))
            {
                jobId = Uri.UnescapeDataString(segments[2]);
                return !string.IsNullOrWhiteSpace(jobId);
            }

            jobId = string.Empty;
            return false;
        }

        private void QueueManualRun(HttpListenerContext ctx, string jobId)
        {
            var result = _manualJobRunner.QueueManualRun(jobId);
            if (!result.Accepted)
            {
                WriteJson(ctx, new { ok = false, error = result.Error },
                    string.Equals(result.Error, "Job not found.", StringComparison.Ordinal) ? 404 : 409);
                return;
            }

            WriteJson(ctx, new { ok = true, accepted = true, jobId, trigger = "manual" }, 202);
        }

        private void ServeHistory(HttpListenerContext ctx)
        {
            var jobId = ctx.Request.QueryString["jobId"];
            var requestedLimit = int.TryParse(ctx.Request.QueryString["limit"], out var parsed) ? parsed : 100;
            var limit = Math.Clamp(requestedLimit, 1, 5000);
            var events = _historyStore.GetRecent(limit, string.IsNullOrWhiteSpace(jobId) ? null : jobId);
            WriteJson(ctx, new { ok = true, count = events.Count, events }, 200);
        }

        private void ExportConfiguration(HttpListenerContext ctx)
        {
            var jobs = ReadJobsRaw();
            WriteJson(ctx, new
            {
                schemaVersion = 1,
                exportedAtUtc = DateTime.UtcNow.ToString("O"),
                scheduledCommands = jobs
            }, 200);
        }

        private void ImportConfiguration(HttpListenerContext ctx, ConfigurationImportRequest request)
        {
            var mode = request.Mode?.Trim().ToLowerInvariant() ?? "replace";
            if (mode is not ("replace" or "merge"))
            {
                WriteJson(ctx, new { ok = false, error = "Mode must be 'replace' or 'merge'." }, 400);
                return;
            }

            var incoming = request.ScheduledCommands ?? new List<ScheduledCommand>();
            if (incoming.Any(job => job == null))
            {
                WriteJson(ctx, new { ok = false, error = "ScheduledCommands cannot contain null entries." }, 400);
                return;
            }
            List<ScheduledCommand> finalJobs;
            lock (ConfigWriteLock)
            {
                if (mode == "merge")
                {
                    finalJobs = ReadJobsRaw()
                        .Select(job => JsonSerializer.Deserialize<ScheduledCommand>(JsonSerializer.Serialize(job), JsonOptions))
                        .Where(job => job != null)
                        .Cast<ScheduledCommand>()
                        .ToList();

                    foreach (var job in incoming)
                    {
                        var index = finalJobs.FindIndex(existing =>
                            string.Equals(existing.Id, job.Id, StringComparison.OrdinalIgnoreCase));
                        if (index >= 0)
                            finalJobs[index] = job;
                        else
                            finalJobs.Add(job);
                    }
                }
                else
                {
                    finalJobs = incoming;
                }

                var defaultTimeZone = _configuration["Scheduler:DefaultTimeZone"] ?? "UTC";
                var report = ConfigValidator.Validate(finalJobs, defaultTimeZone);
                if (!report.AllValid)
                {
                    WriteJson(ctx, new
                    {
                        ok = false,
                        error = "Imported jobs failed validation.",
                        problems = report.Jobs.Where(job => !job.IsValid)
                    }, 400);
                    return;
                }

                var rawJobs = finalJobs.Select(job =>
                        JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(job))
                        ?? throw new JsonException("Unable to serialize imported job."))
                    .ToList();
                WriteJobsRaw(rawJobs);
            }

            WriteJson(ctx, new { ok = true, mode, imported = incoming.Count, total = finalJobs.Count }, 200);
        }

        private static string ConfigPath() => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

        private static List<Dictionary<string, object?>> ReadJobsRaw()
        {
            var json = File.ReadAllText(ConfigPath(), Encoding.UTF8);
            using var doc = JsonDocument.Parse(json);
            if(!doc.RootElement.TryGetProperty("ScheduledCommands", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return new();
            var list = new List<Dictionary<string, object?>>();
            foreach(var el in arr.EnumerateArray())
                list.Add(JsonSerializer.Deserialize<Dictionary<string, object?>>(el.GetRawText())
                         ?? throw new InvalidDataException("ScheduledCommands contains a non-object entry."));
            return list;
        }

        private static void WriteJobsRaw(List<Dictionary<string, object?>> list)
        {
            var cfgPath = ConfigPath();
            var json = File.ReadAllText(cfgPath, Encoding.UTF8);
            var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(json)
                       ?? throw new InvalidDataException("appsettings.json must contain a JSON object.");
            dict["ScheduledCommands"] = list;
            var newJson = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });

            var tmp = cfgPath + ".tmp";
            var bak = cfgPath + ".bak";
            try
            {
                using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    writer.Write(newJson);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                if(File.Exists(bak))
                    File.Delete(bak);
                File.Replace(tmp, cfgPath, bak);
            }
            finally
            {
                if(File.Exists(tmp))
                    File.Delete(tmp);
            }
        }

        private void CreateJob(HttpListenerContext ctx, Dictionary<string, object?> job)
        {
            if(!TryValidateJobPayload(job, out var validationError))
            {
                WriteJson(ctx, new { ok = false, error = validationError }, 400);
                return;
            }

            var id = Convert.ToString(job["Id"]);
            lock (ConfigWriteLock)
            {
                var list = ReadJobsRaw();
                if(list.Any(
                    x => string.Equals(Convert.ToString(x.GetValueOrDefault("Id")), id, StringComparison.OrdinalIgnoreCase)))
                {
                    WriteJson(ctx, new { ok = false, error = "Id already exists" }, 409);
                    return;
                }

                list.Add(job);
                WriteJobsRaw(list);
            }
            WriteJson(ctx, new { ok = true }, 200);
        }

        private void UpdateJob(HttpListenerContext ctx, string id, Dictionary<string, object?> incoming)
        {
            incoming ??= new();
            incoming["Id"] = id;

            if(!TryValidateJobPayload(incoming, out var validationError))
            {
                WriteJson(ctx, new { ok = false, error = validationError }, 400);
                return;
            }

            lock (ConfigWriteLock)
            {
                var list = ReadJobsRaw();
                var idx = list.FindIndex(
                    x => string.Equals(Convert.ToString(x.GetValueOrDefault("Id")), id, StringComparison.OrdinalIgnoreCase));
                if(idx < 0)
                {
                    WriteJson(ctx, new { ok = false, error = "Not found" }, 404);
                    return;
                }

                list[idx] = incoming;
                WriteJobsRaw(list);
            }
            WriteJson(ctx, new { ok = true }, 200);
        }

        private bool TryValidateJobPayload(Dictionary<string, object?> job, out string error)
        {
            if(job == null)
            {
                error = "Invalid body";
                return false;
            }

            var candidate = JsonSerializer.Deserialize<ScheduledCommand>(
                JsonSerializer.Serialize(job),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if(candidate == null)
            {
                error = "Body must contain a job object.";
                return false;
            }

            var defaultTimeZone = _configuration["Scheduler:DefaultTimeZone"] ?? "UTC";
            var report = ConfigValidator.Validate(new List<ScheduledCommand> { candidate }, defaultTimeZone);
            var result = report.Jobs.Single();
            if(result.IsValid)
            {
                error = string.Empty;
                return true;
            }

            error = string.Join("; ", result.Problems);
            return false;
        }

        private void DeleteJob(HttpListenerContext ctx, string id)
        {
            lock (ConfigWriteLock)
            {
                var list = ReadJobsRaw();
                var newList = list.Where(
                    x => !string.Equals(Convert.ToString(x.GetValueOrDefault("Id")), id, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                WriteJobsRaw(newList);
            }
            WriteJson(ctx, new { ok = true }, 200);
        }

        private string ReadBody(HttpListenerContext ctx)
        {
            var contentType = ctx.Request.ContentType;
            if (string.IsNullOrWhiteSpace(contentType) ||
                !contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                throw new UnsupportedMediaTypeException("Content-Type must be application/json.");
            }

            var configuredLimit = _options.Value.MaxRequestBodyBytes;
            var maxBytes = Math.Clamp(configuredLimit, 1024, 1024 * 1024);
            return HttpRequestBodyReader.Read(
                ctx.Request.InputStream,
                ctx.Request.ContentEncoding ?? Encoding.UTF8,
                ctx.Request.ContentLength64,
                maxBytes);
        }

        private static void WriteJson(HttpListenerContext ctx, object obj, int statusCode)
        {
            var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.StatusCode = statusCode;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }

        private static string ResolveHtmlPath(string configured)
        {
            if(string.IsNullOrWhiteSpace(configured))
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dashboard.html");
            if(Path.IsPathRooted(configured))
                return configured;
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configured);
        }

        private void SetupHtmlWatcher(string path)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                var file = Path.GetFileName(path);
                if(!Directory.Exists(dir))
                    return;

                _htmlWatcher = new FileSystemWatcher(dir, file)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime
                };
                _htmlWatcher.Changed += (s, e) =>
                { /* file changes are picked up on next request */
                };
                _htmlWatcher.EnableRaisingEvents = true;
            } catch
            {
            }
        }

        private static IEnumerable<string> SafeGetFiles(string dir, string pattern)
        {
            try
            {
                return Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly);
            } catch
            {
                return Array.Empty<string>();
            }
        }
        #endregion
    }

    #region Small helpers
    internal static class StringReplaceExtensions
    {
        public static string replaceInsensitive(this string s, string find, string replaceWith) => s.Replace(
                find,
                replaceWith,
                StringComparison.OrdinalIgnoreCase);
    }

    public static class DictionaryExtensions
    {
        public static TValue? GetValueOrDefault<TKey, TValue>(
            this IDictionary<TKey, TValue>? d,
            TKey key,
            TValue? fallback = default) where TKey : notnull =>
            d != null && d.TryGetValue(key, out var v) ? v : fallback;
    }
    #endregion
}

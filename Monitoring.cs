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

        public AlertThresholds AlertOn { get; set; } = new();

        public NotifiersOptions Notifiers { get; set; } = new();

        public DashboardOptions Dashboard { get; set; } = new();

        // Admin key for Job Builder write APIs
        public string AdminKey { get; set; } = null;
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
Message:  ${CustomMessage}";
    }

    public class ExecutionEvent
    {
        public string CommandId { get; set; }

        public string Command { get; set; }

        public DateTime StartUtc { get; set; }

        public DateTime EndUtc { get; set; }

        public int? ExitCode { get; set; }

        public bool Success { get; set; }

        public bool SkippedDueToConflict { get; set; }

        public string Error { get; set; }

        public int DurationMs { get; set; }
    }

    public class ScheduledCommandView
    {
        public string Id { get; set; }

        public string Command { get; set; }

        public string CronExpression { get; set; }

        public string TimeZone { get; set; }

        public bool Enabled { get; set; }

        public bool AllowParallelRuns { get; set; }

        public string ConcurrencyKey { get; set; } = string.Empty;

        public int? MaxRuntimeMinutes { get; set; }

        public string NextRunUtc { get; set; }

        public string CustomAlertMessage { get; set; }
    }

    public record CronPreviewReq(string Cron, string TimeZone);

    public class ScheduledCommandPayload
    {
        public string Id { get; set; }

        public string Command { get; set; }

        public string CronExpression { get; set; }

        public string TimeZone { get; set; }

        public bool Enabled { get; set; }

        public bool AllowParallelRuns { get; set; }

        public string ConcurrencyKey { get; set; }

        public int? MaxRuntimeMinutes { get; set; }

        public string NextRunUtc { get; set; }

        public string NextRunLocal { get; set; }

        public string CustomAlertMessage { get; set; }
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
        private readonly MonitoringOptions _options;
        private readonly IAlertNotifier _notifier;
        private readonly ILogger<ExecutionMonitor> _logger;
        private readonly ILoggerFactory _loggerFactory;

        private readonly ConcurrentQueue<ExecutionEvent> _events = new();
        private readonly ConcurrentDictionary<string, int> _consecutiveFailures = new();
        private readonly object _scheduleLock = new();
        private List<ScheduledCommandView> _scheduleSnapshot = new();

        public ExecutionMonitor(IOptions<MonitoringOptions> options, ILogger<ExecutionMonitor> logger, ILoggerFactory loggerFactory)
        {
            _options = options.Value;
            _logger = logger;
            _loggerFactory = loggerFactory;
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
        }

        public void UpdateScheduleSnapshot(IEnumerable<ScheduledCommandView> snapshot)
        {
            lock(_scheduleLock)
            {
                _scheduleSnapshot = snapshot?.ToList() ?? new List<ScheduledCommandView>();
            }
        }

        public void Record(ExecutionEvent ev)
        {
            ev.DurationMs = (int)(ev.EndUtc - ev.StartUtc).TotalMilliseconds;
            _events.Enqueue(ev);
            while(_events.Count > 5000 && _events.TryDequeue(out _))
            {
            } // cap memory

            // update consecutive failures
            if(ev.SkippedDueToConflict)
                return;
            if(ev.Success)
            {
                _consecutiveFailures[ev.CommandId] = 0;
            } else
            {
                var n = _consecutiveFailures.AddOrUpdate(ev.CommandId, 1, (_, v) => v + 1);
                if(_options.AlertOn.EmailOnFail)
                    FireAlert("Failure", ev, n);
                if(_options.AlertOn.EmailOnConsecutiveFailures && n >= _options.AlertOn.ConsecutiveFailures)
                    FireAlert($"Consecutive failures ({n})", ev, n);
            }

            // slow run?
            if(ev.DurationMs >= _options.AlertOn.SlowRunMs && _options.AlertOn.SlowRunMs > 0)
            {
                FireAlert("Slow run", ev, _consecutiveFailures.GetValueOrDefault(ev.CommandId, 0));
            }
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
                string nextLocal = null;
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
                consecutiveFailures = _consecutiveFailures.ToDictionary(kv => kv.Key, kv => kv.Value),
                scheduled = scheduledForPayload,

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

        private HttpListener _listener;
        private string _dashboardPath;
        private FileSystemWatcher _htmlWatcher;

        public Monitoring(
            IConfiguration configuration,
            IOptions<MonitoringOptions> options,
            ExecutionMonitor monitor,
            ILogger<Monitoring> logger)
        {
            _configuration = configuration;
            _options = options;
            _monitor = monitor;
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
                var path = ctx.Request.Url.AbsolutePath;
                var pathLower = (path ?? string.Empty).ToLowerInvariant();
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
                } else if(pathLower == "/api/jobs" && ctx.Request.HttpMethod == "GET")
                {
                    var jobs = ReadJobsRaw();
                    WriteJson(ctx, new { ok = true, jobs }, 200);
                } else if((pathLower == "/api/jobs/validatecron" || pathLower == "/api/jobs/validatecron/" || pathLower == "/api/jobs/validatecron") && ctx.Request.HttpMethod == "POST"
                    || (path == "/api/jobs/validateCron" && ctx.Request.HttpMethod == "POST"))
                {
                    var body = ReadBody(ctx);
                    var dto = JsonSerializer.Deserialize<CronPreviewReq>(body);
                    ValidateCron(ctx, dto);
                } else if(pathLower == "/api/jobs" && ctx.Request.HttpMethod == "POST")
                {
                    if(!IsAuthorized(ctx))
                    {
                        ctx.Response.StatusCode = 401;
                        ctx.Response.Close();
                        return;
                    }
                    var body = ReadBody(ctx);
                    var job = JsonSerializer.Deserialize<Dictionary<string, object>>(body);
                    CreateJob(ctx, job);
                } else if(pathLower.StartsWith("/api/jobs/") &&
                    (ctx.Request.HttpMethod == "PUT" || ctx.Request.HttpMethod == "DELETE"))
                {
                    if(!IsAuthorized(ctx))
                    {
                        ctx.Response.StatusCode = 401;
                        ctx.Response.Close();
                        return;
                    }
                    var id = path.Split('/').Last();
                    if(ctx.Request.HttpMethod == "DELETE")
                    {
                        DeleteJob(ctx, id);
                    } else
                    {
                        var body = ReadBody(ctx);
                        var job = JsonSerializer.Deserialize<Dictionary<string, object>>(body);
                        UpdateJob(ctx, id, job);
                    }
                } else
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                }
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
                fs.Read(buf, 0, (int)read);
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
            var got = ctx.Request.Headers["X-Admin-Key"];
            return !string.IsNullOrWhiteSpace(expected) && string.Equals(expected, got, StringComparison.Ordinal);
        }

        private static string ConfigPath() => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

        private static List<Dictionary<string, object>> ReadJobsRaw()
        {
            var json = File.ReadAllText(ConfigPath(), Encoding.UTF8);
            using var doc = JsonDocument.Parse(json);
            if(!doc.RootElement.TryGetProperty("ScheduledCommands", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return new();
            var list = new List<Dictionary<string, object>>();
            foreach(var el in arr.EnumerateArray())
                list.Add(JsonSerializer.Deserialize<Dictionary<string, object>>(el.GetRawText()));
            return list;
        }

        private static void WriteJobsRaw(List<Dictionary<string, object>> list)
        {
            var cfgPath = ConfigPath();
            var json = File.ReadAllText(cfgPath, Encoding.UTF8);
            var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
            dict["ScheduledCommands"] = list;
            var newJson = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });

            var tmp = cfgPath + ".tmp";
            var bak = cfgPath + ".bak";
            File.WriteAllText(tmp, newJson, Encoding.UTF8);
            if(File.Exists(bak))
                File.Delete(bak);
            File.Replace(tmp, cfgPath, bak);
        }

        private void CreateJob(HttpListenerContext ctx, Dictionary<string, object> job)
        {
            if(job == null)
            {
                WriteJson(ctx, new { ok = false, error = "Invalid body" }, 400);
                return;
            }

            bool Missing(string k) => !job.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(Convert.ToString(v));

            if(Missing("Id") || Missing("Command") || Missing("CronExpression"))
            {
                WriteJson(ctx, new { ok = false, error = "Id, Command, CronExpression are required" }, 400);
                return;
            }

            try
            {
                CronExpression.Parse(Convert.ToString(job["CronExpression"]));
            } catch(Exception ex)
            {
                WriteJson(ctx, new { ok = false, error = $"Invalid cron: {ex.Message}" }, 400);
                return;
            }

            if(job.TryGetValue("TimeZone", out var tz) && !string.IsNullOrWhiteSpace(Convert.ToString(tz)))
            {
                try
                {
                    _ = TimeZoneInfo.FindSystemTimeZoneById(Convert.ToString(tz));
                } catch
                {
                    WriteJson(ctx, new { ok = false, error = "Invalid TimeZone" }, 400);
                    return;
                }
            }

            var list = ReadJobsRaw();
            var id = Convert.ToString(job["Id"]);
            if(list.Any(
                x => string.Equals(Convert.ToString(x.GetValueOrDefault("Id")), id, StringComparison.OrdinalIgnoreCase)))
            {
                WriteJson(ctx, new { ok = false, error = "Id already exists" }, 409);
                return;
            }

            list.Add(job);
            WriteJobsRaw(list);
            WriteJson(ctx, new { ok = true }, 200);
        }

        private void UpdateJob(HttpListenerContext ctx, string id, Dictionary<string, object> incoming)
        {
            var list = ReadJobsRaw();
            var idx = list.FindIndex(
                x => string.Equals(Convert.ToString(x.GetValueOrDefault("Id")), id, StringComparison.OrdinalIgnoreCase));
            if(idx < 0)
            {
                WriteJson(ctx, new { ok = false, error = "Not found" }, 404);
                return;
            }

            incoming ??= new();
            incoming["Id"] = id;

            if(incoming.TryGetValue("CronExpression", out var cron) &&
                !string.IsNullOrWhiteSpace(Convert.ToString(cron)))
            {
                try
                {
                    CronExpression.Parse(Convert.ToString(cron));
                } catch(Exception ex)
                {
                    WriteJson(ctx, new { ok = false, error = $"Invalid cron: {ex.Message}" }, 400);
                    return;
                }
            }

            list[idx] = incoming;
            WriteJobsRaw(list);
            WriteJson(ctx, new { ok = true }, 200);
        }

        private void DeleteJob(HttpListenerContext ctx, string id)
        {
            var list = ReadJobsRaw();
            var newList = list.Where(
                x => !string.Equals(Convert.ToString(x.GetValueOrDefault("Id")), id, StringComparison.OrdinalIgnoreCase))
                .ToList();
            WriteJobsRaw(newList);
            WriteJson(ctx, new { ok = true }, 200);
        }

        private static string ReadBody(HttpListenerContext ctx)
        {
            using var sr = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8);
            return sr.ReadToEnd();
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
        public static string replaceInsensitive(this string s, string find, string replaceWith) => s?.Replace(
                find,
                replaceWith,
                StringComparison.OrdinalIgnoreCase) ??
            s;
    }

    public static class DictionaryExtensions
    {
        public static TValue GetValueOrDefault<TKey, TValue>(
            this IDictionary<TKey, TValue> d,
            TKey key,
            TValue fallback = default) => d != null && d.TryGetValue(key, out var v) ? v : fallback;
    }
    #endregion
}

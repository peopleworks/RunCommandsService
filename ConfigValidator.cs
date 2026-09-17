using System;
using System.Collections.Generic;
using System.Text;
using Cronos;
using Microsoft.Extensions.Configuration;

namespace RunCommandsService
{
    /// <summary>
    /// Validates the scheduler configuration (ScheduledCommands) without executing anything.
    /// Backs the <c>--validate</c> CLI mode so a bad cron/timezone/command can be caught
    /// before the service is deployed (and in CI). Reuses the same <see cref="ScheduledCommand"/>
    /// model, Cronos parsing and <see cref="TimeZoneHelper"/> checks the runtime scheduler uses.
    /// </summary>
    public static class ConfigValidator
    {
        public class JobValidationResult
        {
            public string Id { get; set; }
            public bool IsValid { get; set; }
            public List<string> Problems { get; } = new List<string>();
        }

        public class ValidationReport
        {
            public List<JobValidationResult> Jobs { get; } = new List<JobValidationResult>();
            public List<string> ConfigurationProblems { get; } = new List<string>();
            public List<string> SecurityWarnings { get; } = new List<string>();
            public int TotalJobs => Jobs.Count;
            public int ValidJobs { get; set; }
            public int InvalidJobs { get; set; }
            public bool AllValid => InvalidJobs == 0 && ConfigurationProblems.Count == 0 && SecurityWarnings.Count == 0;
        }

        /// <summary>Validate the ScheduledCommands section and security options of an <see cref="IConfiguration"/>.</summary>
        public static ValidationReport Validate(IConfiguration configuration)
        {
            var commands = configuration.GetSection("ScheduledCommands").Get<List<ScheduledCommand>>()
                           ?? new List<ScheduledCommand>();
            var defaultTimeZone = configuration.GetSection("Scheduler").Get<SchedulerOptions>()?.DefaultTimeZone
                                  ?? "UTC";

            var report = Validate(commands, defaultTimeZone);

            var scheduler = configuration.GetSection("Scheduler").Get<SchedulerOptions>() ?? new SchedulerOptions();
            if (scheduler.PollSeconds <= 0)
                report.ConfigurationProblems.Add("Scheduler:PollSeconds must be greater than zero.");
            if (scheduler.MaxParallelism <= 0)
                report.ConfigurationProblems.Add("Scheduler:MaxParallelism must be greater than zero.");
            if (!TimeZoneHelper.IsValidTimeZone(defaultTimeZone, out var defaultTimeZoneError))
                report.ConfigurationProblems.Add($"Scheduler:DefaultTimeZone is invalid — {defaultTimeZoneError}");

            var monitoring = configuration.GetSection("Monitoring").Get<MonitoringOptions>() ?? new MonitoringOptions();
            if (monitoring.EnableHttpEndpoint)
            {
                if (monitoring.HttpPrefixes == null || monitoring.HttpPrefixes.Count == 0)
                {
                    report.ConfigurationProblems.Add("Monitoring:HttpPrefixes must contain at least one prefix when the HTTP endpoint is enabled.");
                }
                else
                {
                    foreach (var prefix in monitoring.HttpPrefixes)
                    {
                        if (!TryValidateHttpPrefix(prefix, out var prefixError))
                            report.ConfigurationProblems.Add($"Monitoring:HttpPrefixes contains an invalid prefix '{prefix}' — {prefixError}");
                        else if (IsRemotelyExposedHttpPrefix(prefix))
                            report.SecurityWarnings.Add($"Monitoring HTTP prefix '{prefix}' may expose the administrative API without transport encryption. Prefer loopback or HTTPS behind a reverse proxy.");
                    }
                }

                if (monitoring.MaxRequestBodyBytes < 1024 || monitoring.MaxRequestBodyBytes > 1024 * 1024)
                    report.ConfigurationProblems.Add("Monitoring:MaxRequestBodyBytes must be between 1024 and 1048576 bytes.");
            }

            if (monitoring.Dashboard.Enabled && monitoring.Dashboard.AutoRefreshSeconds <= 0)
                report.ConfigurationProblems.Add("Monitoring:Dashboard:AutoRefreshSeconds must be greater than zero.");

            // Security checks for default secrets
            var enableHttp = configuration.GetValue<bool>("Monitoring:EnableHttpEndpoint");
            var adminKey = configuration["Monitoring:AdminKey"];
            if (enableHttp && SecretMasker.IsDefaultSecret(adminKey))
            {
                report.SecurityWarnings.Add("Monitoring:AdminKey is using a default or empty value. Set a strong random key before deploying.");
            }

            var emailEnabled = configuration.GetValue<bool>("Monitoring:Notifiers:Email:Enabled");
            var emailPassword = configuration["Monitoring:Notifiers:Email:Password"];
            if (emailEnabled && SecretMasker.IsDefaultSecret(emailPassword))
            {
                report.SecurityWarnings.Add("Monitoring:Notifiers:Email:Password contains a default placeholder value.");
            }

            var webhookEnabled = configuration.GetValue<bool>("Monitoring:Notifiers:Webhook:Enabled");
            var webhookUrl = configuration["Monitoring:Notifiers:Webhook:Url"];
            if (webhookEnabled && (SecretMasker.IsDefaultSecret(webhookUrl) || (webhookUrl != null && webhookUrl.Contains("example.com"))))
            {
                report.SecurityWarnings.Add("Monitoring:Notifiers:Webhook:Url is using an unconfigured example URL.");
            }
            else if (webhookEnabled && (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var webhookUri) ||
                                        (webhookUri.Scheme != Uri.UriSchemeHttp && webhookUri.Scheme != Uri.UriSchemeHttps)))
            {
                report.ConfigurationProblems.Add("Monitoring:Notifiers:Webhook:Url must be an absolute HTTP or HTTPS URL.");
            }

            return report;
        }

        /// <summary>Validate a list of jobs against a default time zone (used when a job omits one).</summary>
        public static ValidationReport Validate(List<ScheduledCommand> commands, string defaultTimeZone)
        {
            var report = new ValidationReport();
            commands ??= new List<ScheduledCommand>();

            var duplicateIds = commands
                .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Id))
                .GroupBy(c => c.Id.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var c in commands)
            {
                if (c == null)
                {
                    report.InvalidJobs++;
                    var nullJob = new JobValidationResult { Id = "(null job)", IsValid = false };
                    nullJob.Problems.Add("job entry must be a JSON object");
                    report.Jobs.Add(nullJob);
                    continue;
                }

                var jr = new JobValidationResult
                {
                    Id = string.IsNullOrWhiteSpace(c.Id) ? "(no id)" : c.Id
                };

                if (string.IsNullOrWhiteSpace(c.Id))
                    jr.Problems.Add("missing Id");
                else if (duplicateIds.Contains(c.Id.Trim()))
                    jr.Problems.Add("duplicate Id (job IDs are case-insensitive)");

                if (string.IsNullOrWhiteSpace(c.Command))
                    jr.Problems.Add("missing Command");

                if (c.MaxRuntimeMinutes.HasValue && c.MaxRuntimeMinutes.Value <= 0)
                    jr.Problems.Add("MaxRuntimeMinutes must be greater than zero when specified");

                if (c.MaxOutputKB <= 0 || c.MaxOutputKB > 102400)
                    jr.Problems.Add("MaxOutputKB must be between 1 and 102400");

                ValidateRetryOptions(c.Retry, jr.Problems);

                if (string.IsNullOrWhiteSpace(c.CronExpression))
                {
                    jr.Problems.Add("missing CronExpression");
                }
                else
                {
                    try
                    {
                        CronExpression.Parse(c.CronExpression);
                    }
                    catch (Exception ex)
                    {
                        jr.Problems.Add($"invalid CronExpression — {ex.Message}");
                    }
                }

                // An empty TimeZone falls back to the scheduler default at runtime, so validate that.
                var tz = string.IsNullOrWhiteSpace(c.TimeZone) ? defaultTimeZone : c.TimeZone;
                if (!TimeZoneHelper.IsValidTimeZone(tz, out var tzError))
                    jr.Problems.Add($"invalid TimeZone — {tzError}");

                jr.IsValid = jr.Problems.Count == 0;
                if (jr.IsValid) report.ValidJobs++; else report.InvalidJobs++;
                report.Jobs.Add(jr);
            }

            return report;
        }

        /// <summary>Render a human-readable report (OK / per-problem detail per job).</summary>
        public static string FormatReport(ValidationReport report)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Configuration validation report");
            sb.AppendLine("===============================");

            if (report.SecurityWarnings.Count > 0)
            {
                sb.AppendLine("Security Warnings:");
                foreach (var w in report.SecurityWarnings)
                    sb.AppendLine($"  [SECURITY RISK] {w}");
                sb.AppendLine("-------------------------------");
            }

            if (report.ConfigurationProblems.Count > 0)
            {
                sb.AppendLine("Configuration Problems:");
                foreach (var problem in report.ConfigurationProblems)
                    sb.AppendLine($"  [FAIL] {problem}");
                sb.AppendLine("-------------------------------");
            }

            if (report.TotalJobs == 0)
                sb.AppendLine("No jobs found in ScheduledCommands.");

            foreach (var j in report.Jobs)
            {
                if (j.IsValid)
                {
                    sb.AppendLine($"  [OK]   {j.Id}");
                }
                else
                {
                    sb.AppendLine($"  [FAIL] {j.Id}");
                    foreach (var p in j.Problems)
                        sb.AppendLine($"           - {p}");
                }
            }

            sb.AppendLine("-------------------------------");
            sb.AppendLine($"{report.TotalJobs} job(s): {report.ValidJobs} valid, {report.InvalidJobs} invalid.");
            return sb.ToString();
        }

        private static bool TryValidateHttpPrefix(string? prefix, out string error)
        {
            if (string.IsNullOrWhiteSpace(prefix))
            {
                error = "prefix is empty";
                return false;
            }

            var trimmed = prefix.Trim();
            if (!trimmed.EndsWith('/'))
            {
                error = "HttpListener prefixes must end with '/'";
                return false;
            }

            // HttpListener accepts '+' and '*' host wildcards, while System.Uri does not
            // consistently parse them. Substitute localhost only for structural validation.
            var parseable = trimmed
                .Replace("://+:", "://localhost:", StringComparison.Ordinal)
                .Replace("://*:", "://localhost:", StringComparison.Ordinal);

            if (!Uri.TryCreate(parseable, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                string.IsNullOrWhiteSpace(uri.Host) ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment))
            {
                error = "expected an absolute http(s) prefix without query or fragment";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static void ValidateRetryOptions(RetryOptions? retry, List<string> problems)
        {
            if (retry == null)
            {
                problems.Add("Retry must be a JSON object when specified");
                return;
            }

            if (retry.MaxAttempts < 1 || retry.MaxAttempts > 10)
                problems.Add("Retry:MaxAttempts must be between 1 and 10");
            if (retry.InitialDelaySeconds < 0 || retry.InitialDelaySeconds > 3600)
                problems.Add("Retry:InitialDelaySeconds must be between 0 and 3600");
            if (retry.BackoffMultiplier < 1 || retry.BackoffMultiplier > 10 ||
                double.IsNaN(retry.BackoffMultiplier) || double.IsInfinity(retry.BackoffMultiplier))
                problems.Add("Retry:BackoffMultiplier must be between 1 and 10");
            if (retry.MaxDelaySeconds < retry.InitialDelaySeconds || retry.MaxDelaySeconds > 86400)
                problems.Add("Retry:MaxDelaySeconds must be at least InitialDelaySeconds and no greater than 86400");
            if (retry.JitterPercent < 0 || retry.JitterPercent > 100)
                problems.Add("Retry:JitterPercent must be between 0 and 100");
            if (retry.RetryableExitCodes == null)
                problems.Add("Retry:RetryableExitCodes must be an array when specified");
        }

        private static bool IsRemotelyExposedHttpPrefix(string prefix)
        {
            var trimmed = prefix.Trim();
            if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return false;

            if (trimmed.Contains("://+:", StringComparison.Ordinal) ||
                trimmed.Contains("://*:", StringComparison.Ordinal))
                return true;

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
                return false;

            return !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(uri.Host, "[::1]", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase);
        }
    }
}

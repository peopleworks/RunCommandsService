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
            public int TotalJobs => Jobs.Count;
            public int ValidJobs { get; set; }
            public int InvalidJobs { get; set; }
            public bool AllValid => InvalidJobs == 0;
        }

        /// <summary>Validate the ScheduledCommands section of an <see cref="IConfiguration"/>.</summary>
        public static ValidationReport Validate(IConfiguration configuration)
        {
            var commands = configuration.GetSection("ScheduledCommands").Get<List<ScheduledCommand>>()
                           ?? new List<ScheduledCommand>();
            var defaultTimeZone = configuration.GetSection("Scheduler").Get<SchedulerOptions>()?.DefaultTimeZone
                                  ?? "UTC";
            return Validate(commands, defaultTimeZone);
        }

        /// <summary>Validate a list of jobs against a default time zone (used when a job omits one).</summary>
        public static ValidationReport Validate(List<ScheduledCommand> commands, string defaultTimeZone)
        {
            var report = new ValidationReport();

            foreach (var c in commands ?? new List<ScheduledCommand>())
            {
                var jr = new JobValidationResult
                {
                    Id = string.IsNullOrWhiteSpace(c.Id) ? "(no id)" : c.Id
                };

                if (string.IsNullOrWhiteSpace(c.Id))
                    jr.Problems.Add("missing Id");

                if (string.IsNullOrWhiteSpace(c.Command))
                    jr.Problems.Add("missing Command");

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
    }
}

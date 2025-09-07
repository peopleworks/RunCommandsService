using System;

namespace RunCommandsService
{
    public class SchedulerOptions
    {
        /// <summary>How often the scheduler loop checks for due jobs (seconds).</summary>
        public int PollSeconds { get; set; } = 5;

        /// <summary>Default Windows time zone id for jobs that don't specify one (e.g., "Eastern Standard Time").</summary>
        public string DefaultTimeZone { get; set; } = "UTC";

        /// <summary>Max number of commands the service will run in parallel.</summary>
        public int MaxParallelism { get; set; } = 1;
    }
}

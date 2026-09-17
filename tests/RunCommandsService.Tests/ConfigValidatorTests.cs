using Microsoft.Extensions.Configuration;
using RunCommandsService;

namespace RunCommandsService.Tests;

public class ConfigValidatorTests
{
    [Fact]
    public void Validate_RejectsDuplicateIds_IgnoringCaseAndWhitespace()
    {
        var jobs = new List<ScheduledCommand>
        {
            ValidJob("Nightly"),
            ValidJob(" nightly ")
        };

        var report = ConfigValidator.Validate(jobs, "UTC");

        Assert.Equal(2, report.InvalidJobs);
        Assert.All(report.Jobs, job => Assert.Contains(job.Problems, p => p.Contains("duplicate Id")));
    }

    [Fact]
    public void Validate_RejectsInvalidPerJobLimits()
    {
        var job = ValidJob("limits");
        job.MaxRuntimeMinutes = 0;
        job.MaxOutputKB = 0;

        var result = ConfigValidator.Validate(new List<ScheduledCommand> { job }, "UTC").Jobs.Single();

        Assert.Contains(result.Problems, p => p.Contains("MaxRuntimeMinutes"));
        Assert.Contains(result.Problems, p => p.Contains("MaxOutputKB"));
    }

    [Fact]
    public void Validate_RejectsInvalidSchedulerAndHttpRanges()
    {
        var values = new Dictionary<string, string?>
        {
            ["Scheduler:PollSeconds"] = "0",
            ["Scheduler:MaxParallelism"] = "-1",
            ["Scheduler:DefaultTimeZone"] = "UTC",
            ["Monitoring:EnableHttpEndpoint"] = "true",
            ["Monitoring:AdminKey"] = "a-strong-test-key",
            ["Monitoring:HttpPrefixes:0"] = "http://localhost:5058",
            ["Monitoring:MaxRequestBodyBytes"] = "100",
            ["Monitoring:Dashboard:Enabled"] = "true",
            ["Monitoring:Dashboard:AutoRefreshSeconds"] = "0"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var report = ConfigValidator.Validate(config);

        Assert.Contains(report.ConfigurationProblems, p => p.Contains("PollSeconds"));
        Assert.Contains(report.ConfigurationProblems, p => p.Contains("MaxParallelism"));
        Assert.Contains(report.ConfigurationProblems, p => p.Contains("must end with"));
        Assert.Contains(report.ConfigurationProblems, p => p.Contains("MaxRequestBodyBytes"));
        Assert.Contains(report.ConfigurationProblems, p => p.Contains("AutoRefreshSeconds"));
    }

    [Fact]
    public void Validate_WarnsWhenPlainHttpPrefixIsRemotelyExposed()
    {
        var values = new Dictionary<string, string?>
        {
            ["Scheduler:PollSeconds"] = "5",
            ["Scheduler:MaxParallelism"] = "1",
            ["Scheduler:DefaultTimeZone"] = "UTC",
            ["Monitoring:EnableHttpEndpoint"] = "true",
            ["Monitoring:AdminKey"] = "a-strong-test-key",
            ["Monitoring:HttpPrefixes:0"] = "http://+:5058/",
            ["Monitoring:MaxRequestBodyBytes"] = "65536",
            ["Monitoring:Dashboard:Enabled"] = "false"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var report = ConfigValidator.Validate(config);

        Assert.DoesNotContain(report.ConfigurationProblems, p => p.Contains("HttpPrefixes"));
        Assert.Contains(report.SecurityWarnings, p => p.Contains("transport encryption"));
    }

    private static ScheduledCommand ValidJob(string id) => new()
    {
        Id = id,
        Command = "cmd /c ver",
        CronExpression = "*/5 * * * *",
        TimeZone = "UTC",
        MaxRuntimeMinutes = 1,
        MaxOutputKB = 512
    };
}

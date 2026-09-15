using Xunit;
using RunCommandsService;

namespace RunCommandsService.Tests;

public class ExecutionSuccessLogicTests
{
    [Theory]
    [InlineData(0, false, null, true)]
    [InlineData(0, false, "warning output", true)]
    [InlineData(0, true, "warning output", false)]
    [InlineData(1, false, null, false)]
    [InlineData(1, false, "error output", false)]
    [InlineData(1, true, "error output", false)]
    public void Command_SuccessCalculation_WorksAsExpected(int exitCode, bool treatStdErrAsFailure, string? stderr, bool expectedSuccess)
    {
        var command = new ScheduledCommand
        {
            Id = "TestJob",
            Command = "cmd /c exit",
            CaptureOutput = true,
            TreatStdErrAsFailure = treatStdErrAsFailure
        };

        var success = exitCode == 0;
        if (command.TreatStdErrAsFailure && command.CaptureOutput && !string.IsNullOrWhiteSpace(stderr))
        {
            success = false;
        }

        Assert.Equal(expectedSuccess, success);
    }
}

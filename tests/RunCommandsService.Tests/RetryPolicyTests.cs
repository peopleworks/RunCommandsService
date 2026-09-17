using RunCommandsService;

namespace RunCommandsService.Tests;

public class RetryPolicyTests
{
    [Fact]
    public void ShouldRetry_AllowsConfiguredExitCodeBeforeLastAttempt()
    {
        var options = Options();
        options.RetryableExitCodes = new List<int> { 7 };

        Assert.True(RetryPolicy.ShouldRetry(options, 1, false, 7, RetryFailureKind.ExitCode));
        Assert.False(RetryPolicy.ShouldRetry(options, 1, false, 8, RetryFailureKind.ExitCode));
        Assert.False(RetryPolicy.ShouldRetry(options, 3, false, 7, RetryFailureKind.ExitCode));
    }

    [Fact]
    public void ShouldRetry_EmptyExitCodeListMatchesEveryFailedExitResult()
    {
        var options = Options();

        Assert.True(RetryPolicy.ShouldRetry(options, 1, false, 23, RetryFailureKind.ExitCode));
        Assert.True(RetryPolicy.ShouldRetry(options, 1, false, 0, RetryFailureKind.ExitCode));
        Assert.False(RetryPolicy.ShouldRetry(options, 1, true, 0, RetryFailureKind.ExitCode));
        Assert.False(RetryPolicy.ShouldRetry(new RetryOptions(), 1, false, 23, RetryFailureKind.ExitCode));
        Assert.False(RetryPolicy.ShouldRetry(options, 1, false, null, RetryFailureKind.Timeout));
        Assert.False(RetryPolicy.ShouldRetry(options, 1, false, null, RetryFailureKind.Exception));
    }

    [Theory]
    [InlineData(RetryFailureKind.Timeout, true, false)]
    [InlineData(RetryFailureKind.Exception, false, true)]
    [InlineData(RetryFailureKind.Shutdown, true, true)]
    public void ShouldRetry_RespectsFailureFlags(
        RetryFailureKind kind,
        bool retryOnTimeout,
        bool retryOnException)
    {
        var options = Options();
        options.RetryOnTimeout = retryOnTimeout;
        options.RetryOnException = retryOnException;

        var expected = kind != RetryFailureKind.Shutdown &&
                       (kind != RetryFailureKind.Timeout || retryOnTimeout) &&
                       (kind != RetryFailureKind.Exception || retryOnException);
        Assert.Equal(expected, RetryPolicy.ShouldRetry(options, 1, false, null, kind));
    }

    [Theory]
    [InlineData(1, 0.5, 10)]
    [InlineData(2, 0.5, 20)]
    [InlineData(3, 0.5, 40)]
    [InlineData(4, 1.0, 60)]
    [InlineData(1, 0.0, 8)]
    [InlineData(1, 1.0, 12)]
    public void CalculateDelay_AppliesExponentialCapAndSymmetricJitter(
        int completedAttempt,
        double jitterSample,
        double expectedSeconds)
    {
        var options = Options();
        options.MaxDelaySeconds = 60;

        var delay = RetryPolicy.CalculateDelay(options, completedAttempt, jitterSample);

        Assert.Equal(expectedSeconds, delay.TotalSeconds, precision: 6);
    }

    [Fact]
    public void Normalize_ClampsUnsafeRuntimeValues()
    {
        var normalized = RetryPolicy.Normalize(new RetryOptions
        {
            MaxAttempts = 0,
            InitialDelaySeconds = -5,
            BackoffMultiplier = double.NaN,
            MaxDelaySeconds = -1,
            JitterPercent = 200,
            RetryableExitCodes = null!
        });

        Assert.Equal(1, normalized.MaxAttempts);
        Assert.Equal(0, normalized.InitialDelaySeconds);
        Assert.Equal(2, normalized.BackoffMultiplier);
        Assert.Equal(0, normalized.MaxDelaySeconds);
        Assert.Equal(100, normalized.JitterPercent);
        Assert.Empty(normalized.RetryableExitCodes);
    }

    private static RetryOptions Options() => new()
    {
        MaxAttempts = 3,
        InitialDelaySeconds = 10,
        BackoffMultiplier = 2,
        MaxDelaySeconds = 300,
        JitterPercent = 20
    };
}

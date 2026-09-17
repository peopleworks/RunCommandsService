namespace RunCommandsService;

public sealed class RetryOptions
{
    /// <summary>Total attempts, including the initial execution. One disables retries.</summary>
    public int MaxAttempts { get; set; } = 1;

    /// <summary>Delay before the first retry.</summary>
    public int InitialDelaySeconds { get; set; } = 10;

    /// <summary>Multiplier applied after each failed attempt.</summary>
    public double BackoffMultiplier { get; set; } = 2.0;

    /// <summary>Upper bound for an individual retry delay.</summary>
    public int MaxDelaySeconds { get; set; } = 300;

    /// <summary>Symmetric random variation applied to each delay (0-100).</summary>
    public int JitterPercent { get; set; } = 20;

    /// <summary>
    /// Exit codes eligible for retry. An empty list retries every unsuccessful
    /// exit-code result, including an exit code of zero rejected by TreatStdErrAsFailure.
    /// </summary>
    public List<int> RetryableExitCodes { get; set; } = new();

    public bool RetryOnTimeout { get; set; }

    public bool RetryOnException { get; set; }
}

public enum RetryFailureKind
{
    ExitCode,
    Timeout,
    Exception,
    Shutdown
}

public static class RetryPolicy
{
    public static RetryOptions Normalize(RetryOptions? options)
    {
        options ??= new RetryOptions();
        var initialDelay = Math.Clamp(options.InitialDelaySeconds, 0, 3600);
        var multiplier = double.IsFinite(options.BackoffMultiplier)
            ? Math.Clamp(options.BackoffMultiplier, 1, 10)
            : 2.0;

        return new RetryOptions
        {
            MaxAttempts = Math.Clamp(options.MaxAttempts, 1, 10),
            InitialDelaySeconds = initialDelay,
            BackoffMultiplier = multiplier,
            MaxDelaySeconds = Math.Clamp(options.MaxDelaySeconds, initialDelay, 86400),
            JitterPercent = Math.Clamp(options.JitterPercent, 0, 100),
            RetryableExitCodes = options.RetryableExitCodes?.Distinct().ToList() ?? new List<int>(),
            RetryOnTimeout = options.RetryOnTimeout,
            RetryOnException = options.RetryOnException
        };
    }

    public static bool ShouldRetry(
        RetryOptions options,
        int completedAttempt,
        bool success,
        int? exitCode,
        RetryFailureKind failureKind)
    {
        ArgumentNullException.ThrowIfNull(options);
        options = Normalize(options);

        if (success || completedAttempt >= options.MaxAttempts || failureKind == RetryFailureKind.Shutdown)
            return false;

        return failureKind switch
        {
            RetryFailureKind.Timeout => options.RetryOnTimeout,
            RetryFailureKind.Exception => options.RetryOnException,
            RetryFailureKind.ExitCode => options.RetryableExitCodes.Count == 0 ||
                                         (exitCode.HasValue && options.RetryableExitCodes.Contains(exitCode.Value)),
            _ => false
        };
    }

    public static TimeSpan CalculateDelay(RetryOptions options, int completedAttempt, double jitterSample)
    {
        ArgumentNullException.ThrowIfNull(options);
        options = Normalize(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(completedAttempt, 1);
        if (jitterSample is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(jitterSample), "Jitter sample must be between 0 and 1.");

        var exponential = options.InitialDelaySeconds *
                          Math.Pow(options.BackoffMultiplier, completedAttempt - 1);
        var capped = Math.Min(exponential, options.MaxDelaySeconds);
        var jitterRatio = options.JitterPercent / 100.0;
        var jitterFactor = 1 + ((jitterSample * 2) - 1) * jitterRatio;
        var jittered = Math.Max(0, capped * jitterFactor);
        return TimeSpan.FromSeconds(Math.Min(jittered, options.MaxDelaySeconds));
    }
}

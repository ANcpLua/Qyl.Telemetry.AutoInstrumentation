namespace Qyl.Telemetry.Integration.Tests;

/// <summary>What a running child process has shown so far.</summary>
readonly record struct ProcessObservation(bool HasExited, string Stdout, string Stderr)
{
    public static ProcessObservation Of(RunningProcess process) => new(process.HasExited, process.Stdout, process.Stderr);
}

/// <summary>Waits on an injected clock until a running process prints a token, and fails with the last observed output.</summary>
static class OutputWait
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    public static async Task ForTokenAsync(Func<ProcessObservation> observe, string token, TimeProvider timeProvider, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = timeProvider.GetUtcNow() + timeout;
        while (true)
        {
            var observed = observe();
            if (observed.Stdout.Contains(token, StringComparison.Ordinal))
                return;
            if (observed.HasExited)
                throw new InvalidOperationException($"the process exited before it printed '{token}'\nstdout={observed.Stdout}\nstderr={observed.Stderr}");
            if (timeProvider.GetUtcNow() >= deadline)
                throw new TimeoutException($"the process did not print '{token}' within {timeout}\nstdout={observed.Stdout}\nstderr={observed.Stderr}");
            await Task.Delay(PollInterval, timeProvider, cancellationToken);
        }
    }
}

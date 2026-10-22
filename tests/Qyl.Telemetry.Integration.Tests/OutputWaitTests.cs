using Microsoft.Extensions.Time.Testing;

namespace Qyl.Telemetry.Integration.Tests;

[TestClass]
public sealed class OutputWaitTests
{
    private const string ReadyToken = "downstream-listening=";
    private const string StartingOutput = "starting";
    private const string FailureOutput = "bind failed";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [TestMethod]
    public void OutputWait_TokenAlreadyPrinted_CompletesWithoutWaiting()
    {
        var clock = new FakeTimeProvider();

        var wait = OutputWait.ForTokenAsync(static () => new ProcessObservation(false, ReadyToken + "5000", string.Empty), ReadyToken, clock, Timeout,
            CancellationToken.None);

        Assert.IsTrue(wait.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task OutputWait_TokenPrintedAfterAPoll_CompletesBeforeTheDeadline()
    {
        var clock = new FakeTimeProvider();
        var stdout = StartingOutput;
        var wait = OutputWait.ForTokenAsync(() => new ProcessObservation(false, stdout, string.Empty), ReadyToken, clock, Timeout, CancellationToken.None);

        stdout = ReadyToken;
        clock.Advance(OutputWait.PollInterval);
        await wait;

        Assert.IsTrue(wait.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task OutputWait_DeadlinePassesWithoutToken_ThrowsWithTheLastOutput()
    {
        var clock = new FakeTimeProvider();
        var wait = OutputWait.ForTokenAsync(static () => new ProcessObservation(false, StartingOutput, FailureOutput), ReadyToken, clock, Timeout,
            CancellationToken.None);

        clock.Advance(Timeout + OutputWait.PollInterval);
        var failure = await Assert.ThrowsExactlyAsync<TimeoutException>(() => wait);

        Assert.Contains($"stdout={StartingOutput}\nstderr={FailureOutput}", failure.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task OutputWait_ProcessExitsWithoutToken_ThrowsWithTheLastOutput()
    {
        var clock = new FakeTimeProvider();

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => OutputWait.ForTokenAsync(
            static () => new ProcessObservation(true, StartingOutput, FailureOutput), ReadyToken, clock, Timeout, CancellationToken.None));

        Assert.Contains($"stdout={StartingOutput}\nstderr={FailureOutput}", failure.Message, StringComparison.Ordinal);
    }
}

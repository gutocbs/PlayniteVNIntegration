using VNInstallation.Protocol.Contracts;
using VNInstallation.Protocol.Transport;
using VnManager.PlayniteInstallation.Transport;

namespace VnManager.PlayniteInstallation.Tests;

public sealed class PreparationOutcomeWaiterTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PreparationOutcomeWaiterTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Returns_the_correlated_outcome_once_the_orchestrator_publishes_it()
    {
        var store = new AtomicTransportFileStore(new TransportPaths(root));
        var request = Request();
        var waiter = new PreparationOutcomeWaiter(new OrchestratorDispatchService(store, new Launcher()), store, TimeSpan.FromMilliseconds(5));

        var waiting = waiter.WaitAsync(request, TestContext.Current.CancellationToken);
        await Task.Delay(30, TestContext.Current.CancellationToken);
        store.WritePreparationOutcome(new PreparationOutcome(1, request.RequestId, Guid.NewGuid(), request.PlayniteGameId, PreparationOutcomeKind.Prepared, null, DateTimeOffset.UtcNow));
        var result = await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Activation.OrchestratorActivated);
        Assert.False(result.IsCancelled);
        Assert.Equal(PreparationOutcomeKind.Prepared, result.Outcome!.Outcome);
        Assert.Null(store.ConsumePreparationOutcome(request.RequestId, request.PlayniteGameId));
    }

    [Fact]
    public async Task Cancellation_stops_waiting_without_an_outcome()
    {
        var store = new AtomicTransportFileStore(new TransportPaths(root));
        using var cancellation = new CancellationTokenSource();
        var waiter = new PreparationOutcomeWaiter(new OrchestratorDispatchService(store, new Launcher()), store, TimeSpan.FromMilliseconds(5));

        var waiting = waiter.WaitAsync(Request(), cancellation.Token);
        cancellation.Cancel();
        var result = await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.IsCancelled);
        Assert.Null(result.Outcome);
    }

    [Fact]
    public async Task Activation_failure_returns_without_waiting_for_an_outcome()
    {
        var store = new AtomicTransportFileStore(new TransportPaths(root));
        var waiter = new PreparationOutcomeWaiter(new OrchestratorDispatchService(store, new Launcher(fail: true)), store, TimeSpan.FromMilliseconds(5));

        var result = await waiter.WaitAsync(Request(), TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(result.Activation.OrchestratorActivated);
        Assert.False(result.IsCancelled);
        Assert.False(result.IsSuperseded);
        Assert.Null(result.Outcome);
    }

    [Fact]
    public async Task Superseded_marker_ends_preparation_wait_without_dispatching_another_cancel()
    {
        var store = new AtomicTransportFileStore(new TransportPaths(root));
        var request = Request();
        var waiter = new PreparationOutcomeWaiter(new OrchestratorDispatchService(store, new Launcher()), store, TimeSpan.FromMilliseconds(5));
        store.MarkSuperseded(request.RequestId);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var result = await waiter.WaitAsync(request, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(result.IsCancelled);
        Assert.True(result.IsSuperseded);
        Assert.Null(result.Outcome);
        Assert.Null(store.ReadInstallationCancellationCommand(request.RequestId));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private static InstallationRequest Request() => new(1, Guid.NewGuid(), Guid.NewGuid(), "Game", @"K:\Game.rar", "fingerprint", DateTimeOffset.UtcNow);

    private sealed class Launcher(bool fail = false) : IOrchestratorLauncher
    {
        public Task ActivateAsync(CancellationToken cancellationToken) =>
            fail ? Task.FromException(new InvalidOperationException("not running")) : Task.CompletedTask;
    }
}

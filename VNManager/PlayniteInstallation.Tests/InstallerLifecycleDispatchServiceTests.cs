using VNInstallation.Protocol.Contracts;
using VNInstallation.Protocol.Transport;
using VnManager.PlayniteInstallation.Domain;
using VnManager.PlayniteInstallation.Transport;

namespace VnManager.PlayniteInstallation.Tests;

public sealed class InstallerLifecycleDispatchServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "VNManagerInstallerLifecycleTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Launch_and_cancellation_commands_are_correlated_and_wake_the_orchestrator()
    {
        var store = new AtomicTransportFileStore(root);
        var launcher = new RecordingLauncher();
        var service = new OrchestratorDispatchService(store, launcher);
        var prepared = new PreparationOutcome(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), PreparationOutcomeKind.Prepared, null, DateTimeOffset.UtcNow);

        await service.DispatchInstallerLaunchAsync(prepared, TestContext.Current.CancellationToken);
        await service.DispatchCancellationAsync(prepared.RequestId, prepared.PlayniteGameId, TestContext.Current.CancellationToken);

        Assert.Equal(2, launcher.Activations);
        Assert.Equal(prepared.RequestId, store.ReadInstallerLaunchCommand(prepared.RequestId)!.RequestId);
        Assert.Equal(prepared.WorkflowId, store.ReadInstallerLaunchCommand(prepared.RequestId)!.WorkflowId);
        Assert.Equal(prepared.PlayniteGameId, store.ReadInstallationCancellationCommand(prepared.RequestId)!.PlayniteGameId);
    }

    [Fact]
    public async Task Cancellation_request_is_published_before_the_local_cancel_callback()
    {
        var registry = new PendingInstallCompletionRegistry();
        var gameId = Guid.NewGuid();
        var dispatchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var localCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(registry.TryRegister(
            Guid.NewGuid(),
            gameId,
            _ => { },
            () => localCancellation.SetResult(),
            async _ =>
            {
                dispatchStarted.SetResult();
                await releaseDispatch.Task;
            }));

        Assert.True(registry.TryRequestCancellation(gameId));
        await dispatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(localCancellation.Task.IsCompleted);

        releaseDispatch.SetResult();
        await localCancellation.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(registry.HasActiveGame(gameId));
        Assert.False(registry.TryRequestCancellation(gameId));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class RecordingLauncher : IOrchestratorLauncher
    {
        public int Activations { get; private set; }

        public Task ActivateAsync(CancellationToken cancellationToken)
        {
            Activations++;
            return Task.CompletedTask;
        }
    }
}

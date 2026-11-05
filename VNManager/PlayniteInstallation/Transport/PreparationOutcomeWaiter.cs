using VNInstallation.Protocol.Contracts;
using VNInstallation.Protocol.Transport;

namespace VnManager.PlayniteInstallation.Transport;

public sealed record PreparationWaitResult(DispatchResult Activation, PreparationOutcome? Outcome, bool IsCancelled, bool IsSuperseded);

/// <summary>Starts the Orchestrator and waits off the UI thread until it publishes the request's preparation outcome.</summary>
public sealed class PreparationOutcomeWaiter(OrchestratorDispatchService dispatch, AtomicTransportFileStore store, TimeSpan pollInterval)
{
    public async Task<PreparationWaitResult> WaitAsync(InstallationRequest request, CancellationToken cancellationToken)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        var activation = await dispatch.ActivateAsync(CancellationToken.None).ConfigureAwait(false);
        if (!activation.OrchestratorActivated) return new PreparationWaitResult(activation, null, false, false);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (store.IsSuperseded(request.RequestId)) return new PreparationWaitResult(activation, null, false, true);
            var outcome = store.ConsumePreparationOutcome(request.RequestId, request.PlayniteGameId);
            if (outcome is not null) return new PreparationWaitResult(activation, outcome, false, false);
            try
            {
                await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
        return new PreparationWaitResult(activation, null, true, false);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using VNInstallation.Protocol.Transport;
using VNInstallation.Protocol.Contracts;
using VnManager.PlayniteInstallation.Domain;
using VnManager.PlayniteInstallation.Transport;

namespace VnManager.PlaynitePlugin.Installation;

internal sealed class PluginInstallWorkflowCoordinator : IDisposable
{
    private readonly IPlayniteAPI api;
    private readonly AtomicTransportFileStore store;
    private readonly OrchestratorDispatchService dispatch;
    private readonly PreparationOutcomeWaiter preparationWaiter;
    private readonly PlayniteResultApplier resultApplier;
    private readonly InteractionRequestProcessor interactionProcessor;
    private readonly ILogger logger = LogManager.GetLogger(nameof(PluginInstallWorkflowCoordinator));
    private readonly IResultReadyUiDispatcher uiDispatcher;
    private readonly PendingInstallCompletionRegistry installCompletions = new();
    private readonly CancellationTokenSource retryCancellation = new();
    // Accessed only on the Playnite UI thread.
    private readonly Dictionary<Guid, CancellationTokenSource> pendingPreparations = new();
    private UiSignalSubscription resultReadySynchronization;
    private UiSignalSubscription interactionReadySynchronization;
    private UiSignalSubscription terminalOutcomeSynchronization;
    private int retryQueued;
    private bool disposed;

    public PluginInstallWorkflowCoordinator(IPlayniteAPI api, string transportRoot, string orchestratorPath)
    {
        this.api = api;
        store = new AtomicTransportFileStore(new TransportPaths(transportRoot));
        dispatch = new OrchestratorDispatchService(store, new OrchestratorProcessActivator(orchestratorPath));
        preparationWaiter = new PreparationOutcomeWaiter(dispatch, store, TimeSpan.FromMilliseconds(250));
        var wakeSignal = new NamedOrchestratorWakeSignal(OrchestratorWakeEvent.NameForCurrentUser());
        resultApplier = new PlayniteResultApplier(api, store, logger, wakeSignal, () => ScheduleResultSynchronization("lock-contention", TimeSpan.FromMilliseconds(250)), installCompletions.TryComplete);
        uiDispatcher = new PlayniteUiDispatcher(api);
        interactionProcessor = new InteractionRequestProcessor(
            store,
            new PlayniteInteractionDialog(api),
            new PlayniteInteractionValidator(api, store),
            wakeSignal);
    }

    public bool IsEligible(Game game) => InstallEligibility.Evaluate(PlayniteInstallAdapter.ToSnapshot(game), new FileProbe(), installCompletions).IsEligible;

    public void Dispatch(Game game, Action<string> completeInstall, Action cancelInstall)
    {
        var request = PlayniteInstallAdapter.CreateRequest(game);
        if (!installCompletions.TryRegister(
            request.RequestId,
            game.Id,
            completeInstall,
            () => CancelLocalInstallAsync(game.Id, cancelInstall),
            requestId => dispatch.DispatchCancellationAsync(requestId, game.Id))) return;
        try
        {
            dispatch.PersistRequest(request);
            uiDispatcher.BeginInvoke(() => MonitorPreparation(request));
        }
        catch (Exception)
        {
            CancelLiveInstall(request, publishCancellation: false);
            api.Notifications.Add("VNOrchestrator", "A solicitação não pôde ser preparada para extração.", NotificationType.Error);
        }
    }

    public void StartResultSynchronization()
    {
        if (resultReadySynchronization == null)
        {
            resultReadySynchronization = new UiSignalSubscription(TransportEventNames.ResultReadyNameForCurrentUser(), uiDispatcher, () => SynchronizeResults("signal"));
        }
        if (interactionReadySynchronization == null)
        {
            interactionReadySynchronization = new UiSignalSubscription(TransportEventNames.InteractionReadyNameForCurrentUser(), uiDispatcher, () => ProcessInteractions("signal"));
        }
        if (terminalOutcomeSynchronization == null)
        {
            terminalOutcomeSynchronization = new UiSignalSubscription(TransportEventNames.InstallationTerminalOutcomeNameForCurrentUser(), uiDispatcher, ApplyTerminalOutcomes);
        }
        ScheduleResultSynchronization("startup");
        ScheduleInteractionProcessing("startup");
        ApplyTerminalOutcomes();
    }

    public void ReconcilePendingResults() => resultApplier.ApplyPending();

    public bool HasPendingPreparation(Guid gameId) => pendingPreparations.ContainsKey(gameId);

    public void CancelPendingPreparation(Guid gameId)
    {
        installCompletions.TryRequestCancellation(gameId);
    }

    // Extraction can take minutes; a modal progress window would block Playnite for all of it.
    private void MonitorPreparation(InstallationRequest request)
    {
        var cancellation = new CancellationTokenSource();
        pendingPreparations[request.PlayniteGameId] = cancellation;
        preparationWaiter.WaitAsync(request, cancellation.Token).ContinueWith(
            waited => uiDispatcher.BeginInvoke(() => CompletePreparation(request, cancellation, waited)),
            TaskScheduler.Default);
        api.Dialogs.ShowMessage(
            "O jogo será extraído em segundo plano e você pode continuar usando o Playnite.\n\n"
            + "Assim que a extração terminar, o instalador começará. Para cancelar, use \"" + CancelPreparationMenuDescription + "\" no menu do jogo.",
            "VN Orchestrator",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    public const string CancelPreparationMenuDescription = "Cancelar instalação (VN Orchestrator)";

    private void CompletePreparation(InstallationRequest request, CancellationTokenSource cancellation, Task<PreparationWaitResult> waited)
    {
        if (pendingPreparations.TryGetValue(request.PlayniteGameId, out var current) && current == cancellation)
        {
            pendingPreparations.Remove(request.PlayniteGameId);
        }
        cancellation.Dispose();
        if (waited.IsFaulted)
        {
            logger.Error($"VN Orchestrator preparation monitoring failed. RequestId='{request.RequestId}', Exception='{waited.Exception}'.");
            CancelLiveInstall(request, publishCancellation: true);
            ShowFailure("Não foi possível acompanhar a extração do jogo.", null);
            return;
        }

        var result = waited.Result;
        if (!result.Activation.OrchestratorActivated)
        {
            CancelLiveInstall(request, publishCancellation: true);
            api.Notifications.Add("VNOrchestrator", "A solicitação foi salva, mas o VN Install Orchestrator não pôde ser iniciado.", NotificationType.Error);
            return;
        }
        if (result.IsCancelled)
        {
            CancelLiveInstall(request, publishCancellation: true);
            return;
        }
        if (result.IsSuperseded)
        {
            CancelLiveInstall(request, publishCancellation: false);
            return;
        }
        var outcome = result.Outcome;
        if (outcome is null || outcome.Outcome == PreparationOutcomeKind.Failed)
        {
            CancelLiveInstall(request, publishCancellation: false);
            ShowFailure("Não foi possível preparar os arquivos do jogo.", outcome?.FailureReason);
            return;
        }
        if (outcome.Outcome == PreparationOutcomeKind.PreparedWithoutInstallerLaunch)
        {
            _ = dispatch.DispatchPortableConfigurationAsync(outcome).ContinueWith(completed =>
            {
                if (completed.IsFaulted || !completed.Result.OrchestratorActivated)
                {
                    CancelLiveInstall(request, publishCancellation: true);
                    api.Notifications.Add("VNOrchestrator", "A configuração portátil não pôde ser iniciada.", NotificationType.Error);
                }
            });
            return;
        }
        _ = dispatch.DispatchInstallerLaunchAsync(outcome).ContinueWith(completed =>
        {
            if (completed.IsFaulted || !completed.Result.OrchestratorActivated)
            {
                CancelLiveInstall(request, publishCancellation: true);
                api.Notifications.Add("VNOrchestrator", "O instalador não pôde ser iniciado.", NotificationType.Error);
            }
        });
    }

    private void CancelLiveInstall(InstallationRequest request, bool publishCancellation)
    {
        if (publishCancellation)
        {
            installCompletions.TryRequestCancellation(request.PlayniteGameId);
            return;
        }
        installCompletions.TryCancel(request.RequestId, request.PlayniteGameId);
    }

    private void CancelLocalInstall(Guid gameId, Action cancelInstall)
    {
        if (pendingPreparations.TryGetValue(gameId, out var cancellation)) cancellation.Cancel();
        cancelInstall();
    }

    private Task CancelLocalInstallAsync(Guid gameId, Action cancelInstall)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            uiDispatcher.BeginInvoke(() =>
            {
                try
                {
                    CancelLocalInstall(gameId, cancelInstall);
                    completion.SetResult(true);
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            });
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
        }
        return completion.Task;
    }

    private void ApplyTerminalOutcomes()
    {
        foreach (var outcome in store.ReadInstallationTerminalOutcomes())
        {
            if (store.ConsumeInstallationTerminalOutcome(outcome.RequestId, outcome.WorkflowId, outcome.PlayniteGameId) is null) continue;
            if (installCompletions.TryCancel(outcome.RequestId, outcome.PlayniteGameId))
            {
                ShowFailure("A instalação não foi concluída.", outcome.FailureReason);
            }
        }
    }

    // A notification is easy to miss; the Orchestrator's reason usually tells the user what to fix.
    private void ShowFailure(string summary, string reason)
    {
        var message = InstallationFailureMessage.Format(summary, reason);
        logger.Warn($"VN Orchestrator installation failed. Message='{message}'.");
        api.Dialogs.ShowErrorMessage(message, "VN Orchestrator");
    }

    private void SynchronizeResults(string trigger)
    {
        try
        {
            logger.Info($"VN Orchestrator result synchronization started from {trigger}.");
            ReconcilePendingResults();
        }
        catch (Exception exception)
        {
            logger.Error($"VN Orchestrator result synchronization failed after {trigger}. Exception='{exception}'.");
        }
    }

    private void ProcessInteractions(string trigger)
    {
        try
        {
            logger.Info($"VN Orchestrator interaction processing started from {trigger}.");
            var summary = interactionProcessor.ProcessPending();
            LogRejectedInteractions(summary);
            logger.Info($"VN Orchestrator interaction processing completed from {trigger}. Processed='{summary.Processed}'.");
        }
        catch (Exception exception)
        {
            logger.Error($"VN Orchestrator interaction processing failed after {trigger}. Exception='{exception}'.");
        }
    }

    private void ScheduleInteractionProcessing(string trigger)
    {
        if (disposed) return;
        try { uiDispatcher.BeginInvoke(new Action(() => ProcessInteractions(trigger))); }
        catch (Exception exception) when (disposed)
        {
            logger.Warn($"VN Orchestrator interaction processing was skipped during shutdown. Exception='{exception}'.");
        }
    }

    private void ScheduleResultSynchronization(string trigger, TimeSpan? delay = null)
    {
        if (disposed) return;
        if (delay is { } retryDelay)
        {
            if (Interlocked.Exchange(ref retryQueued, 1) != 0) return;
            Task.Delay(retryDelay, retryCancellation.Token).ContinueWith(completed =>
            {
                if (completed.IsCanceled || disposed) return;
                ScheduleResultSynchronization(trigger);
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
            return;
        }
        Interlocked.Exchange(ref retryQueued, 0);
        try { uiDispatcher.BeginInvoke(new Action(() => SynchronizeResults(trigger))); }
        catch (Exception exception) when (disposed)
        {
            logger.Warn($"VN Orchestrator result synchronization was skipped during shutdown. Exception='{exception}'.");
        }
    }

    private void LogRejectedInteractions(InteractionProcessingSummary summary)
    {
        foreach (var rejection in summary.Rejected)
        {
            logger.Warn($"VN Orchestrator interaction was rejected. InteractionId='{rejection.InteractionId}', RequestId='{rejection.RequestId}', WorkflowId='{rejection.WorkflowId}', GameId='{rejection.PlayniteGameId}', ErrorCode='{rejection.ErrorCode}', Reason='{rejection.Reason}'.");
        }
    }

    public void Dispose()
    {
        disposed = true;
        foreach (var cancellation in pendingPreparations.Values) cancellation.Cancel();
        retryCancellation.Cancel();
        resultReadySynchronization?.Dispose();
        resultReadySynchronization = null;
        interactionReadySynchronization?.Dispose();
        interactionReadySynchronization = null;
        terminalOutcomeSynchronization?.Dispose();
        terminalOutcomeSynchronization = null;
        retryCancellation.Dispose();
    }

    private sealed class FileProbe : IFileProbe { public bool Exists(string path) => File.Exists(path); }
    private sealed class PlayniteUiDispatcher(IPlayniteAPI api) : IResultReadyUiDispatcher
    {
        public void BeginInvoke(Action action) => api.MainView.UIDispatcher.BeginInvoke(action);
    }
}

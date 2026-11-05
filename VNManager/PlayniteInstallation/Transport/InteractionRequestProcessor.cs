using VNInstallation.Protocol.Contracts;
using VNInstallation.Protocol.Serialization;
using VNInstallation.Protocol.Transport;
using VnManager.PlayniteInstallation.Domain;

namespace VnManager.PlayniteInstallation.Transport;

public interface IInteractionDialog
{
    InteractionDialogResult Show(PlayniteInteractionRequest request);
}

public interface IInteractionRequestValidator
{
    InteractionValidationResult Validate(PlayniteInteractionRequest request);
}

public sealed record InteractionValidationResult(bool CanHandle, ProtocolErrorCode ErrorCode, string? Reason);

public sealed record RejectedInteraction(
    Guid InteractionId,
    Guid RequestId,
    Guid WorkflowId,
    Guid PlayniteGameId,
    ProtocolErrorCode ErrorCode,
    string? Reason);

public sealed record InteractionProcessingSummary(int Processed, IReadOnlyList<RejectedInteraction> Rejected);

public sealed class InteractionRequestProcessor(
    AtomicTransportFileStore store,
    IInteractionDialog dialog,
    IInteractionRequestValidator validator,
    IOrchestratorWakeSignal orchestratorWakeSignal)
{
    private readonly object processingLock = new();

    public InteractionProcessingSummary ProcessPending()
    {
        lock (processingLock)
        {
            var answered = new HashSet<Guid>(store.ReadInteractionResponses().Select(response => response.InteractionId));
            var rejected = new List<RejectedInteraction>();
            var processed = 0;
            foreach (var request in store.ReadInteractionRequests())
            {
                if (answered.Contains(request.InteractionId)) continue;

                var validation = !IsSupported(request.Kind)
                    ? new InteractionValidationResult(
                        false,
                        ProtocolErrorCode.UnsupportedInteractionKind,
                        $"Interaction kind '{(int)request.Kind}' is not supported.")
                    : request.Kind == InteractionKind.SelectPortableDestination && request.PortableDestination == null
                        ? new InteractionValidationResult(
                            false,
                            ProtocolErrorCode.MissingRequiredField,
                            "The portable destination prompt is missing.")
                        : validator.Validate(request);
                if (!validation.CanHandle)
                {
                    rejected.Add(new RejectedInteraction(
                        request.InteractionId,
                        request.RequestId,
                        request.WorkflowId,
                        request.PlayniteGameId,
                        validation.ErrorCode,
                        validation.Reason));
                    continue;
                }

                var result = dialog.Show(request);
                var response = new PlayniteInteractionResponse(
                    ProtocolV1.SchemaVersion,
                    request.InteractionId,
                    request.RequestId,
                    request.WorkflowId,
                    request.PlayniteGameId,
                    request.Kind,
                    result.Outcome,
                    request.Kind is InteractionKind.ConfigureInstallation or InteractionKind.SelectPortableDestination ? null : result.SelectedPath,
                    DateTimeOffset.UtcNow)
                {
                    Configuration = request.Kind == InteractionKind.ConfigureInstallation ? result.Configuration : null,
                    PortableDestination = request.Kind == InteractionKind.SelectPortableDestination ? result.PortableDestination : null
                };
                store.WriteInteractionResponse(response);
                answered.Add(request.InteractionId);
                processed++;
                orchestratorWakeSignal.Signal();
            }

            return new InteractionProcessingSummary(processed, rejected);
        }
    }

    private static bool IsSupported(InteractionKind kind) => kind is
        InteractionKind.SelectInstallerExecutable
        or InteractionKind.SelectInstallationDirectory
        or InteractionKind.SelectGameExecutable
        or InteractionKind.ConfirmDirectInstallerLaunch
        or InteractionKind.ConfigureInstallation
        or InteractionKind.SelectPortableDestination;
}

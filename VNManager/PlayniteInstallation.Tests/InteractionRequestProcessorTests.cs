using VNInstallation.Protocol.Contracts;
using VNInstallation.Protocol.Serialization;
using VNInstallation.Protocol.Transport;
using VnManager.PlayniteInstallation.Domain;
using VnManager.PlayniteInstallation.Transport;

namespace VnManager.PlayniteInstallation.Tests;

public sealed class InteractionRequestProcessorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "InteractionRequestProcessorTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void One_request_produces_one_dialog_and_one_response_across_reconciliations()
    {
        var store = Store();
        var request = Request();
        store.WriteInteractionRequest(request);
        var unexpectedConfiguration = new InstallationConfigurationSelection(@"D:\Ignored", @"D:\Ignored\game.exe", null);
        var dialog = new RecordingDialog(new InteractionDialogResult(InteractionOutcome.Selected, @"K:\ゲーム\game.exe", unexpectedConfiguration));
        var wake = new RecordingWakeSignal();
        var processor = new InteractionRequestProcessor(store, dialog, AcceptingValidator(), wake);

        var first = processor.ProcessPending();
        var second = processor.ProcessPending();

        Assert.Equal(1, first.Processed);
        Assert.Empty(first.Rejected);
        Assert.Equal(0, second.Processed);
        Assert.Equal(1, dialog.CallCount);
        var response = Assert.Single(store.ReadInteractionResponses());
        Assert.Equal(request.InteractionId, response.InteractionId);
        Assert.Equal(@"K:\ゲーム\game.exe", response.SelectedPath);
        Assert.Null(response.Configuration);
        Assert.Equal(1, wake.CallCount);
    }

    [Fact]
    public void Existing_response_suppresses_the_dialog_and_cancel_is_persisted()
    {
        var store = Store();
        var answered = Request();
        store.WriteInteractionRequest(answered);
        store.WriteInteractionResponse(Response(answered, InteractionOutcome.Selected));
        var cancelled = Request();
        store.WriteInteractionRequest(cancelled);
        var dialog = new RecordingDialog(new InteractionDialogResult(InteractionOutcome.Cancelled, null, null));

        new InteractionRequestProcessor(store, dialog, AcceptingValidator(), new RecordingWakeSignal()).ProcessPending();

        Assert.Equal(1, dialog.CallCount);
        Assert.Contains(store.ReadInteractionResponses(), response => response.InteractionId == cancelled.InteractionId && response.Outcome == InteractionOutcome.Cancelled);
    }

    [Fact]
    public void Restart_reprocesses_an_unanswered_request_but_two_startup_passes_show_it_once()
    {
        var store = Store();
        store.WriteInteractionRequest(Request());
        new InteractionRequestProcessor(store, new RecordingDialog(new InteractionDialogResult(InteractionOutcome.Selected, "ignored", null)), RejectingValidator(), new RecordingWakeSignal()).ProcessPending();
        var dialogAfterRestart = new RecordingDialog(new InteractionDialogResult(InteractionOutcome.Selected, @"K:\game.exe", null));
        var restarted = new InteractionRequestProcessor(store, dialogAfterRestart, AcceptingValidator(), new RecordingWakeSignal());

        restarted.ProcessPending();
        restarted.ProcessPending();

        Assert.Equal(1, dialogAfterRestart.CallCount);
        Assert.Single(store.ReadInteractionResponses());
    }

    [Fact]
    public void Invalid_or_superseded_requests_are_rejected_while_a_valid_replacement_is_processed()
    {
        var store = Store();
        var superseded = Request();
        var mismatched = Request();
        var replacement = Request();
        store.WriteInteractionRequest(superseded);
        store.WriteInteractionRequest(mismatched);
        store.WriteInteractionRequest(replacement);
        var dialog = new RecordingDialog(new InteractionDialogResult(InteractionOutcome.Confirmed, null, null));

        var summary = new InteractionRequestProcessor(
            store,
            dialog,
            new PredicateValidator(request => request.InteractionId == replacement.InteractionId
                ? Accepted()
                : new InteractionValidationResult(false, ProtocolErrorCode.SourceFingerprintMismatch, "Request is stale.")),
            new RecordingWakeSignal()).ProcessPending();

        Assert.Equal(1, summary.Processed);
        Assert.Equal(2, summary.Rejected.Count);
        Assert.Equal(1, dialog.CallCount);
        Assert.Equal(replacement.InteractionId, Assert.Single(store.ReadInteractionResponses()).InteractionId);
    }

    [Fact]
    public void Configure_installation_writes_one_structured_response_and_never_uses_selected_path()
    {
        var store = Store();
        var request = Request(InteractionKind.ConfigureInstallation) with
        {
            Configuration = new InstallationConfigurationPrompt(
                InstallationFlowKind.Portable,
                @"D:\Jogos\星の記憶",
                @"D:\Jogos\星の記憶\星.exe",
                new[] { @"D:\Jogos\星の記憶\星.exe" })
        };
        store.WriteInteractionRequest(request);
        var configuration = new InstallationConfigurationSelection(
            @"D:\Jogos\星の記憶",
            @"D:\Jogos\星の記憶\星.exe",
            @"F:\Traduções\追加ファイル");
        var processor = new InteractionRequestProcessor(
            store,
            new RecordingDialog(new InteractionDialogResult(InteractionOutcome.Confirmed, @"D:\must-not-be-used.exe", configuration)),
            AcceptingValidator(),
            new RecordingWakeSignal());

        processor.ProcessPending();
        processor.ProcessPending();

        var response = Assert.Single(store.ReadInteractionResponses());
        Assert.Equal(request.InteractionId, response.InteractionId);
        Assert.Equal(InteractionOutcome.Confirmed, response.Outcome);
        Assert.Null(response.SelectedPath);
        Assert.Equal(configuration, response.Configuration);
    }

    [Fact]
    public void Cancelled_configuration_writes_a_cancelled_response_without_paths()
    {
        var store = Store();
        var request = Request(InteractionKind.ConfigureInstallation);
        store.WriteInteractionRequest(request);

        new InteractionRequestProcessor(
            store,
            new RecordingDialog(new InteractionDialogResult(InteractionOutcome.Cancelled, null, null)),
            AcceptingValidator(),
            new RecordingWakeSignal()).ProcessPending();

        var response = Assert.Single(store.ReadInteractionResponses());
        Assert.Equal(InteractionOutcome.Cancelled, response.Outcome);
        Assert.Null(response.SelectedPath);
        Assert.Null(response.Configuration);
    }

    [Fact]
    public void Cancelled_portable_destination_has_no_partial_selection()
    {
        var store = Store();
        var request = Request(InteractionKind.SelectPortableDestination) with
        {
            PortableDestination = new PortableDestinationPrompt(@"D:\Jogos\星の記憶")
        };
        store.WriteInteractionRequest(request);

        new InteractionRequestProcessor(
            store,
            new RecordingDialog(new InteractionDialogResult(InteractionOutcome.Cancelled, @"D:\must-not-be-used", null)),
            AcceptingValidator(),
            new RecordingWakeSignal()).ProcessPending();

        var response = Assert.Single(store.ReadInteractionResponses());
        Assert.Equal(InteractionOutcome.Cancelled, response.Outcome);
        Assert.Null(response.SelectedPath);
        Assert.Null(response.Configuration);
        Assert.Null(response.PortableDestination);
    }

    [Fact]
    public void Unsupported_missing_portable_destination_prompt_is_rejected_before_showing_a_dialog()
    {
        var store = Store();
        var request = Request(InteractionKind.SelectPortableDestination);
        store.WriteInteractionRequest(request);
        var dialog = new RecordingDialog(new InteractionDialogResult(InteractionOutcome.Confirmed, null, null));

        var summary = new InteractionRequestProcessor(store, dialog, AcceptingValidator(), new RecordingWakeSignal()).ProcessPending();

        Assert.Equal(0, summary.Processed);
        Assert.Empty(summary.Rejected);
        Assert.Equal(0, dialog.CallCount);
        Assert.Empty(store.ReadInteractionResponses());
    }

    [Fact]
    public void Portable_destination_response_uses_structured_selection_not_selected_path()
    {
        var store = Store();
        var request = Request(InteractionKind.SelectPortableDestination) with
        {
            PortableDestination = new PortableDestinationPrompt(@"D:\Jogos\星の記憶")
        };
        store.WriteInteractionRequest(request);
        var selection = new PortableDestinationSelection(@"D:\Jogos\星の記憶", true);

        new InteractionRequestProcessor(
            store,
            new RecordingDialog(new InteractionDialogResult(InteractionOutcome.Confirmed, @"D:\must-not-be-used", new InstallationConfigurationSelection(@"D:\ignored", @"D:\ignored\game.exe", null), selection)),
            AcceptingValidator(),
            new RecordingWakeSignal()).ProcessPending();

        var response = Assert.Single(store.ReadInteractionResponses());
        Assert.Equal(InteractionOutcome.Confirmed, response.Outcome);
        Assert.Null(response.SelectedPath);
        Assert.Null(response.Configuration);
        Assert.Equal(selection, response.PortableDestination);
    }

    [Fact]
    public void Unknown_interaction_kind_is_rejected_without_showing_a_dialog_or_writing_a_response()
    {
        var store = Store();
        var request = Request((InteractionKind)999);
        store.WriteInteractionRequest(request);
        var dialog = new RecordingDialog(new InteractionDialogResult(InteractionOutcome.Confirmed, null, null));

        var summary = new InteractionRequestProcessor(store, dialog, AcceptingValidator(), new RecordingWakeSignal()).ProcessPending();

        Assert.Equal(0, summary.Processed);
        var rejection = Assert.Single(summary.Rejected);
        Assert.Equal(request.InteractionId, rejection.InteractionId);
        Assert.Equal(request.RequestId, rejection.RequestId);
        Assert.Equal(request.WorkflowId, rejection.WorkflowId);
        Assert.Equal(request.PlayniteGameId, rejection.PlayniteGameId);
        Assert.Equal(ProtocolErrorCode.UnsupportedInteractionKind, rejection.ErrorCode);
        Assert.NotNull(rejection.Reason);
        Assert.Equal(0, dialog.CallCount);
        Assert.Empty(store.ReadInteractionResponses());
    }

    private AtomicTransportFileStore Store() => new(new TransportPaths(root));
    private static PlayniteInteractionRequest Request(InteractionKind kind = InteractionKind.SelectGameExecutable) => new(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), @"K:\game.rar", "fingerprint", kind, "Caption", "Purpose", @"K:\ゲーム", "Executable files|*.exe", DateTimeOffset.UtcNow);
    private static PlayniteInteractionResponse Response(PlayniteInteractionRequest request, InteractionOutcome outcome) => new(1, request.InteractionId, request.RequestId, request.WorkflowId, request.PlayniteGameId, request.Kind, outcome, null, DateTimeOffset.UtcNow);
    private static InteractionValidationResult Accepted() => new(true, ProtocolErrorCode.None, null);
    private static PredicateValidator AcceptingValidator() => new(_ => Accepted());
    private static PredicateValidator RejectingValidator() => new(_ => new InteractionValidationResult(false, ProtocolErrorCode.SourceFingerprintMismatch, "Request is stale."));

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private sealed class RecordingDialog(InteractionDialogResult result) : IInteractionDialog
    {
        public int CallCount { get; private set; }
        public InteractionDialogResult Show(PlayniteInteractionRequest request) { CallCount++; return result; }
    }

    private sealed class PredicateValidator(Func<PlayniteInteractionRequest, InteractionValidationResult> predicate) : IInteractionRequestValidator
    {
        public InteractionValidationResult Validate(PlayniteInteractionRequest request) => predicate(request);
    }

    private sealed class RecordingWakeSignal : IOrchestratorWakeSignal
    {
        public int CallCount { get; private set; }
        public void Signal() => CallCount++;
    }
}

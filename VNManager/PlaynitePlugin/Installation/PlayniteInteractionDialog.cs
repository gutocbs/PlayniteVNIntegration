using System;
using System.Windows;
using Playnite.SDK;
using VNInstallation.Protocol.Contracts;
using VnManager.PlayniteInstallation.Domain;
using VnManager.PlayniteInstallation.Transport;

namespace VnManager.PlaynitePlugin.Installation;

internal sealed class PlayniteInteractionDialog(IPlayniteAPI api) : IInteractionDialog
{
    public InteractionDialogResult Show(PlayniteInteractionRequest request) =>
        api.MainView.UIDispatcher.Invoke(() => ShowCore(request));

    private InteractionDialogResult ShowCore(PlayniteInteractionRequest request)
    {
        switch (request.Kind)
        {
            case InteractionKind.SelectInstallationDirectory:
                return SelectedOrCancelled(api.Dialogs.SelectFolder(request.InitialDirectory));
            case InteractionKind.SelectInstallerExecutable:
            case InteractionKind.SelectGameExecutable:
                return SelectedOrCancelled(api.Dialogs.SelectFile(request.FileFilter ?? InstallationDialogs.ExecutableFileFilter, request.InitialDirectory));
            case InteractionKind.ConfirmDirectInstallerLaunch:
                var answer = api.Dialogs.ShowMessage(request.Purpose, request.Caption, MessageBoxButton.YesNo);
                return new InteractionDialogResult(
                    answer == MessageBoxResult.Yes ? InteractionOutcome.Confirmed : InteractionOutcome.Declined,
                    null,
                    null);
            case InteractionKind.ConfigureInstallation:
                return new InstallationConfigurationDialog(api).Show(request);
            case InteractionKind.SelectPortableDestination:
                return new PortableDestinationDialog(api).Show(request);
            default:
                throw new ArgumentOutOfRangeException(nameof(request.Kind), request.Kind, "Unsupported interaction kind.");
        }
    }

    private static InteractionDialogResult SelectedOrCancelled(string selectedPath) =>
        string.IsNullOrWhiteSpace(selectedPath)
            ? new InteractionDialogResult(InteractionOutcome.Cancelled, null, null)
            : new InteractionDialogResult(InteractionOutcome.Selected, selectedPath, null);
}

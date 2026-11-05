using System;
using System.IO;
using System.Windows;
using Playnite.SDK;
using VNInstallation.Protocol.Contracts;
using VnManager.PlayniteInstallation.Domain;

namespace VnManager.PlaynitePlugin.Installation;

internal sealed class PortableDestinationDialog(IPlayniteAPI api)
{
    public InteractionDialogResult Show(PlayniteInteractionRequest request)
    {
        if (request.PortableDestination == null)
        {
            throw new ArgumentException("The portable destination prompt is required.", nameof(request));
        }

        var form = PortableDestinationForm.Create(request.PortableDestination);
        var probe = new PortableDestinationPathProbe();
        var view = new PortableDestinationView
        {
            Purpose = request.Purpose,
            DestinationDirectory = form.DestinationDirectory
        };
        var window = api.Dialogs.CreateWindow(new WindowCreationOptions
        {
            ShowCloseButton = true,
            ShowMaximizeButton = false,
            ShowMinimizeButton = false
        });
        window.Title = request.Caption;
        window.Content = view;
        window.Owner = api.Dialogs.GetCurrentAppWindow();
        window.SizeToContent = SizeToContent.WidthAndHeight;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;

        InteractionDialogResult result = null;
        view.BrowseDestinationRequested += (_, _) =>
        {
            var selected = api.Dialogs.SelectFolder(view.DestinationDirectory);
            if (!string.IsNullOrWhiteSpace(selected)) view.DestinationDirectory = selected;
        };
        view.ContinueRequested += (_, _) =>
        {
            if (!form.TrySelect(view.DestinationDirectory, probe, out var selection, out var validationError))
            {
                view.ValidationError = validationError ?? "Revise a pasta de destino.";
                return;
            }

            if (form.RequiresExistingContentMergeConfirmation)
            {
                var answer = api.Dialogs.ShowMessage(
                    "A pasta selecionada já contém arquivos. Arquivos com o mesmo nome podem ser substituídos. Deseja continuar?",
                    request.Caption,
                    MessageBoxButton.OKCancel);
                if (answer != MessageBoxResult.OK)
                {
                    result = Cancelled();
                    window.DialogResult = false;
                    return;
                }

                selection = selection with { AllowExistingContentMerge = true };
            }

            result = new InteractionDialogResult(InteractionOutcome.Confirmed, null, null, selection);
            window.DialogResult = true;
        };
        view.CancelRequested += (_, _) =>
        {
            result = Cancelled();
            window.DialogResult = false;
        };

        window.ShowDialog();
        return result ?? Cancelled();
    }

    private static InteractionDialogResult Cancelled() => new(InteractionOutcome.Cancelled, null, null);

    private sealed class PortableDestinationPathProbe : IPortableDestinationPathProbe
    {
        public PortableDestinationPathState Inspect(string path)
        {
            var canonicalPath = Path.GetFullPath(path);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(canonicalPath);
            }
            catch (FileNotFoundException)
            {
                return PortableDestinationPathState.Missing;
            }
            catch (DirectoryNotFoundException)
            {
                return PortableDestinationPathState.Missing;
            }

            if ((attributes & FileAttributes.Directory) == 0)
            {
                throw new IOException("The destination path is an existing file.");
            }

            using var entries = Directory.EnumerateFileSystemEntries(canonicalPath).GetEnumerator();
            return entries.MoveNext()
                ? PortableDestinationPathState.NonEmpty
                : PortableDestinationPathState.Empty;
        }
    }
}

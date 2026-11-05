using System.Security;
using VNInstallation.Protocol.Contracts;

namespace VnManager.PlayniteInstallation.Domain;

public enum PortableDestinationPathState
{
    Missing,
    Empty,
    NonEmpty
}

public interface IPortableDestinationPathProbe
{
    PortableDestinationPathState Inspect(string path);
}

public sealed class PortableDestinationForm
{
    private PortableDestinationForm(string destinationDirectory)
    {
        DestinationDirectory = destinationDirectory;
    }

    public string DestinationDirectory { get; }
    public bool RequiresExistingContentMergeConfirmation { get; private set; }

    public static PortableDestinationForm Create(PortableDestinationPrompt prompt)
    {
        if (prompt is null) throw new ArgumentNullException(nameof(prompt));

        return new PortableDestinationForm(prompt.SuggestedDirectory);
    }

    public bool TrySelect(
        string path,
        IPortableDestinationPathProbe probe,
        out PortableDestinationSelection? selection,
        out string? validationError)
    {
        selection = null;
        validationError = null;
        RequiresExistingContentMergeConfirmation = false;

        if (probe is null) throw new ArgumentNullException(nameof(probe));
        if (string.IsNullOrWhiteSpace(path))
        {
            validationError = "Selecione uma pasta de destino.";
            return false;
        }

        if (!TryGetFullPath(path, out var normalizedPath))
        {
            validationError = "A pasta de destino não é válida.";
            return false;
        }

        PortableDestinationPathState state;
        try
        {
            state = probe.Inspect(normalizedPath!);
        }
        catch (Exception exception) when (exception is ArgumentException
                                           || exception is IOException
                                           || exception is NotSupportedException
                                           || exception is PathTooLongException
                                           || exception is SecurityException
                                           || exception is UnauthorizedAccessException)
        {
            validationError = "A pasta de destino não pode ser acessada.";
            return false;
        }

        RequiresExistingContentMergeConfirmation = state == PortableDestinationPathState.NonEmpty;
        selection = new PortableDestinationSelection(normalizedPath!, false);
        return true;
    }

    private static bool TryGetFullPath(string path, out string? fullPath)
    {
        try
        {
            fullPath = Path.GetFullPath(path.Trim());
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
                                           || exception is NotSupportedException
                                           || exception is PathTooLongException
                                           || exception is SecurityException)
        {
            fullPath = null;
            return false;
        }
    }
}

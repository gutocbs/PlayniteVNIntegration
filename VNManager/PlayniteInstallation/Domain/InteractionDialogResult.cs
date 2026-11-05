using VNInstallation.Protocol.Contracts;

namespace VnManager.PlayniteInstallation.Domain;

public sealed record InteractionDialogResult(
    InteractionOutcome Outcome,
    string? SelectedPath,
    InstallationConfigurationSelection? Configuration,
    PortableDestinationSelection? PortableDestination = null);

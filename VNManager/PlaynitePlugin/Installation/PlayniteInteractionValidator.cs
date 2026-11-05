using System;
using System.IO;
using Playnite.SDK;
using VNInstallation.Protocol.Contracts;
using VNInstallation.Protocol.Serialization;
using VnManager.PlayniteInstallation.Transport;
using VNInstallation.Protocol.Transport;

namespace VnManager.PlaynitePlugin.Installation;

internal sealed class PlayniteInteractionValidator(IPlayniteAPI api, AtomicTransportFileStore store) : IInteractionRequestValidator
{
    public InteractionValidationResult Validate(PlayniteInteractionRequest request)
    {
        if (request.Kind == InteractionKind.ConfigureInstallation && request.Configuration == null)
        {
            return Reject(ProtocolErrorCode.MissingRequiredField, "The installation configuration prompt is missing.");
        }

        if (request.Kind == InteractionKind.SelectPortableDestination && request.PortableDestination == null)
        {
            return Reject(ProtocolErrorCode.MissingRequiredField, "The portable destination prompt is missing.");
        }

        if (store.IsSuperseded(request.RequestId)) return Reject(ProtocolErrorCode.None, "The installation request was superseded.");

        var game = api.Database.Games.Get(request.PlayniteGameId);
        if (game == null) return Reject(ProtocolErrorCode.MissingRequiredField, "The correlated Playnite game was not found.");
        if (string.IsNullOrWhiteSpace(game.InstallDirectory)) return Reject(ProtocolErrorCode.MissingRequiredField, "The source archive path is missing.");

        string currentSource;
        try { currentSource = Path.GetFullPath(game.InstallDirectory); }
        catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException || exception is PathTooLongException)
        {
            return Reject(ProtocolErrorCode.MissingRequiredField, "The source archive path is invalid.");
        }

        if (!string.Equals(currentSource, request.SourcePath, StringComparison.OrdinalIgnoreCase) || !File.Exists(currentSource))
        {
            return Reject(ProtocolErrorCode.SourceFingerprintMismatch, "The source archive no longer matches the request.");
        }
        try
        {
            var info = new FileInfo(currentSource);
            var currentFingerprint = ProtocolV1.CreateSourceFingerprint(currentSource, info.Length, info.LastWriteTimeUtc);
            return string.Equals(currentFingerprint, request.SourceFingerprint, StringComparison.Ordinal)
                ? new InteractionValidationResult(true, ProtocolErrorCode.None, null)
                : Reject(ProtocolErrorCode.SourceFingerprintMismatch, "The source archive fingerprint changed.");
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            return Reject(ProtocolErrorCode.SourceFingerprintMismatch, "The source archive could not be inspected.");
        }
    }

    private static InteractionValidationResult Reject(ProtocolErrorCode errorCode, string reason) => new(false, errorCode, reason);
}

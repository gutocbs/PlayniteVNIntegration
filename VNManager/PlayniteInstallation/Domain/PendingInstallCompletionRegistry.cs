namespace VnManager.PlayniteInstallation.Domain;

public sealed class PendingInstallCompletionRegistry : IActiveWorkflowLookup
{
    private readonly object sync = new();
    private readonly Dictionary<Guid, PendingCompletion> pending = new();
    private readonly Dictionary<Guid, Guid> requestIdsByGame = new();

    public bool TryRegister(
        Guid requestId,
        Guid gameId,
        Action<string> complete,
        Action? cancel = null,
        Func<Guid, Task>? requestCancellation = null)
    {
        lock (sync)
        {
            if (requestIdsByGame.ContainsKey(gameId)) return false;
            pending[requestId] = new PendingCompletion(gameId, complete, cancel, requestCancellation);
            requestIdsByGame[gameId] = requestId;
            return true;
        }
    }

    public bool HasActiveGame(Guid gameId)
    {
        lock (sync) return requestIdsByGame.ContainsKey(gameId);
    }

    public bool HasActiveWorkflow(Guid gameId) => HasActiveGame(gameId);

    public bool TryComplete(Guid requestId, Guid gameId, string installationDirectory)
    {
        PendingCompletion? pendingCompletion;
        lock (sync)
        {
            if (!pending.TryGetValue(requestId, out pendingCompletion) || pendingCompletion.GameId != gameId) return false;
            Remove(requestId, gameId);
        }
        pendingCompletion.Complete(installationDirectory);
        return true;
    }

    public bool TryCancel(Guid requestId, Guid gameId)
    {
        PendingCompletion? pendingCompletion;
        lock (sync)
        {
            if (!pending.TryGetValue(requestId, out pendingCompletion) || pendingCompletion.GameId != gameId) return false;
            Remove(requestId, gameId);
        }
        pendingCompletion.Cancel?.Invoke();
        return true;
    }

    public bool TryRequestCancellation(Guid gameId)
    {
        Guid requestId;
        PendingCompletion? pendingCompletion;
        lock (sync)
        {
            if (!requestIdsByGame.TryGetValue(gameId, out requestId) || !pending.TryGetValue(requestId, out pendingCompletion)) return false;
            Remove(requestId, gameId);
        }

        if (pendingCompletion.RequestCancellation is null)
        {
            pendingCompletion.Cancel?.Invoke();
        }
        else
        {
            _ = DispatchThenCancelAsync(() => pendingCompletion.RequestCancellation(requestId), pendingCompletion.Cancel);
        }
        return true;
    }

    private static async Task DispatchThenCancelAsync(Func<Task> dispatchCancellation, Action? cancel)
    {
        try
        {
            await dispatchCancellation().ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            cancel?.Invoke();
        }
    }

    private sealed record PendingCompletion(
        Guid GameId,
        Action<string> Complete,
        Action? Cancel,
        Func<Guid, Task>? RequestCancellation);

    private void Remove(Guid requestId, Guid gameId)
    {
        pending.Remove(requestId);
        requestIdsByGame.Remove(gameId);
    }
}

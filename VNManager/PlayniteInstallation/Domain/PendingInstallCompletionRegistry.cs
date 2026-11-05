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
            if (pendingCompletion.CancellationRequested) return false;
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
            if (pendingCompletion.CancellationRequested) return false;
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
            if (pendingCompletion.CancellationRequested) return false;
            pendingCompletion.CancellationRequested = true;
        }

        if (pendingCompletion.RequestCancellation is null)
        {
            CompleteCancellation(requestId, gameId, pendingCompletion);
        }
        else
        {
            _ = DispatchThenCancelAsync(
                () => pendingCompletion.RequestCancellation(requestId),
                () => CompleteCancellation(requestId, gameId, pendingCompletion));
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

    private void CompleteCancellation(Guid requestId, Guid gameId, PendingCompletion pendingCompletion)
    {
        try
        {
            pendingCompletion.Cancel?.Invoke();
        }
        finally
        {
            lock (sync)
            {
                if (pending.TryGetValue(requestId, out var current) && ReferenceEquals(current, pendingCompletion))
                {
                    Remove(requestId, gameId);
                }
            }
        }
    }

    private sealed class PendingCompletion(
        Guid gameId,
        Action<string> complete,
        Action? cancel,
        Func<Guid, Task>? requestCancellation)
    {
        public Guid GameId { get; } = gameId;
        public Action<string> Complete { get; } = complete;
        public Action? Cancel { get; } = cancel;
        public Func<Guid, Task>? RequestCancellation { get; } = requestCancellation;
        public bool CancellationRequested { get; set; }
    }

    private void Remove(Guid requestId, Guid gameId)
    {
        pending.Remove(requestId);
        requestIdsByGame.Remove(gameId);
    }
}

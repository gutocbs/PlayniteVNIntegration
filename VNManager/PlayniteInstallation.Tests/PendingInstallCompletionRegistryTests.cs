using VnManager.PlayniteInstallation.Domain;

namespace VnManager.PlayniteInstallation.Tests;

public sealed class PendingInstallCompletionRegistryTests
{
    [Fact]
    public void Second_registration_for_an_active_game_is_rejected()
    {
        var registry = new PendingInstallCompletionRegistry();
        var gameId = Guid.NewGuid();
        var firstRequestId = Guid.NewGuid();
        var secondRequestId = Guid.NewGuid();
        Assert.True(registry.TryRegister(firstRequestId, gameId, _ => { }));

        Assert.False(registry.TryRegister(secondRequestId, gameId, _ => { }));

        Assert.False(registry.TryComplete(secondRequestId, gameId, @"D:\\Library\\Game"));
        Assert.True(registry.TryComplete(firstRequestId, gameId, @"D:\\Library\\Game"));
    }

    [Fact]
    public void Different_game_ids_can_be_active_together()
    {
        var registry = new PendingInstallCompletionRegistry();
        var firstRequestId = Guid.NewGuid();
        var secondRequestId = Guid.NewGuid();
        var firstGameId = Guid.NewGuid();
        var secondGameId = Guid.NewGuid();
        Assert.True(registry.TryRegister(firstRequestId, firstGameId, _ => { }));
        Assert.True(registry.TryRegister(secondRequestId, secondGameId, _ => { }));

        Assert.True(registry.TryComplete(firstRequestId, firstGameId, @"D:\\Library\\First"));
        Assert.True(registry.TryComplete(secondRequestId, secondGameId, @"D:\\Library\\Second"));
    }

    [Fact]
    public void Complete_cancel_and_failure_release_the_game_for_a_new_registration()
    {
        var registry = new PendingInstallCompletionRegistry();
        var gameId = Guid.NewGuid();
        var completedRequestId = Guid.NewGuid();
        var cancelledRequestId = Guid.NewGuid();
        var failedRequestId = Guid.NewGuid();
        var replacementRequestId = Guid.NewGuid();
        Assert.True(registry.TryRegister(completedRequestId, gameId, _ => { }));
        Assert.True(registry.HasActiveGame(gameId));
        Assert.True(registry.TryComplete(completedRequestId, gameId, @"D:\\Library\\Completed"));
        Assert.False(registry.HasActiveGame(gameId));
        Assert.True(registry.TryRegister(cancelledRequestId, gameId, _ => { }));
        Assert.True(registry.TryCancel(cancelledRequestId, gameId));
        Assert.False(registry.HasActiveGame(gameId));
        Assert.True(registry.TryRegister(failedRequestId, gameId, _ => { }));
        Assert.True(registry.TryCancel(failedRequestId, gameId));
        Assert.False(registry.HasActiveGame(gameId));

        Assert.True(registry.TryRegister(replacementRequestId, gameId, _ => { }));

        Assert.True(registry.TryComplete(replacementRequestId, gameId, @"D:\\Library\\Replacement"));
    }

    [Fact]
    public void Completes_the_controller_registered_for_the_applied_game_once()
    {
        var registry = new PendingInstallCompletionRegistry();
        var gameId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        string? completedDirectory = null;
        Assert.True(registry.TryRegister(requestId, gameId, directory => completedDirectory = directory));

        var first = registry.TryComplete(requestId, gameId, @"D:\\Library\\Game");
        var second = registry.TryComplete(requestId, gameId, @"D:\\Library\\Other");

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(@"D:\\Library\\Game", completedDirectory);
    }
}

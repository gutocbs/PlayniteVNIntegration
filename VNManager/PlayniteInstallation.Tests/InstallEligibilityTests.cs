using VnManager.PlayniteInstallation.Domain;

namespace VnManager.PlayniteInstallation.Tests;

public sealed class InstallEligibilityTests
{
    [Fact]
    public void Evaluate_allows_an_uninstalled_visual_novel_with_an_existing_rar()
    {
        var result = InstallEligibility.Evaluate(Game(), new ExistingFileProbe(), new InactiveWorkflows());

        Assert.True(result.IsEligible);
    }

    [Fact]
    public void Install_eligibility_rejects_only_an_active_same_game()
    {
        var activeGameId = Guid.NewGuid();
        var otherGameId = Guid.NewGuid();
        var workflows = new PendingInstallCompletionRegistry();
        Assert.True(workflows.TryRegister(Guid.NewGuid(), activeGameId, _ => { }));

        var active = InstallEligibility.Evaluate(Game(gameId: activeGameId), new ExistingFileProbe(), workflows);
        var other = InstallEligibility.Evaluate(Game(gameId: otherGameId), new ExistingFileProbe(), workflows);

        Assert.False(active.IsEligible);
        Assert.Equal(InstallEligibilityRejection.WorkflowAlreadyActive, active.Rejection);
        Assert.True(other.IsEligible);
    }

    [Theory]
    [InlineData(true, @"K:\\Game.rar", "Visual Novel", false, InstallEligibilityRejection.Installed)]
    [InlineData(false, @"K:\\missing.rar", "Visual Novel", false, InstallEligibilityRejection.SourceMissing)]
    [InlineData(false, @"K:\\Game.zip", "Visual Novel", false, InstallEligibilityRejection.UnsupportedSource)]
    [InlineData(false, @"K:\\Game.RAR", "Action", false, InstallEligibilityRejection.NotVisualNovel)]
    [InlineData(false, @"K:\\Game.RAR", "Visual Novel", true, InstallEligibilityRejection.WorkflowAlreadyActive)]
    public void Evaluate_returns_a_specific_rejection(bool installed, string source, string genre, bool active, InstallEligibilityRejection expected)
    {
        var result = InstallEligibility.Evaluate(Game(installed, source, genre), new ExistingFileProbe(), new ActiveWorkflows(active));

        Assert.False(result.IsEligible);
        Assert.Equal(expected, result.Rejection);
    }

    private static GameInstallSnapshot Game(bool installed = false, string source = @"K:\\Game.rar", string genre = "Visual Novel", Guid? gameId = null) =>
        new(gameId ?? Guid.Parse("7604d65c-cb66-4fba-83c3-b1459f754f53"), "Game", installed, source, new[] { genre });

    private sealed class ExistingFileProbe : IFileProbe
    {
        public bool Exists(string path) => !path.Contains("missing", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ActiveWorkflows(bool active) : IActiveWorkflowLookup
    {
        public bool HasActiveWorkflow(Guid gameId) => active;
    }

    private sealed class InactiveWorkflows : IActiveWorkflowLookup
    {
        public bool HasActiveWorkflow(Guid gameId) => false;
    }

}

using VNInstallation.Protocol.Contracts;
using VnManager.PlayniteInstallation.Domain;

namespace VnManager.PlayniteInstallation.Tests;

public sealed class PortableDestinationFormTests
{
    [Fact]
    public void Suggested_path_prefills_the_portable_destination_form()
    {
        var form = PortableDestinationForm.Create(new PortableDestinationPrompt(@"D:\Jogos\星の記憶"));

        Assert.Equal(@"D:\Jogos\星の記憶", form.DestinationDirectory);
    }

    [Theory]
    [InlineData(PortableDestinationPathState.Missing)]
    [InlineData(PortableDestinationPathState.Empty)]
    public void Missing_or_empty_path_confirms_without_merge_consent(PortableDestinationPathState state)
    {
        var form = PortableDestinationForm.Create(new PortableDestinationPrompt(@"D:\Sugestao"));

        var confirmed = form.TrySelect(@"D:\Jogos\星の記憶", new RecordingPathProbe(state), out var selection, out var validationError);

        Assert.True(confirmed);
        Assert.Null(validationError);
        Assert.Equal(new PortableDestinationSelection(@"D:\Jogos\星の記憶", false), selection);
    }

    [Fact]
    public void Non_empty_path_requires_merge_confirmation()
    {
        var form = PortableDestinationForm.Create(new PortableDestinationPrompt(@"D:\Sugestao"));

        var confirmed = form.TrySelect(@"D:\Jogos\星の記憶", new RecordingPathProbe(PortableDestinationPathState.NonEmpty), out var selection, out var validationError);

        Assert.True(confirmed);
        Assert.Equal(new PortableDestinationSelection(@"D:\Jogos\星の記憶", false), selection);
        Assert.Null(validationError);
        Assert.True(form.RequiresExistingContentMergeConfirmation);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\0")]
    public void File_inaccessible_and_malformed_paths_are_rejected(string path)
    {
        var form = PortableDestinationForm.Create(new PortableDestinationPrompt(@"D:\Sugestao"));

        var confirmed = form.TrySelect(path, new ThrowingPathProbe(), out var selection, out var validationError);

        Assert.False(confirmed);
        Assert.Null(selection);
        Assert.False(string.IsNullOrWhiteSpace(validationError));
    }

    private sealed class RecordingPathProbe(PortableDestinationPathState state) : IPortableDestinationPathProbe
    {
        public PortableDestinationPathState Inspect(string path) => state;
    }

    private sealed class ThrowingPathProbe : IPortableDestinationPathProbe
    {
        public PortableDestinationPathState Inspect(string path) => throw new UnauthorizedAccessException();
    }
}

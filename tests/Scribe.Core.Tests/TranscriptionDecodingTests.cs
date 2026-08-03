using Scribe.Core.Transcription;

namespace Scribe.Core.Tests;

/// <summary>
/// Locks the decoding policy that fixed the production "Yeah." bug: a whole spoken paragraph came
/// back as one invented word because beam search was selected. See <see cref="TranscriptionDecoding"/>
/// for the measurement behind it.
/// </summary>
public class TranscriptionDecodingTests
{
    [Theory]
    [InlineData(TranscriptionModelArchitecture.NemoTransducer)]
    [InlineData(TranscriptionModelArchitecture.Moonshine)]
    public void BeamSearchIsRefusedForEveryShippedArchitecture(TranscriptionModelArchitecture architecture)
    {
        var selection = TranscriptionDecoding.Resolve(
            TranscriptionDecoding.ModifiedBeamSearch, architecture);

        Assert.Equal(TranscriptionDecoding.Greedy, selection.Method);
        Assert.True(selection.Overridden);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("greedy_search")]
    [InlineData("beam")]
    [InlineData("modified beam search")]
    [InlineData("something-a-future-version-writes")]
    public void AnythingOtherThanBeamSearchDecodesGreedilyWithoutClaimingAnOverride(string? configured)
    {
        var selection = TranscriptionDecoding.Resolve(
            configured, TranscriptionModelArchitecture.NemoTransducer);

        Assert.Equal(TranscriptionDecoding.Greedy, selection.Method);
        Assert.False(selection.Overridden);
    }

    [Fact]
    public void CaseDoesNotLetBeamSearchThroughTheRefusal()
    {
        var selection = TranscriptionDecoding.Resolve(
            "Modified_Beam_Search", TranscriptionModelArchitecture.NemoTransducer);

        Assert.Equal(TranscriptionDecoding.Greedy, selection.Method);
        Assert.True(selection.Overridden);
    }

    [Fact]
    public void DiagnosticsCanStillSelectBeamSearchDeliberately()
    {
        var selection = TranscriptionDecoding.Resolve(
            TranscriptionDecoding.ModifiedBeamSearch,
            TranscriptionModelArchitecture.NemoTransducer,
            allowUnsafe: true);

        Assert.Equal(TranscriptionDecoding.ModifiedBeamSearch, selection.Method);
        Assert.False(selection.Overridden);
    }

    [Fact]
    public void TheUnsafeEscapeHatchStillCannotTurnGreedyIntoBeamSearch()
    {
        var selection = TranscriptionDecoding.Resolve(
            TranscriptionDecoding.Greedy,
            TranscriptionModelArchitecture.NemoTransducer,
            allowUnsafe: true);

        Assert.Equal(TranscriptionDecoding.Greedy, selection.Method);
    }

    [Fact]
    public void TranscriptionOptionsDefaultToGreedyAndKeepTheEscapeHatchClosed()
    {
        var options = new TranscriptionOptions();

        Assert.Equal(TranscriptionDecoding.Greedy, options.DecodingMethod);
        Assert.False(options.AllowUnsafeDecodingMethod);
    }
}

using Scribe.Core.Transcription;
using Scribe.Core.Infrastructure;

namespace Scribe.Core.Tests;

public sealed class TranscriptionModelCatalogTests
{
    [Fact]
    public void Default_is_bundled_multilingual_Parakeet_v3()
    {
        var model = TranscriptionModelCatalog.Resolve(null);

        Assert.Equal(TranscriptionModelCatalog.DefaultId, model.Id);
        Assert.Equal(TranscriptionModelArchitecture.NemoTransducer, model.Architecture);
        Assert.True(model.IsBundled);
        Assert.Contains("25", model.Languages);
    }

    [Theory]
    [InlineData("moonshine-base-en-int8")]
    [InlineData("moonshine-tiny-en-int8")]
    public void Optional_models_have_pinned_https_archives(string id)
    {
        var model = TranscriptionModelCatalog.Resolve(id);

        Assert.False(model.IsBundled);
        Assert.Equal(Uri.UriSchemeHttps, model.DownloadUri!.Scheme);
        Assert.Equal(64, model.ArchiveSha256!.Length);
        Assert.Equal(TranscriptionModelArchitecture.Moonshine, model.Architecture);
        Assert.NotEmpty(model.RequiredFiles);
    }

    [Fact]
    public void Unknown_model_falls_back_to_default()
    {
        Assert.Equal(
            TranscriptionModelCatalog.DefaultId,
            TranscriptionModelCatalog.Resolve("not-a-real-model").Id);
    }

    [Fact]
    public void Model_set_validates_the_selected_architectures_files()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"scribe-model-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var model = TranscriptionModelCatalog.Resolve("moonshine-tiny-en-int8");
            foreach (var file in model.RequiredFiles)
            {
                File.WriteAllBytes(Path.Combine(directory, file), [0]);
            }

            var set = ModelSet.ForDirectory(directory, model.RequiredFiles);

            Assert.True(set.AsrComplete);
            File.Delete(Path.Combine(directory, model.RequiredFiles[0]));
            Assert.False(set.AsrComplete);
            Assert.Equal([model.RequiredFiles[0]], set.MissingAsrFiles());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

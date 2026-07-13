using Scribe.Core.Transcription;
using Scribe.Core.Infrastructure;
using SherpaOnnx;

namespace Scribe.Core.Tests;

public sealed class TranscriptionModelCatalogTests
{
    [Fact]
    public void Default_is_downloadable_multilingual_Parakeet_v3()
    {
        var model = TranscriptionModelCatalog.Resolve(null);

        Assert.Equal(TranscriptionModelCatalog.DefaultId, model.Id);
        Assert.Equal(TranscriptionModelArchitecture.NemoTransducer, model.Architecture);
        Assert.False(model.IsBundled);
        Assert.Contains("25", model.Languages);
        var downloadFiles = model.DownloadFiles!;
        Assert.Equal(model.RequiredFiles, downloadFiles.Select(file => file.FileName));
        Assert.Equal(model.DownloadSize, downloadFiles.Sum(file => file.Size));
        Assert.All(downloadFiles, file =>
        {
            Assert.Equal(Uri.UriSchemeHttps, file.DownloadUri.Scheme);
            Assert.Equal(64, file.Sha256.Length);
        });
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
    public void Parakeet_configuration_updates_the_recognizer_struct()
    {
        var directory = Path.Combine(Path.GetTempPath(), "scribe-parakeet-config");
        var model = TranscriptionModelCatalog.Resolve(TranscriptionModelCatalog.DefaultId);
        var models = ModelSet.ForDirectory(directory, model.RequiredFiles);
        var config = new OfflineRecognizerConfig();

        TranscriptionService.ConfigureModel(ref config, model, models);

        Assert.Equal(models.EncoderPath, config.ModelConfig.Transducer.Encoder);
        Assert.Equal(models.DecoderPath, config.ModelConfig.Transducer.Decoder);
        Assert.Equal(models.JoinerPath, config.ModelConfig.Transducer.Joiner);
        Assert.Equal("nemo_transducer", config.ModelConfig.ModelType);
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

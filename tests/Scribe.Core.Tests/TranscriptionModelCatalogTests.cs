using System.Globalization;
using System.Text.RegularExpressions;
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
    public void Parakeet_Unified_ships_in_its_own_folder_and_downloads_only_as_a_fallback()
    {
        var model = TranscriptionModelCatalog.Resolve(TranscriptionModelCatalog.ParakeetUnifiedEnglishId);
        var parakeet = TranscriptionModelCatalog.Resolve(TranscriptionModelCatalog.DefaultId);

        Assert.Equal(TranscriptionModelCatalog.ParakeetUnifiedEnglishId, model.Id);
        Assert.Equal(TranscriptionModelArchitecture.NemoTransducer, model.Architecture);
        Assert.Equal(parakeet.RequiredFiles, model.RequiredFiles);
        Assert.Equal("English", model.Languages);
        Assert.Equal(model.Id, model.BundledDirectory);
        Assert.Null(parakeet.BundledDirectory);

        // Not IsBundled: a build without its folder (a source tree that predates it) can still download it.
        Assert.False(model.IsBundled);
        Assert.Equal(Uri.UriSchemeHttps, model.DownloadUri!.Scheme);
        Assert.Equal(64, model.ArchiveSha256!.Length);
    }

    [Fact]
    public void A_model_that_ships_with_the_app_is_found_in_its_own_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"scribe-bundled-model-{Guid.NewGuid():N}");
        var paths = new AppPaths(root);
        var model = TranscriptionModelCatalog.Resolve(TranscriptionModelCatalog.ParakeetUnifiedEnglishId)
            with { Id = "probe-model", BundledDirectory = "probe-folder" };
        try
        {
            var locator = new ModelLocator(paths);

            // Nowhere yet: the answer is the folder a download would fill, so Settings offers it.
            Assert.False(locator.Resolve(model).AsrComplete);
            Assert.Equal(Path.Combine(paths.ModelsDir, "probe-model"), locator.Resolve(model).Directory);

            var bundled = Path.Combine(paths.ModelsDir, "probe-folder");
            Directory.CreateDirectory(bundled);
            foreach (var file in model.RequiredFiles)
            {
                File.WriteAllBytes(Path.Combine(bundled, file), [0]);
            }

            var set = locator.ResolveOrDefault(model, out var usedFallback);

            Assert.False(usedFallback);
            Assert.True(set.AsrComplete);
            Assert.Equal(bundled, set.Directory);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void The_release_manifest_ships_every_model_the_catalog_says_ships_with_the_app()
    {
        var repository = RepositoryRoot();
        var manifest = File.ReadAllText(Path.Combine(repository, "scripts", "Model-Manifest.ps1"));
        var bundled = TranscriptionModelCatalog.Curated.Where(model => model.BundledDirectory is not null).ToList();
        Assert.NotEmpty(bundled);

        foreach (var model in bundled)
        {
            long total = 0;
            foreach (var file in model.RequiredFiles)
            {
                var entry = Regex.Match(
                    manifest,
                    $@"Path\s*=\s*'{Regex.Escape($"{model.BundledDirectory}/{file}")}';\s*Size\s*=\s*(\d+);");
                Assert.True(entry.Success, $"{model.Id}: {file} is not in Model-Manifest.ps1.");
                total += long.Parse(entry.Groups[1].Value, CultureInfo.InvariantCulture);
            }

            // The size Settings shows is what the folder holds.
            Assert.Equal(model.DownloadSize, total);
        }

        // The NVIDIA Open Model License (section 3.1): every copy carries the agreement and a Notice text file.
        var licenses = Path.Combine(
            repository, "src", "Scribe.App", "ModelLicenses", TranscriptionModelCatalog.ParakeetUnifiedEnglishId);
        Assert.True(new FileInfo(Path.Combine(licenses, "NVIDIA-Open-Model-License.pdf")).Length > 0);
        Assert.Contains(
            "Licensed by NVIDIA Corporation under the NVIDIA Open Model License",
            File.ReadAllText(Path.Combine(licenses, "NOTICE.txt")),
            StringComparison.Ordinal);
        Assert.Contains(
            $"'{TranscriptionModelCatalog.ParakeetUnifiedEnglishId}/NOTICE.txt'", manifest, StringComparison.Ordinal);
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

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }
}

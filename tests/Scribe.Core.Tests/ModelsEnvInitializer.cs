using System.Runtime.CompilerServices;

namespace Scribe.Core.Tests;

/// <summary>
/// The offline speech models now live next to the app that ships them
/// (<c>src/Scribe.App/models</c>), which is outside this test project's parent chain — so
/// <see cref="Scribe.Core.Infrastructure.ModelLocator"/>'s ancestor walk no longer reaches them.
/// Point the locator at the source folder via its <c>SCRIBE_MODELS_DIR</c> override (honoured
/// first) when it is present, so the engine smoke tests keep running locally. When the models are
/// absent (e.g. CI without <c>Download-Models.ps1</c>), nothing is set and those tests skip
/// themselves exactly as before. Never overrides an env var the developer set explicitly.
/// </summary>
internal static class ModelsEnvInitializer
{
    [ModuleInitializer]
    internal static void EnsureModelsDir()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SCRIBE_MODELS_DIR")))
        {
            return;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Scribe.App", "models");
            if (File.Exists(Path.Combine(candidate, "encoder.int8.onnx")))
            {
                Environment.SetEnvironmentVariable("SCRIBE_MODELS_DIR", candidate);
                return;
            }
        }
    }
}

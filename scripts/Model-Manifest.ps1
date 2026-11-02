# Runtime model payload shared by the downloader and release preflight.
# Archive names an entry of $ScribeModelArchives for a file that comes out of a release archive rather than its own URL.
$ScribeRuntimeModelManifest = @(
    [pscustomobject]@{ Path = 'encoder.int8.onnx'; Size = 652184281; Sha256 = 'acfc2b4456377e15d04f0243af540b7fe7c992f8d898d751cf134c3a55fd2247'; Archive = $null }
    [pscustomobject]@{ Path = 'decoder.int8.onnx'; Size = 11845275;  Sha256 = '179e50c43d1a9de79c8a24149a2f9bac6eb5981823f2a2ed88d655b24248db4e'; Archive = $null }
    [pscustomobject]@{ Path = 'joiner.int8.onnx';  Size = 6355277;   Sha256 = '3164c13fc2821009440d20fcb5fdc78bff28b4db2f8d0f0b329101719c0948b3'; Archive = $null }
    [pscustomobject]@{ Path = 'tokens.txt';        Size = 93939;     Sha256 = 'd58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d'; Archive = $null }
    [pscustomobject]@{ Path = 'silero_vad_v5.onnx'; Size = 2313101; Sha256 = '6b99cbfd39246b6706f98ec13c7c50c6b299181f2474fa05cbc8046acc274396'; Archive = $null }
    # Parakeet Unified (English), TranscriptionModelCatalog.ParakeetUnifiedEnglishId, under the NVIDIA Open Model License.
    [pscustomobject]@{ Path = 'parakeet-unified-en-0.6b-int8/encoder.int8.onnx'; Size = 654040552; Sha256 = '6716910b7a0833997fec7a410494c995d70124001a0e9b66d6370d6aced577e0'; Archive = 'parakeet-unified-en' }
    [pscustomobject]@{ Path = 'parakeet-unified-en-0.6b-int8/decoder.int8.onnx'; Size = 7257753;   Sha256 = 'a5e223392c90e75f8144cdb5eb95af7625db389e39edef2bd1a9c872b3298fe6'; Archive = 'parakeet-unified-en' }
    [pscustomobject]@{ Path = 'parakeet-unified-en-0.6b-int8/joiner.int8.onnx';  Size = 1735860;   Sha256 = '869f43f7d24595c55581ad3bf249a935fb8a71389fbdaa7504b9f46f93140f8a'; Archive = 'parakeet-unified-en' }
    [pscustomobject]@{ Path = 'parakeet-unified-en-0.6b-int8/tokens.txt';        Size = 8952;      Sha256 = 'dc0b4584ab2e4ddbf888425c076c61b736e7356a015250db7d307e6f1a8188ff'; Archive = 'parakeet-unified-en' }
)

# Release archives the manifest's files come out of. Directory is the archive's top folder, whose files are copied
# into Destination under the models folder; Documents are the model card files NVIDIA ships with it, kept beside it.
$ScribeModelArchives = @{
    'parakeet-unified-en' = [pscustomobject]@{
        Url         = 'https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-parakeet-unified-en-0.6b-int8-non-streaming.tar.bz2'
        Size        = 501350460
        Sha256      = '99f63605b3a85a54c250c0869670a687b7d6598a47bf2421515e1f839a76e150'
        Directory   = 'sherpa-onnx-nemo-parakeet-unified-en-0.6b-int8-non-streaming'
        Destination = 'parakeet-unified-en-0.6b-int8'
        Documents   = @('bias.md', 'explainability.md', 'privacy.md', 'safety.md')
    }
}

# Files that must ship with a model's weights. The NVIDIA Open Model License (section 3.1) requires every copy of the
# model to carry the agreement and a Notice text file; the app project copies both from src/Scribe.App/ModelLicenses.
$ScribeModelLicenseManifest = @(
    'parakeet-unified-en-0.6b-int8/NVIDIA-Open-Model-License.pdf'
    'parakeet-unified-en-0.6b-int8/NOTICE.txt'
)

function Test-ScribeRuntimeModels {
    param(
        [Parameter(Mandatory)] [string]$ModelsDir,
        [switch]$VerifyHashes,
        # Set for a published payload, which must carry each model's license files as well as its weights.
        [switch]$RequireLicenses
    )

    $errors = @()
    foreach ($item in $ScribeRuntimeModelManifest) {
        $path = Join-Path $ModelsDir $item.Path
        if (-not (Test-Path $path -PathType Leaf)) {
            $errors += "Missing runtime model: $path"
            continue
        }

        $length = (Get-Item $path).Length
        if ($length -ne $item.Size) {
            $errors += "Wrong size for $($item.Path): got $length, expected $($item.Size)"
            continue
        }

        if ($VerifyHashes -and $item.Sha256) {
            $actual = (Get-FileHash $path -Algorithm SHA256).Hash
            if ($actual -ne $item.Sha256.ToUpperInvariant()) {
                $errors += "SHA-256 mismatch for $($item.Path)"
            }
        }
    }

    if ($RequireLicenses) {
        foreach ($license in $ScribeModelLicenseManifest) {
            $path = Join-Path $ModelsDir $license
            if (-not (Test-Path $path -PathType Leaf) -or (Get-Item $path).Length -eq 0) {
                $errors += "Missing model license file: $path"
            }
        }
    }

    if ($errors.Count -gt 0) {
        throw ($errors -join [Environment]::NewLine)
    }
}

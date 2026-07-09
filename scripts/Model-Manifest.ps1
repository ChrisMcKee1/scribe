# Runtime model payload shared by the downloader and release preflight.
$ScribeRuntimeModelManifest = @(
    [pscustomobject]@{ Path = 'encoder.int8.onnx'; Size = 652184281; Sha256 = 'acfc2b4456377e15d04f0243af540b7fe7c992f8d898d751cf134c3a55fd2247' }
    [pscustomobject]@{ Path = 'decoder.int8.onnx'; Size = 11845275;  Sha256 = '179e50c43d1a9de79c8a24149a2f9bac6eb5981823f2a2ed88d655b24248db4e' }
    [pscustomobject]@{ Path = 'joiner.int8.onnx';  Size = 6355277;   Sha256 = '3164c13fc2821009440d20fcb5fdc78bff28b4db2f8d0f0b329101719c0948b3' }
    [pscustomobject]@{ Path = 'tokens.txt';        Size = 93939;     Sha256 = 'd58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d' }
    [pscustomobject]@{ Path = 'silero_vad_v5.onnx'; Size = 2313101; Sha256 = '6b99cbfd39246b6706f98ec13c7c50c6b299181f2474fa05cbc8046acc274396' }
)

function Test-ScribeRuntimeModels {
    param(
        [Parameter(Mandatory)] [string]$ModelsDir,
        [switch]$VerifyHashes
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

    if ($errors.Count -gt 0) {
        throw ($errors -join [Environment]::NewLine)
    }
}
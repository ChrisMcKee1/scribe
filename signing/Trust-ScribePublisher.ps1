#requires -Version 5.1
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$RootCertificatePath = (Join-Path $PSScriptRoot 'Scribe-Root-CA.cer'),
    [string]$SigningCertificatePath = (Join-Path $PSScriptRoot 'Scribe-CodeSigning.cer'),
    [string]$ExpectedRootThumbprint,
    [string]$ExpectedSigningThumbprint
)

$ErrorActionPreference = 'Stop'
$rootPath = (Resolve-Path $RootCertificatePath).Path
$leafPath = (Resolve-Path $SigningCertificatePath).Path
$root = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($rootPath)
$leaf = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($leafPath)
$rootThumbprint = $root.Thumbprint.ToUpperInvariant()
$leafThumbprint = $leaf.Thumbprint.ToUpperInvariant()

if ($ExpectedRootThumbprint -and $rootThumbprint -ne ($ExpectedRootThumbprint -replace '\s', '').ToUpperInvariant()) {
    throw "Root certificate thumbprint mismatch. Expected $ExpectedRootThumbprint, found $rootThumbprint."
}
if ($ExpectedSigningThumbprint -and $leafThumbprint -ne ($ExpectedSigningThumbprint -replace '\s', '').ToUpperInvariant()) {
    throw "Signing certificate thumbprint mismatch. Expected $ExpectedSigningThumbprint, found $leafThumbprint."
}
if ($root.Subject -ne $root.Issuer) { throw 'The root certificate is not self-signed.' }
if ($leaf.Issuer -ne $root.Subject) { throw 'The code-signing certificate was not issued by the supplied Scribe root.' }

Write-Host "Root publisher : $($root.Subject)"
Write-Host "Root thumbprint: $rootThumbprint"
Write-Host "Root SHA-256   : $((Get-FileHash $rootPath -Algorithm SHA256).Hash)"
Write-Host "Code signer    : $($leaf.Subject)"
Write-Host "Leaf thumbprint: $leafThumbprint"
Write-Host "Leaf SHA-256   : $((Get-FileHash $leafPath -Algorithm SHA256).Hash)"

if ($PSCmdlet.ShouldProcess('CurrentUser Root and TrustedPublisher certificate stores', 'Trust Scribe certificate chain')) {
    foreach ($item in @(
        @{ Name = 'Root'; Certificate = $root },
        @{ Name = 'TrustedPublisher'; Certificate = $leaf }
    )) {
        $store = [System.Security.Cryptography.X509Certificates.X509Store]::new(
            $item.Name,
            [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
        try {
            $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
            $store.Add($item.Certificate)
        }
        finally {
            $store.Close()
        }
    }

    $chain = [System.Security.Cryptography.X509Certificates.X509Chain]::new()
    $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
    if (-not $chain.Build($leaf)) {
        $status = ($chain.ChainStatus | ForEach-Object { $_.StatusInformation.Trim() }) -join '; '
        throw "Windows did not validate the installed Scribe certificate chain: $status"
    }
    if ($chain.ChainElements[$chain.ChainElements.Count - 1].Certificate.Thumbprint -ne $rootThumbprint) {
        throw 'The validated chain did not terminate at the expected Scribe root.'
    }

    Write-Host 'Scribe publisher chain trusted for the current Windows user.' -ForegroundColor Green
}

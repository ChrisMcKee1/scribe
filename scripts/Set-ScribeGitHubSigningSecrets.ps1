#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Repository = 'ChrisMcKee1/scribe',
    [string]$Environment = 'release-signing',
    [string]$PublicCertificatePath = (Join-Path $PSScriptRoot '..\signing\Scribe-CodeSigning.cer')
)

$ErrorActionPreference = 'Stop'
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'GitHub CLI (gh) is required.' }

$publicPath = (Resolve-Path $PublicCertificatePath).Path
$publicCertificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($publicPath)
$thumbprint = $publicCertificate.Thumbprint.ToUpperInvariant()
$certificate = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Thumbprint -eq $thumbprint } |
    Select-Object -First 1

if (-not $certificate -or -not $certificate.HasPrivateKey) {
    throw "Certificate $thumbprint with a private key was not found in Cert:\CurrentUser\My."
}

$tempPfx = Join-Path ([System.IO.Path]::GetTempPath()) ("scribe-signing-{0}.pfx" -f [guid]::NewGuid().ToString('N'))
$passwordBytes = [byte[]]::new(32)
[System.Security.Cryptography.RandomNumberGenerator]::Fill($passwordBytes)
$passwordText = [Convert]::ToBase64String($passwordBytes)
$securePassword = ConvertTo-SecureString $passwordText -AsPlainText -Force

try {
    Export-PfxCertificate `
        -Cert $certificate `
        -FilePath $tempPfx `
        -Password $securePassword `
        -CryptoAlgorithmOption AES256_SHA256 `
        -Force | Out-Null

    $pfxBase64 = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($tempPfx))
    gh api --method PUT "repos/$Repository/environments/$Environment" | Out-Null
    $pfxBase64 | gh secret set SCRIBE_SIGNING_PFX_BASE64 --repo $Repository --env $Environment 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'Failed to set SCRIBE_SIGNING_PFX_BASE64.' }
    $passwordText | gh secret set SCRIBE_SIGNING_PFX_PASSWORD --repo $Repository --env $Environment 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'Failed to set SCRIBE_SIGNING_PFX_PASSWORD.' }

    Write-Host "Configured GitHub environment '$Environment' for certificate $thumbprint." -ForegroundColor Green
}
finally {
    if (Test-Path $tempPfx) { Remove-Item $tempPfx -Force }
    if ($passwordBytes) { [Array]::Clear($passwordBytes, 0, $passwordBytes.Length) }
    $passwordText = $null
    $securePassword = $null
    $pfxBase64 = $null
}

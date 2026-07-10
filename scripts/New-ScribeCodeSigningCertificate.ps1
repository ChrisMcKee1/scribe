#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$RootSubject = 'CN=Scribe Release Root CA 2026, O=Scribe',
    [string]$SigningSubject = 'CN=Chris McKee, O=Scribe',
    [int]$RootValidYears = 10,
    [int]$SigningValidYears = 3,
    [string]$RootCertificatePath = (Join-Path $PSScriptRoot '..\signing\Scribe-Root-CA.cer'),
    [string]$SigningCertificatePath = (Join-Path $PSScriptRoot '..\signing\Scribe-CodeSigning.cer'),
    [switch]$ExportableForGitHubActions,
    [switch]$Replace
)

$ErrorActionPreference = 'Stop'
$codeSigningOid = '1.3.6.1.5.5.7.3.3'

function Get-UsableCertificate([string]$Subject) {
    Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $Subject -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
}

$existingRoot = Get-UsableCertificate $RootSubject
$existingLeaf = Get-UsableCertificate $SigningSubject
if (($existingRoot -or $existingLeaf) -and -not $Replace) {
    throw 'Scribe signing certificate material already exists. Use -Replace only for intentional certificate rotation.'
}

if ($Replace) {
    foreach ($storeName in 'My', 'Root', 'TrustedPeople', 'TrustedPublisher') {
        $store = [System.Security.Cryptography.X509Certificates.X509Store]::new(
            $storeName,
            [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
        try {
            $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
            @($store.Certificates | Where-Object { $_.Subject -in $RootSubject, $SigningSubject }) |
                ForEach-Object { $store.Remove($_) }
        }
        finally {
            $store.Close()
        }
    }
}

$root = New-SelfSignedCertificate `
    -Type Custom `
    -Subject $RootSubject `
    -FriendlyName 'Scribe Private Root CA' `
    -CertStoreLocation Cert:\CurrentUser\My `
    -KeyAlgorithm RSA `
    -KeyLength 4096 `
    -HashAlgorithm SHA256 `
    -KeyExportPolicy NonExportable `
    -KeyUsage CertSign, CRLSign `
    -TextExtension @('2.5.29.19={critical}{text}ca=1&pathlength=0') `
    -NotAfter (Get-Date).AddYears($RootValidYears)

$leafExportPolicy = if ($ExportableForGitHubActions) { 'Exportable' } else { 'NonExportable' }
$leaf = $null
try {
    $leaf = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $SigningSubject `
        -FriendlyName 'Scribe Code Signing' `
        -Signer $root `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyAlgorithm RSA `
        -KeyLength 3072 `
        -HashAlgorithm SHA256 `
        -KeyExportPolicy $leafExportPolicy `
        -KeyUsage DigitalSignature `
        -NotAfter (Get-Date).AddYears($SigningValidYears)

    $ekuExtension = $leaf.Extensions |
        Where-Object { $_.Oid.Value -eq '2.5.29.37' } |
        Select-Object -First 1
    $hasCodeSigningEku = $ekuExtension -and
        @($ekuExtension.EnhancedKeyUsages | Where-Object { $_.Value -eq $codeSigningOid }).Count -eq 1
    if (-not $leaf.HasPrivateKey -or -not $hasCodeSigningEku -or $leaf.Issuer -ne $root.Subject) {
        throw 'The generated leaf is missing its private key, Code Signing EKU, or expected issuer.'
    }

    $rootPath = [System.IO.Path]::GetFullPath($RootCertificatePath)
    $leafPath = [System.IO.Path]::GetFullPath($SigningCertificatePath)
    New-Item -ItemType Directory -Path (Split-Path $rootPath -Parent) -Force | Out-Null
    Export-Certificate -Cert $root -FilePath $rootPath -Type CERT -Force | Out-Null
    Export-Certificate -Cert $leaf -FilePath $leafPath -Type CERT -Force | Out-Null

    Write-Host 'Created Scribe private PKI.' -ForegroundColor Green
    Write-Host "  Root subject       : $($root.Subject)"
    Write-Host "  Root thumbprint    : $($root.Thumbprint)"
    Write-Host "  Root expires       : $($root.NotAfter.ToString('u'))"
    Write-Host "  Root public cert   : $rootPath"
    Write-Host "  Root CER SHA-256   : $((Get-FileHash $rootPath -Algorithm SHA256).Hash)"
    Write-Host "  Signing subject    : $($leaf.Subject)"
    Write-Host "  Signing thumbprint : $($leaf.Thumbprint)"
    Write-Host "  Signing expires    : $($leaf.NotAfter.ToString('u'))"
    Write-Host "  Signing public cert: $leafPath"
    Write-Host "  Signing CER SHA-256: $((Get-FileHash $leafPath -Algorithm SHA256).Hash)"
    Write-Host '  Root private key   : Cert:\CurrentUser\My (non-exportable)'
    Write-Host "  Leaf private key   : Cert:\CurrentUser\My ($leafExportPolicy)"
    Write-Host '  Trust              : run signing\Trust-ScribePublisher.ps1 explicitly'
    if ($ExportableForGitHubActions) {
        Write-Warning 'The code-signing leaf can be exported. Protect the release-signing environment and repository write access.'
    }
}
catch {
    if ($leaf) { Remove-Item "Cert:\CurrentUser\My\$($leaf.Thumbprint)" -Force -ErrorAction SilentlyContinue }
    Remove-Item "Cert:\CurrentUser\My\$($root.Thumbprint)" -Force -ErrorAction SilentlyContinue
    throw
}

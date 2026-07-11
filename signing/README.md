# Scribe self-signed publisher certificate

Scribe release executables are Authenticode-signed with a private code-signing certificate issued by the Scribe Private Root CA.

Self-signing provides integrity and a stable publisher identity only after a Windows user or administrator trusts the public certificate chain. It does **not** create Microsoft SmartScreen reputation, so Windows may still warn for a new or low-reputation download.

## Public certificate fingerprints

Verify these through a trusted channel before installing them:

| Certificate | SHA-1 thumbprint | SHA-256 of `.cer` |
| --- | --- | --- |
| Scribe Release Root CA 2026 | `73734FC58C946BD98BD73E1F0B9125BBDFAD7175` | `CCAA75EC5E96A3DDA61249702A910AFB9A04CC261FFBDF24E19F7F70D8365F29` |
| Chris McKee / Scribe code signer | `E08AF872C3C1D7909C0AC99B69EAD0643312E26D` | `E3EDE447018FC3CDC025384A838500290518339AE638E7389DE567B59690853F` |

The release includes only public `.cer` files. The root private key never leaves the maintainer's Windows certificate store. The code-signing leaf is stored in GitHub's protected `release-signing` environment as an encrypted PFX for release automation; it is never committed or attached to a release.

## Trust for the current Windows user

From the folder containing the downloaded certificates and trust script:

```powershell
pwsh ./Trust-ScribePublisher.ps1 `
  -ExpectedRootThumbprint 73734FC58C946BD98BD73E1F0B9125BBDFAD7175 `
  -ExpectedSigningThumbprint E08AF872C3C1D7909C0AC99B69EAD0643312E26D
```

Windows will ask you to confirm the Scribe root certificate. Confirm the displayed root thumbprint is exactly:

```text
7373 4FC5 8C94 6BD9 8BD7 3E1F 0B91 25BB DFAD 7175
```

The script installs the public root into `CurrentUser\Root`, the signing leaf into `CurrentUser\TrustedPublisher`, then validates the chain. It does not require administrator rights and never installs a private key.

Managed environments can deploy the same public root and leaf through Intune or Group Policy instead.

## Microsoft Defender managed environments

This private publisher certificate proves file integrity and publisher identity, but it does not
create Microsoft cloud reputation. The Defender attack surface reduction rule **Block executable
files from running unless they meet a prevalence, age, or trusted list criterion** can therefore
audit or block a newly signed Scribe release until the organization allows the publisher.

Microsoft Defender for Endpoint supports certificate allow indicators for this scenario, and this
ASR rule honors certificate indicators. A Defender administrator should:

1. Deploy `Scribe-Root-CA.cer` to **Local Machine > Trusted Root Certification Authorities** on
  the scoped devices. Current-user trust alone does not meet the certificate-indicator requirement.
2. In the Microsoft Defender portal, go to **Settings > Endpoints > Indicators > Certificates**.
3. Add `Scribe-CodeSigning.cer` as an indicator with action **Allow** and scope it to the appropriate
  machine group.
4. Allow up to three hours for a new certificate indicator to propagate.

Use the leaf certificate for the indicator, not the root. The stable leaf thumbprint is
`E08AF872C3C1D7909C0AC99B69EAD0643312E26D`, so the allow applies across Scribe versions signed
with this publisher identity. A per-file hash allow is less durable because every release changes
the executable hashes.

## Verify a release file

```powershell
Get-AuthenticodeSignature ./Scribe-win-x64-Setup.exe |
  Select-Object Status, StatusMessage,
    @{n='Signer';e={$_.SignerCertificate.Subject}},
    @{n='Thumbprint';e={$_.SignerCertificate.Thumbprint}},
    @{n='Timestamp';e={$_.TimeStamperCertificate.Subject}}
```

Expected values:

- `Status`: `Valid`
- signer: `CN=Chris McKee, O=Scribe`
- thumbprint: `E08AF872C3C1D7909C0AC99B69EAD0643312E26D`
- a DigiCert RFC 3161 timestamp certificate

## Remove trust

```powershell
Remove-Item Cert:\CurrentUser\TrustedPublisher\E08AF872C3C1D7909C0AC99B69EAD0643312E26D
Remove-Item Cert:\CurrentUser\Root\73734FC58C946BD98BD73E1F0B9125BBDFAD7175
```

Existing timestamped files remain cryptographically signed, but Windows will no longer trust this private publisher chain after removal.

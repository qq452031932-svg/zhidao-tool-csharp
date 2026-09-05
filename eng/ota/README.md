# OTA publisher public identity

`SuperPan.OtaPublisher.cer` is the DER-encoded public certificate for the controlled SuperPan OTA publisher identity. It is safe to distribute and is embedded in `BaiduBatchTransfer.App` as the public trust anchor.

- Subject: `CN=SuperPan OTA Publisher`
- RSA key size: 3072 bits
- Certificate signature: SHA-256 with RSA
- Enhanced key usage: Code Signing
- Key usage: Digital Signature
- SPKI SHA-256: `f905188d79bb427c1a6eacc4bc9741d68a407228f643f6a50bfd791211da6cff`
- Expires: `2041-09-05T09:31:32Z`

The private key is non-exportable and remains in the publisher account's `CurrentUser\My` certificate store. The enrollment script writes only this public certificate and the non-secret thumbprint at `.artifacts/ota/publisher-thumbprint.txt`; `.artifacts` is ignored by Git.

Run `pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/create_ota_publisher_certificate.ps1` from the repository root to enroll or validate/reuse the exact identity. Windows PowerShell 5.1 entry automatically delegates to `pwsh`; enrollment requires PowerShell 7 or later.

The default automated test suite is hermetic and never requires or changes the live certificate store. To run the read-only publisher-store acceptance checks explicitly, use:

```powershell
$env:BAIDU_BATCH_OTA_PUBLISHER_LIVE_STORE = 'approved'
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter Category=OtaPublisherLiveStore --no-restore
```

Enrollment is serialized across Windows sessions for the current user with a `Global\SuperPan.OtaPublisher.Enrollment.<current-user-SID>` named mutex before the script reads the repository certificate or opens the certificate store. The mutex owner and sole access rule are restricted to that user SID; inability to create or validate this ACL, a timeout, or an abandoned mutex stops enrollment without continuing into identity selection or output writes.

The certificate uses a 15-year controlled-distribution lifetime. Renewal must happen before expiry and requires a coordinated client/server release that updates the embedded public certificate and compile-time SPKI pin before releases are signed with the replacement identity. A self-signed certificate does not establish public-CA trust or Microsoft SmartScreen reputation, so users may still see Windows download or reputation prompts.

# Managed Local Browser Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the tested local-browser foundation that lets a user select a Chrome/Edge installation directory, persists a verified browser identity and per-account managed profile reference, launches only an app-owned visible browser, and establishes a private typed helper channel without yet performing Baidu transfer/share writes.

**Architecture:** Keep the existing C# application as the sole owner of database state, scheduling, authorization, recovery, account removal, and exports. Add engine-discriminated session contracts in Core, immutable engine/profile snapshots on every operation kind and web attempt, SQLite-backed browser installation/session state in Infrastructure, and a WPF browser-directory selection plus managed Chromium/helper lifecycle in App. The first milestone exposes browser setup, health, a typed read-only Baidu identity observation, and login-ready state; share and transfer page mutations are separate follow-on plans.

**Tech Stack:** .NET 8, WPF, Microsoft.Data.Sqlite, Windows Authenticode/Job Object APIs, Chromium DevToolsActivePort, a bundled pinned CPython/DrissionPage helper, xUnit, Python unittest.

**Spec:** `docs/superpowers/specs/2026-08-26-local-browser-automation-migration-design.md`

## Execution checkpoint — 2026-08-28

Tasks 1–7 were completed previously. Task 8 is complete through `babb1ff`; its evidence and the cleanup incident's unresolved historical ACL uncertainty are recorded in `docs/testing/managed-browser-task8-handoff.md`. Task 9 is complete through `ccb64e8`: fresh Release client+protocol tests 77/77, client-only tests 35/35 repeated three times, integrity tests 76/76 (all 0 skipped, actual helper package enabled), Python tests 28/28, solution Release `-warnaserror` build and manifest gate pass. Independent spec and quality reviews both passed; one non-blocking environment-sensitive test assertion is recorded in `docs/testing/managed-browser-task9-handoff.md`. Task 10 is next; Tasks 10–12 and the follow-on transfer/share write plans are not yet complete.

---

## Scope boundary

This plan delivers a usable, testable browser foundation only. It may navigate an app-owned browser to the HTTPS Baidu account/login route and read the stable logged-in Baidu UID from an explicitly bound DevTools target. It must not change the active `IBaiduNetdiskAdapter`/`IBaiduShareAdapter` implementation, open an imported share link, click any Baidu write control, or migrate an in-flight/started/recovering operation to the new engine. Follow-on plans will cover:

1. Managed-Chromium share execution and read-only recovery.
2. Managed-Chromium transfer execution and exact result mapping.
3. Production rollout, engine selection, and WebView2 retirement.

## File structure

### Core contracts

- Create `src/BaiduBatchTransfer.Core/Domain/BrowserFamily.cs`: supported browser family enum.
- Create `src/BaiduBatchTransfer.Core/Domain/BrowserInstallationIdentity.cs`: verified browser directory/executable identity.
- Create `src/BaiduBatchTransfer.Core/Domain/ManagedBrowserSessionReference.cs`: account/profile/UID binding without cookies.
- Create `src/BaiduBatchTransfer.Core/Domain/BaiduExecutionContext.cs`: discriminated legacy vs managed execution context.
- Create `src/BaiduBatchTransfer.Core/Abstractions/IManagedBrowserStateStore.cs`: persistence contract for installation and session references.
- Modify operation/attempt domain records in `src/BaiduBatchTransfer.Core`: immutable `ExecutionEngineVersion`, `BrowserFamily`, and `ProfileId` snapshots.

### Database and security

- Create `src/BaiduBatchTransfer.Infrastructure/Database/Migrations/016_ManagedChromiumFoundation.sql`: browser/session tables and engine/profile snapshots.
- Modify `src/BaiduBatchTransfer.Infrastructure/Database/DatabaseMigrator.cs`: version 16.
- Create `src/BaiduBatchTransfer.Infrastructure/Database/SqliteManagedBrowserStateStore.cs`: typed SQLite repository.
- Modify `src/BaiduBatchTransfer.Infrastructure/Database/Repositories/SqliteJobRepository.cs`, `SqliteTransferSchedulingStore.cs`, `SqliteShareSchedulingStore.cs`, `SqliteStandaloneShareStore.cs`, and `SqliteWebAutomationAttemptStore.cs`: read/write immutable execution snapshots.
- Create `src/BaiduBatchTransfer.Infrastructure/Security/BrowserExecutableVerifier.cs`: canonical path, reparse-point, Authenticode publisher, product identity, and replacement checks.
- Create `src/BaiduBatchTransfer.Infrastructure/Security/FileSystemManagedBrowserProfilePolicy.cs`: per-account/per-family profile paths and ownership.
- Create `src/BaiduBatchTransfer.Infrastructure/Security/FileSystemManagedBrowserProfileStore.cs`: pinned quarantine/recovery/confirmed-delete filesystem operations.

### Application lifecycle

- Modify `src/BaiduBatchTransfer.Application/Accounts/AccountRemovalContracts.cs`, `AccountRemovalCoordinator.cs`, and `RemoveAccountService.cs`: stop, quarantine, and explicitly confirm managed-profile deletion.
- Modify the scheduling creation/retry paths in `src/BaiduBatchTransfer.Application/Scheduling`: preserve the operation engine snapshot and prohibit cross-engine retry after submission may have occurred.

### WPF and process runtime

- Create `src/BaiduBatchTransfer.App/Features/ManagedBrowser/BrowserDirectoryDialog.cs`: directory picker abstraction and WPF implementation.
- Create `src/BaiduBatchTransfer.App/Features/ManagedBrowser/BrowserSetupService.cs`: select, verify, persist, and reselect flow.
- Create `src/BaiduBatchTransfer.App/Features/ManagedBrowser/ManagedBrowserProtocol.cs`: fixed IPC request/response schema.
- Create `src/BaiduBatchTransfer.App/Features/ManagedBrowser/ManagedBrowserHelperClient.cs`: private stdio/anonymous-pipe client.
- Create `src/BaiduBatchTransfer.App/Features/ManagedBrowser/ManagedChromiumProcessManager.cs`: launch, ownership, DevTools endpoint, and close lifecycle.
- Create `src/BaiduBatchTransfer.App/Features/ManagedBrowser/DevToolsActivePortReader.cs`: bounded parser for app-owned profile endpoint.
- Create `src/BaiduBatchTransfer.App/Features/ManagedBrowser/WindowsTcpOwnerTable.cs`: bind the DevTools listener port to an owned PID.
- Create `src/BaiduBatchTransfer.App/Features/ManagedBrowser/WindowsJobObject.cs`: child process lifetime binding.
- Create `src/BaiduBatchTransfer.App/Features/ManagedBrowser/ManagedBrowserLoginLauncher.cs`: setup/login entry point without Cookie extraction.
- Modify `src/BaiduBatchTransfer.App/Services/DialogService.cs`: browser-directory selection surface.
- Modify `src/BaiduBatchTransfer.App/AppBootstrap.cs`: register foundation services without switching production adapters.
- Modify `src/BaiduBatchTransfer.App/ApplicationShutdownCoordinator.cs`: stop the helper and only app-owned browser processes during shutdown.
- Modify `src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj`: embed helper manifest and package helper artifacts.

### Minimal helper

- Create `tools/managed_browser_helper/requirements.lock`: exact helper dependency versions and hashes.
- Create `tools/managed_browser_helper/protocol.py`: length-framed typed protocol.
- Create `tools/managed_browser_helper/main.py`: handshake, health, read-only account identity observation, and shutdown only.
- Create `tools/managed_browser_helper/helper-manifest.json`: packaged file/version/hash manifest source.
- Create `tools/managed_browser_helper/THIRD_PARTY_NOTICES.txt`: bundled runtime and dependency licenses.
- Create `scripts/build_managed_browser_helper.ps1`: reproducible self-contained helper packaging and manifest generation.

---

### Task 1: Add engine-discriminated domain contracts

**Files:**
- Create: `src/BaiduBatchTransfer.Core/Domain/BrowserFamily.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/BrowserInstallationIdentity.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/ManagedBrowserSessionReference.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/BaiduExecutionContext.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/ExecutionProfileSnapshot.cs`
- Create: `src/BaiduBatchTransfer.Core/Abstractions/IManagedBrowserStateStore.cs`
- Test: `tests/BaiduBatchTransfer.Core.Tests/Domain/ManagedBrowserContractsTests.cs`

- [ ] **Step 1: Write failing construction and redaction tests**

Cover valid legacy/Chrome/Edge snapshots, valid Chrome/Edge installation identities, empty GUID/UID/profile rejection, canonical absolute path requirement, legacy/managed context exclusivity, and `ToString()` redaction.

```csharp
[Fact]
public void ManagedContextCarriesReferenceWithoutCookies()
{
    var reference = new ManagedBrowserSessionReference(
        SessionId, InstallationId, AccountId, "stable-uid",
        BrowserFamily.Chrome, ProfileId, OwnershipId);

    var context = BaiduExecutionContext.Managed(reference);

    Assert.Equal(BaiduExecutionEngine.ManagedChromiumV1, context.Engine);
    Assert.Null(context.LegacySession);
    Assert.Same(reference, context.ManagedSession);
    Assert.DoesNotContain("stable-uid", context.ToString(), StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run the focused test and verify RED**

Run: `dotnet test tests/BaiduBatchTransfer.Core.Tests/BaiduBatchTransfer.Core.Tests.csproj --filter FullyQualifiedName~ManagedBrowserContractsTests`

Expected: FAIL because the new contracts do not exist.

- [ ] **Step 3: Implement the minimal immutable contracts**

Use closed enums:

```csharp
public enum BrowserFamily { LegacyWebView2 = 0, Chrome = 1, Edge = 2 }
public enum BaiduExecutionEngine { WebView2V1 = 1, ManagedChromiumV1 = 2 }
```

`BrowserInstallationIdentity` carries immutable `InstallationId` and rejects `LegacyWebView2`; that enum member exists only so old operation/attempt snapshots remain explicit and non-null. `ManagedBrowserSessionReference` carries immutable `SessionId` and `InstallationId`. `ExecutionProfileSnapshot` validates the legal engine/family combination. `BaiduExecutionContext` must have private construction and exactly one of `AccountSession` or `ManagedBrowserSessionReference`. All public `ToString()` methods redact paths, UID, profile, and browser file identity.

- [ ] **Step 4: Run focused and Core tests**

Run: `dotnet test tests/BaiduBatchTransfer.Core.Tests/BaiduBatchTransfer.Core.Tests.csproj --filter FullyQualifiedName~ManagedBrowserContractsTests`

Run: `dotnet test tests/BaiduBatchTransfer.Core.Tests/BaiduBatchTransfer.Core.Tests.csproj`

Expected: PASS; existing Core tests remain green.

- [ ] **Step 5: Commit**

```bash
git add src/BaiduBatchTransfer.Core tests/BaiduBatchTransfer.Core.Tests/Domain/ManagedBrowserContractsTests.cs
git commit -m "feat: add managed browser execution contracts"
```

### Task 2: Add the version-16 forward migration and SQLite stores

**Files:**
- Create: `src/BaiduBatchTransfer.Infrastructure/Database/Migrations/016_ManagedChromiumFoundation.sql`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/DatabaseMigrator.cs`
- Create: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteManagedBrowserStateStore.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/Repositories/SqliteJobRepository.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteTransferSchedulingStore.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteShareSchedulingStore.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteStandaloneShareStore.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteWebAutomationAttemptStore.cs`
- Modify: `src/BaiduBatchTransfer.Core/Domain/TransferOperation.cs`
- Modify: `src/BaiduBatchTransfer.Core/Abstractions/PersistenceModels.cs`
- Modify: `src/BaiduBatchTransfer.Core/Abstractions/StandaloneShareRecords.cs`
- Modify: `src/BaiduBatchTransfer.Core/Abstractions/WebAutomationAttemptRecord.cs`
- Modify: `src/BaiduBatchTransfer.Application/Jobs/CreateBatchService.cs`
- Modify: `src/BaiduBatchTransfer.Application/Jobs/CreateStandaloneShareBatchService.cs`
- Modify: `src/BaiduBatchTransfer.Application/Scheduling/OperationReconciliationService.cs`
- Modify: `src/BaiduBatchTransfer.Application/Scheduling/ShareOperationReconciliationService.cs`
- Modify: `src/BaiduBatchTransfer.Application/Scheduling/ShareOperationExecutor.cs`
- Modify: `src/BaiduBatchTransfer.Application/Scheduling/StandaloneShareOperationExecutor.cs`
- Modify: `src/BaiduBatchTransfer.Application/Scheduling/RetryPolicy.cs`
- Modify: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/MigrationTests.cs`
- Create: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/ManagedBrowserStateStoreTests.cs`
- Modify: scheduling-store tests under `tests/BaiduBatchTransfer.Infrastructure.Tests/Database`
- Modify: retry/reconciliation tests under `tests/BaiduBatchTransfer.Application.Tests/Scheduling`

- [ ] **Step 1: Write a RED upgrade-shape test**

Create version-15 fixtures containing terminal and non-terminal rows in `TransferOperations`, `ShareOperations`, `StandaloneShareOperations`, and `WebAutomationAttempts`. Assert upgrade 16 adds non-null `ExecutionEngineVersion`, `BrowserFamily`, and `ProfileId` to every row and preserves all prior values, constraints, triggers, indexes, and foreign keys.

- [ ] **Step 2: Run the new migration test and verify RED**

Run: `dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj --filter "FullyQualifiedName~MigrationTests.VersionFifteen"`

Expected: FAIL because migration 16 is missing.

- [ ] **Step 3: Add the installation/session tables**

Create:

```sql
CREATE TABLE BrowserInstallations (
    InstallationId TEXT PRIMARY KEY,
    BrowserFamily TEXT NOT NULL CHECK(BrowserFamily IN ('Chrome','Edge')),
    InstallationDirectory TEXT NOT NULL,
    ExecutablePath TEXT NOT NULL,
    Publisher TEXT NOT NULL,
    ProductName TEXT NOT NULL,
    FileIdentity TEXT NOT NULL UNIQUE,
    State TEXT NOT NULL CHECK(State IN ('Active','Retired')),
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);

CREATE TABLE ManagedBrowserSessions (
    SessionId TEXT PRIMARY KEY,
    AccountId TEXT NOT NULL REFERENCES Accounts(Id),
    BaiduUserId TEXT NOT NULL,
    InstallationId TEXT NOT NULL REFERENCES BrowserInstallations(InstallationId),
    BrowserFamily TEXT NOT NULL CHECK(BrowserFamily IN ('Chrome','Edge')),
    ProfileId TEXT NOT NULL,
    ProfileOwnershipId TEXT NOT NULL,
    State TEXT NOT NULL CHECK(State IN ('Active','Retired','QuarantinePending','Quarantined','CleanupPending','Deleted')),
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    UNIQUE(BrowserFamily, ProfileId)
);

CREATE UNIQUE INDEX UX_BrowserInstallations_OneActive
ON BrowserInstallations(State) WHERE State='Active';

CREATE UNIQUE INDEX UX_ManagedBrowserSessions_ActiveAccountFamily
ON ManagedBrowserSessions(AccountId, BrowserFamily) WHERE State='Active';

CREATE TABLE ManagedProfileCleanupManifests (
    ManifestId TEXT PRIMARY KEY,
    AccountId TEXT NOT NULL REFERENCES Accounts(Id),
    SessionId TEXT NOT NULL REFERENCES ManagedBrowserSessions(SessionId),
    State TEXT NOT NULL CHECK(State IN ('Prepared','QuarantineMoveIntent','Quarantined','DeleteIntent','CleanupPending','Deleted')),
    SourceProfilePath TEXT NOT NULL,
    QuarantinePath TEXT NOT NULL,
    ExpectedOwnershipId TEXT NOT NULL,
    ConfirmationTokenHash TEXT NULL,
    Revision INTEGER NOT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);
```

Directory reselect inserts a new `BrowserInstallations` row and retires the old row; family/profile switching inserts a new session row and retires the old row. Never overwrite either identity. Managed operations/attempts additionally store immutable `InstallationId` and `SessionId` foreign keys; legacy rows store null for those two references under a tuple `CHECK`. Retired identities remain until no operation/attempt references them and the durable cleanup manifest has reached `Deleted`.

Run the focused test and confirm it still fails only on missing operation snapshots.

- [ ] **Step 4: Rebuild and test `TransferOperations`**

Inside the migration transaction, create a replacement table with temporarily nullable snapshot columns, copy every terminal/non-terminal row, backfill `WebView2-v1`, `LegacyWebView2`, and its existing account-specific WebView2 profile identity, validate, then rebuild with non-null snapshot fields and tuple checks. Add managed-only nullable `InstallationId`/`SessionId` foreign keys. Run the transfer fixture and expect PASS.

- [ ] **Step 5: Rebuild and test `ShareOperations`**

Repeat the staged copy/backfill/validate/rebuild for `ShareOperations`, preserving all share baselines, mutation evidence, generations, triggers, and indexes. Run the share fixture and expect PASS.

- [ ] **Step 6: Rebuild and test `StandaloneShareOperations`**

Repeat for standalone shares, preserving every lease, output ordinal, evidence field, trigger, and index. Run the standalone fixture and expect PASS.

- [ ] **Step 7: Rebuild and test `WebAutomationAttempts`**

Repeat for attempts, inheriting the exact parent operation snapshot; reject an attempt whose tuple differs from its operation. Never use a generic constant profile shared by accounts. Run the attempt fixture and expect PASS.

- [ ] **Step 8: Add rollback RED tests**

Add corrupt-fixture cases for an unknown engine/family, missing legacy profile identity, duplicate managed profile, and a forced copy failure. Assert the schema version stays 15 and the original tables/rows remain unchanged.

Run: `dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj --filter "FullyQualifiedName~MigrationTests.VersionSixteen"`

Expected: PASS for valid fixtures and PASS for asserted rollback behavior.

- [ ] **Step 9: Write managed-state store RED tests**

Cover save/load/reselect installation without overwriting the retired identity, session UID binding, family switch without overwriting the retired session/profile identity, browser-family/profile mismatch, compare-and-swap activation/retirement, tombstoned-account lookup rejection, retention while any operation/attempt references an identity, no secret/path leakage in exceptions, and cancellation.

- [ ] **Step 10: Implement the SQLite store with exact enum parsing**

Use parameters for every value. Reject unknown browser/engine strings and corrupted rows; never silently downgrade to WebView2.

Run: `dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj --filter FullyQualifiedName~ManagedBrowserStateStoreTests`

Expected: PASS.

- [ ] **Step 11: Write operation round-trip and retry-fence RED tests**

For all five persistence classes listed above, assert insertion and reading preserve the immutable snapshot. In Application tests assert a new logical operation receives the current default snapshot, a new attempt inherits its operation snapshot, a started/recovering operation cannot be reselected, and a retry may change engine only when the prior generation is `DefinitelyNotSubmitted` with no source evidence, directory/share mutation, `Submitting`, or `ResultPending` state.

- [ ] **Step 12: Update operation insert/read paths**

Thread `ExecutionProfileSnapshot` through `CreateBatchService`, `CreateStandaloneShareBatchService`, all five SQLite persistence classes, and lease reads. Run each store round-trip test and expect PASS.

- [ ] **Step 13: Update reconciliation and retry fencing**

Thread the snapshot through attempt creation, reconciliation, and retry generation. Enforce the cross-engine rule from Step 11. This task persists and enforces the snapshot only; it does not select the managed adapter for production execution.

- [ ] **Step 14: Run migration/store/scheduling tests**

Run: `dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj --filter "FullyQualifiedName~MigrationTests.VersionSixteen|FullyQualifiedName~ManagedBrowserStateStoreTests|FullyQualifiedName~RepositoryTests|FullyQualifiedName~ShareSchedulingStoreTests|FullyQualifiedName~StandaloneShareStoreTests|FullyQualifiedName~WebAutomationAttemptStoreTests"`

Run: `dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj --filter "FullyQualifiedName~SqliteSchedulingStoreTests|FullyQualifiedName~Retry|FullyQualifiedName~Reconciliation|FullyQualifiedName~CreateBatchServiceTests|FullyQualifiedName~CreateStandaloneShareBatchServiceTests"`

Expected: PASS.

- [ ] **Step 15: Commit**

```bash
git add src/BaiduBatchTransfer.Core/Domain/TransferOperation.cs src/BaiduBatchTransfer.Core/Domain/ExecutionProfileSnapshot.cs src/BaiduBatchTransfer.Core/Abstractions/PersistenceModels.cs src/BaiduBatchTransfer.Core/Abstractions/StandaloneShareRecords.cs src/BaiduBatchTransfer.Core/Abstractions/WebAutomationAttemptRecord.cs src/BaiduBatchTransfer.Application/Jobs src/BaiduBatchTransfer.Application/Scheduling src/BaiduBatchTransfer.Infrastructure/Database tests/BaiduBatchTransfer.Infrastructure.Tests/Database tests/BaiduBatchTransfer.Application.Tests/Scheduling tests/BaiduBatchTransfer.Application.Tests/Jobs
git commit -m "feat: persist managed browser state"
```

### Task 3: Add direct browser installation-directory selection

**Files:**
- Create: `src/BaiduBatchTransfer.App/Features/ManagedBrowser/BrowserDirectoryDialog.cs`
- Create: `src/BaiduBatchTransfer.App/Features/ManagedBrowser/BrowserSetupService.cs`
- Modify: `src/BaiduBatchTransfer.App/Services/DialogService.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/BrowserSetupServiceTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Services/DialogServiceTests.cs`

- [ ] **Step 1: Write RED tests for the user-directed flow**

Cover first-use prompt, cancellation, saved-directory reuse, invalidated saved directory, explicit reselect, zero allowed executables, and both executables in one directory. Assert no filesystem enumeration occurs outside the selected directory.

```csharp
[Fact]
public async Task FirstUsePromptsInsteadOfScanning()
{
    var dialog = new RecordingBrowserDirectoryDialog(@"C:\Browser\Application");
    var fileSystem = new RecordingBrowserDirectoryFileSystem();
    fileSystem.AddDirectChild(@"C:\Browser\Application\chrome.exe");

    await CreateService(dialog, fileSystem).EnsureSelectedAsync(default);

    Assert.Equal(1, dialog.ShowCount);
    Assert.Equal([@"C:\Browser\Application"], fileSystem.EnumeratedDirectories);
}
```

- [ ] **Step 2: Run focused tests and verify RED**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter "FullyQualifiedName~BrowserSetupServiceTests|FullyQualifiedName~DialogServiceTests"`

Expected: FAIL because browser directory selection is absent.

- [ ] **Step 3: Implement the folder dialog and coordinator**

Use `Microsoft.Win32.OpenFolderDialog` on the UI dispatcher. The selected directory may only be inspected for direct children named `chrome.exe` or `msedge.exe`; do not call `Directory.EnumerateFiles` recursively and do not fall back to fixed installation paths.

- [ ] **Step 4: Run focused tests**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter "FullyQualifiedName~BrowserSetupServiceTests|FullyQualifiedName~DialogServiceTests"`

Expected: PASS, including cancellation without side effects.

- [ ] **Step 5: Commit**

```bash
git add src/BaiduBatchTransfer.App/Features/ManagedBrowser src/BaiduBatchTransfer.App/Services/DialogService.cs tests/BaiduBatchTransfer.App.Tests
git commit -m "feat: let users select browser directory"
```

### Task 4: Verify Chrome/Edge executable authenticity and replacement

**Files:**
- Create: `src/BaiduBatchTransfer.Infrastructure/Security/BrowserExecutableVerifier.cs`
- Create: `tests/BaiduBatchTransfer.Infrastructure.Tests/Security/BrowserExecutableVerifierTests.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/ManagedBrowser/BrowserSetupService.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/BrowserSetupServiceTests.cs`

- [ ] **Step 1: Write filename/family/publisher policy RED tests**

Use an injected signature/file-identity reader. Cover allowed filename/family/publisher/product pairs, unsigned files, wrong publisher, and wrong product.

- [ ] **Step 2: Run focused tests and verify RED**

Run: `dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj --filter FullyQualifiedName~BrowserExecutableVerifierTests`

Expected: FAIL.

- [ ] **Step 3: Implement the pure identity policy and make it green**

Allow only the following exact family/name/product/publisher combinations and return typed safe error codes:

```text
chrome.exe  -> Chrome -> Google LLC -> Google Chrome
msedge.exe  -> Edge   -> Microsoft Corporation -> Microsoft Edge
```

- [ ] **Step 4: Write pinned-path RED tests**

Cover selected-directory reparse points, executable reparse points, an executable whose final parent is not the selected directory, directory/file replacement between checks, and identity mismatch on a later launch. Tests must use injected native handle operations and prove the verifier never falls back to scanning another directory.

- [ ] **Step 5: Implement pinned Windows verification**

Open and retain handles for both the selected directory and direct-child executable, reject any reparse point, resolve both final paths from handles, and prove the executable final parent equals the selected directory final path. Call `WinVerifyTrust`, inspect the signed file certificate and `FileVersionInfo.ProductName`, and compute a bounded file identity from volume/file ID plus SHA-256. Persist only after all checks pass; re-run the complete check immediately before every process launch and compare against the saved identity.

Return a typed safe error code; never include the selected path in logs or `ToString()`.

- [ ] **Step 6: Run policy tests and Windows integration test**

The integration test may verify the already-installed signed Chrome/Edge binaries read-only and must skip with an explicit reason when neither exists.

Run: `dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj --filter FullyQualifiedName~BrowserExecutableVerifierTests`

Expected: PASS; the integration case either passes for an installed supported browser or reports an explicit skip.

- [ ] **Step 7: Commit**

```bash
git add src/BaiduBatchTransfer.Infrastructure/Security tests/BaiduBatchTransfer.Infrastructure.Tests/Security src/BaiduBatchTransfer.App/Features/ManagedBrowser tests/BaiduBatchTransfer.App.Tests/Features
git commit -m "feat: verify selected browser identity"
```

### Task 5: Add per-account managed profile ownership

**Files:**
- Create: `src/BaiduBatchTransfer.Infrastructure/Security/FileSystemManagedBrowserProfilePolicy.cs`
- Create: `tests/BaiduBatchTransfer.Infrastructure.Tests/Security/ManagedBrowserProfilePolicyTests.cs`
- Modify: `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs`

- [ ] **Step 1: Write RED tests**

Cover opaque account GUID paths, distinct Chrome/Edge roots, ownership marker creation, current-user-only ACL, reparse-point rejection, damaged marker isolation, and no automatic deletion.

- [ ] **Step 2: Run focused tests and verify RED**

Run: `dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj --filter FullyQualifiedName~ManagedBrowserProfilePolicyTests`

Expected: FAIL because the managed profile policy does not exist.

- [ ] **Step 3: Implement by reusing the proven profile filesystem primitives**

Reuse `IProfilePathFileSystem`/ownership patterns from `FileSystemAccountProfilePathPolicy` and `FileSystemWebViewProfileStore`; do not copy unsafe path logic. The canonical layout is:

```text
<AppDataRoot>/managed-browser-profiles/<browser-family>/<account-guid>/
```

- [ ] **Step 4: Run focused and existing profile-security tests**

Run: `dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj --filter "FullyQualifiedName~ManagedBrowserProfilePolicyTests|FullyQualifiedName~ProfilePathSecurityTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/BaiduBatchTransfer.Infrastructure/Security tests/BaiduBatchTransfer.Infrastructure.Tests/Security src/BaiduBatchTransfer.App/AppBootstrap.cs tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs
git commit -m "feat: isolate managed browser profiles"
```

### Task 6: Define crash-consistent managed-profile lifecycle state

**Files:**
- Create: `src/BaiduBatchTransfer.Core/Abstractions/IManagedBrowserLifecycle.cs`
- Create: `src/BaiduBatchTransfer.Core/Abstractions/IManagedBrowserProfileStore.cs`
- Modify: `src/BaiduBatchTransfer.Application/Accounts/AccountRemovalContracts.cs`
- Modify: `src/BaiduBatchTransfer.Application/Accounts/AccountRemovalCoordinator.cs`
- Modify: `src/BaiduBatchTransfer.Application/Accounts/RemoveAccountService.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteAccountRemovalStore.cs`
- Create: `src/BaiduBatchTransfer.Infrastructure/Security/FileSystemManagedBrowserProfileStore.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Accounts/AccountManagementViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Accounts/AccountManagementWindow.xaml`
- Modify: `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- Modify: `tests/BaiduBatchTransfer.Application.Tests/Accounts/AccountRemovalCoordinatorTests.cs`
- Modify: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/AccountRemovalStoreTests.cs`
- Create: `tests/BaiduBatchTransfer.Application.Tests/Accounts/ManagedBrowserAccountRemovalTests.cs`
- Create: `tests/BaiduBatchTransfer.Infrastructure.Tests/Security/ManagedBrowserProfileStoreTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/AccountManagementViewModelTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs`
- Create: `src/BaiduBatchTransfer.App/Features/ManagedBrowser/NullManagedBrowserLifecycle.cs`

- [ ] **Step 1: Write RED tests for removal preparation**

Using a fake `IManagedBrowserLifecycle`, assert preparation refuses removal while a managed browser cannot be stopped or any transfer/share/standalone-share operation for the account is in flight. Assert it snapshots the exact SessionId, InstallationId, family, ProfileId, ownership ID, source path, and deterministic quarantine path before tombstoning.

Run: `dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj --filter FullyQualifiedName~ManagedBrowserAccountRemovalTests`

Expected: FAIL because managed profile state is not part of removal.

- [ ] **Step 2: Add a compiling interim lifecycle and durable intent**

Add an optional managed-profile cleanup entry to the existing removal preparation/manifest. Register `NullManagedBrowserLifecycle`, which reports no managed session and changes no existing behavior until Task 11 wires the real manager. For a managed-session fixture, preparation asks the injected lifecycle to stop the exact session, fences new leases, proves no in-flight operation remains, retires (does not overwrite/delete) the session reference, and durably writes `QuarantineMoveIntent` with source/destination identities before any filesystem move. Run the focused Application test and expect PASS.

- [ ] **Step 3: Write RED tests for confirmation and restart recovery**

Cover explicit user confirmation, cancellation, occupied profile, corrupt ownership marker, app restart before the move, crash after the move but before database CAS, both source/destination present, neither present, repeated confirmation, and a failed delete that stays `CleanupPending`. Assert no path outside the managed-profile root is touched.

- [ ] **Step 4: Implement the concrete pinned profile store**

`FileSystemManagedBrowserProfileStore` implements `IManagedBrowserProfileStore` using pinned source/root handles and the policy from Task 5. It validates the ownership marker before and after a same-volume rename, exposes typed observations for source/destination existence, and deletes only an exact quarantined path after a confirmation-bound call. Register it in production DI while leaving `NullManagedBrowserLifecycle` as the process-lifecycle implementation until Task 11.

Run: `dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj --filter FullyQualifiedName~ManagedBrowserProfileStoreTests`

Expected: PASS.

- [ ] **Step 5: Implement intent-before-move and compare-and-swap completion**

After `QuarantineMoveIntent` commits, rename the exact source directory to the exact same-volume app-owned quarantine path, verify its ownership marker, then compare-and-swap the manifest to `Quarantined`. Add tests for the clean path and the crash after rename/before CAS.

- [ ] **Step 6: Implement deterministic restart recovery**

Recovery rules are explicit: source exists/destination absent => retry the move; source absent/destination exists with matching marker => finish the CAS; both exist or neither exists => `CleanupPending` for manual review. Run `ManagedBrowserAccountRemovalTests`; expected: all crash-boundary cases PASS.

- [ ] **Step 7: Implement confirmed deletion UI and state transitions**

Only an explicit confirmation token for the exact `Quarantined` manifest may compare-and-swap to `DeleteIntent` and delete it; a final CAS records `Deleted`. Surface that confirmation in the existing account-management window with the account display name and a warning that the dedicated browser login data will be deleted. A tombstoned account stays disabled and credential-free even if cleanup is pending. Run the account-management tests and expect PASS.

- [ ] **Step 8: Write RED browser-family switch tests**

Switching Chrome to Edge (or vice versa) creates a fresh family-specific profile and requires a new stable-UID login. It must not reuse or delete the old profile automatically. Browser-directory reselect updates only future launches; started/recovering operation snapshots keep their family/ProfileId.

- [ ] **Step 9: Implement family switch fencing**

At this task boundary, implement the state transition against an injected fake identity observation: create a new immutable installation/session row only after its stable UID matches. Retire, but do not overwrite or delete, the old reference/profile; it remains available to pinned work and later quarantine. Reject switching any started/recovering operation. Task 11 wires this already-tested transition to the real observer. Run the three focused commands below.

Run: `dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj --filter "FullyQualifiedName~ManagedBrowserAccountRemovalTests|FullyQualifiedName~AccountRemovalCoordinatorTests"`

Run: `dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj --filter FullyQualifiedName~AccountRemovalStoreTests`

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter FullyQualifiedName~AccountManagementViewModelTests`

Expected: PASS.

- [ ] **Step 10: Commit**

```bash
git add src/BaiduBatchTransfer.Core/Abstractions src/BaiduBatchTransfer.Application/Accounts src/BaiduBatchTransfer.Infrastructure/Database/SqliteAccountRemovalStore.cs src/BaiduBatchTransfer.Infrastructure/Security/FileSystemManagedBrowserProfileStore.cs src/BaiduBatchTransfer.App/AppBootstrap.cs src/BaiduBatchTransfer.App/Features/Accounts src/BaiduBatchTransfer.App/Features/ManagedBrowser/NullManagedBrowserLifecycle.cs tests/BaiduBatchTransfer.Application.Tests/Accounts tests/BaiduBatchTransfer.Infrastructure.Tests/Database/AccountRemovalStoreTests.cs tests/BaiduBatchTransfer.Infrastructure.Tests/Security/ManagedBrowserProfileStoreTests.cs tests/BaiduBatchTransfer.App.Tests/Features/AccountManagementViewModelTests.cs tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs
git commit -m "feat: manage local browser profile removal"
```

### Task 7: Define the fixed helper protocol and minimal helper

**Files:**
- Create: `src/BaiduBatchTransfer.App/Features/ManagedBrowser/ManagedBrowserProtocol.cs`
- Create: `tools/managed_browser_helper/protocol.py`
- Create: `tools/managed_browser_helper/main.py`
- Create: `tools/managed_browser_helper/requirements.lock`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/ManagedBrowserProtocolTests.cs`
- Create: `tools/managed_browser_helper/tests/test_protocol.py`

- [ ] **Step 1: Write C# and Python RED framing tests**

Cover version mismatch, unknown fields/actions, oversized frames, malformed UTF-8/JSON, and secret-like keys (`cookie`, `authorization`, `extractionCode`) rejection.

- [ ] **Step 2: Run both test suites and verify RED**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter FullyQualifiedName~ManagedBrowserProtocolTests`

Run: `python -m unittest discover -s tools/managed_browser_helper/tests -v`

- [ ] **Step 3: Implement framing plus handshake/health/shutdown schemas**

Use a four-byte big-endian payload length followed by UTF-8 JSON. Every message includes:

```json
{"protocolVersion":1,"executionEngineVersion":"ManagedChromium-v1","correlationId":"...","operationId":"...","accountId":"...","generation":1,"action":"Health","payload":{}}
```

Do not add a generic JavaScript field.

- [ ] **Step 4: Run both framing test suites**

Expected: PASS.

- [ ] **Step 5: Write RED identity request/result schema tests**

Add exactly one additional read-only action, `ObserveAccountIdentity`, whose input is a verified `BrowserId + TargetId` binding and whose helper result is one of `Observed(stableBaiduUserId)`, `LoginRequired`, `BrowserUnavailable`, `TimedOut`, or `Cancelled`. The expected UID never enters the helper; C# compares `Observed` with the account record and alone produces `AccountIdentityMismatch`. Assert empty TargetId and page text/URL/Cookie fields in the response are rejected, and UID is redacted from logs/`ToString()`.

- [ ] **Step 6: Implement the identity schema and make contract tests green**

Run both commands from Step 2; expected: PASS for framing and typed result serialization without any page access.

- [ ] **Step 7: Write RED route/target validation tests**

Cover the only allowed top-level route state machine: start at `https://pan.baidu.com/`; optionally transition to exact `https://passport.baidu.com/` for manual authentication; return to `https://pan.baidu.com/` before identity observation. Reject HTTP, userinfo, non-default ports, wildcard/subdomain lookalikes, every other host, observing identity on the authentication host, changed BrowserId/TargetId, disconnected endpoint, and timeout/cancellation while waiting for login.

- [ ] **Step 8: Implement route validation and endpoint attachment**

The C# process manager passes the already verified loopback DevTools endpoint and exact target binding to the helper over the inherited private stdio channel. The helper attaches only to that target, enforces the state machine above before reading, waits with a bounded timeout and cancellation, and returns only the typed result. It may navigate the bound target to `https://pan.baidu.com/`; the user performs authentication manually. It cannot click, submit, upload, transfer, share, or evaluate caller-supplied JavaScript.

- [ ] **Step 9: Write RED DOM-to-result mapping tests**

Use local static HTML fixtures for logged-out, logged-in stable UID, missing UID, and changed account. The helper may query only fixed selectors/DOM properties compiled into its code. It must return a typed result and no HTML.

- [ ] **Step 10: Implement the minimal fixed identity observer**

Implement only the selectors needed by the fixtures, then run the Python suite. Expected: PASS and no network access.

- [ ] **Step 11: Run identity and full protocol tests**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter FullyQualifiedName~ManagedBrowserProtocolTests`

Run: `python -m unittest discover -s tools/managed_browser_helper/tests -v`

Expected: PASS.

- [ ] **Step 12: Commit**

```bash
git add src/BaiduBatchTransfer.App/Features/ManagedBrowser/ManagedBrowserProtocol.cs tools/managed_browser_helper tests/BaiduBatchTransfer.App.Tests/Features/ManagedBrowserProtocolTests.cs
git commit -m "feat: define managed browser helper protocol"
```

### Task 8: Package and verify the helper before every launch

**Files:**
- Create: `scripts/build_managed_browser_helper.ps1`
- Create: `tools/managed_browser_helper/helper-manifest.json`
- Create: `tools/managed_browser_helper/THIRD_PARTY_NOTICES.txt`
- Create: `src/BaiduBatchTransfer.App/Features/ManagedBrowser/ManagedBrowserHelperIntegrityVerifier.cs`
- Modify: `src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/ManagedBrowserHelperIntegrityVerifierTests.cs`

- [x] **Step 1: Write manifest parser RED tests**

Cover missing file, altered byte, extra unlisted executable, old protocol/runtime version, path escape, duplicate manifest path, absent license notice, and a valid fixture. Tests must prove launch is not attempted after verification failure.

- [x] **Step 2: Run focused tests and verify RED**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter FullyQualifiedName~ManagedBrowserHelperIntegrityVerifierTests`

Expected: FAIL because the manifest verifier does not exist.

- [x] **Step 3: Implement the runtime manifest verifier**

Embed the expected manifest bytes in the signed .NET application, reject unexpected executable/library files, and verify every listed file's length and SHA-256 before process creation. Run the focused test and expect PASS.

- [x] **Step 4: Add the exact locked helper build inputs**

Use the official CPython embeddable archive and fail on any digest mismatch:

```text
https://www.python.org/ftp/python/3.14.0/python-3.14.0-embed-amd64.zip
sha256:8d4d3590c10449d78aa4375f534e6d5f3027d67fdc362dd1a882279db6f90fdf
```

The provided reference contains DrissionPage 4.1.1.2, but that wheel is not available from the configured package index. Pin the closest published patch, DrissionPage 4.1.1.4, and put this complete Windows x64/Python 3.14 lock in `requirements.lock`:

```text
DrissionPage==4.1.1.4 --hash=sha256:d1d066e732031c16b00bdc88a2f450e8400da46362597a23c8c6144e4cbb768d
certifi==2026.7.22 --hash=sha256:62f22742b58a1a33014a2b6b706588a8d7e2a88ae7bd1a6ebe8c992928483775
charset-normalizer==3.5.1 --hash=sha256:c658c50ac0c98cd755a2dd50b7977d3bca7df401dcc47fbdfa87db53ef7d4e8b
click==8.5.0 --hash=sha256:255bc9599cf7748b4b1a446ccc735421bd08a2ae529a8b88597d3de5664ee360
cssselect==1.5.0 --hash=sha256:1d1aded98e82bdde447ded990a191fd6916177c4f0c914fb62eccd58e2ffcdcc
DrissionGet==1.2.1 --hash=sha256:cd0ae08c26e6a7f5bd105ca787d79854569c8ebfeb67f978b86aee7d2b983a29
DrissionRecord==2.0.1 --hash=sha256:b6efb09268ca2b5fb5e2942d3bfb75d70fe6c936770aaac02fca16e6960fe9ee
et-xmlfile==2.0.0 --hash=sha256:7a91720bc756843502c3b7504c77b8fe44217c85c537d85037f0f536151b2caa
filelock==3.32.4 --hash=sha256:22e58ca3b1ae3b98993b762d7338367ae64fe50252bf78d59da3bfebcdf1cedd
idna==3.19 --hash=sha256:815e7be7a7806d54abb586dc943addc79e8b2ee16915059658cbeff4b1b43bf4
lxml==6.1.2 --hash=sha256:87e9673cd8a3445024fe38e7f91b55fa3428437eec9b7a7ff7d81979520c0d2d
openpyxl==3.1.5 --hash=sha256:5282c12b107bffeef825f4617dc029afaf41d0ea60823bbb665ef3079dc79de2
psutil==7.2.2 --hash=sha256:eb7e81434c8d223ec4a219b5fc1c47d0417b12be7ea866e24fb5ad6e84b3d988
requests-file==3.0.1 --hash=sha256:d0f5eb94353986d998f80ac63c7f146a307728be051d4d1cd390dbdb59c10fa2
requests==2.34.2 --hash=sha256:2a0d60c172f83ac6ab31e4554906c0f3b3588d37b5cb939b1c061f4907e278e0
tldextract==5.3.2 --hash=sha256:6c90d2a259f5c89f4fcf01f97af15708416a59ffedddff006d67222ab30d0fb0
urllib3==2.7.0 --hash=sha256:9fb4c81ebbb1ce9531cce37674bbc6f1360472bc18ca9a553ede278ef7276897
websocket-client==1.9.0 --hash=sha256:af248a825037ef591efbf6ed20cc5faa03d3b47b9e5a2230a529eeee1c1fc3ef
```

`THIRD_PARTY_NOTICES.txt` must include the complete license texts and this inventory: CPython/PSF-2.0; DrissionPage/custom personal and legal non-profit license; certifi/MPL-2.0; charset-normalizer, DrissionRecord, et-xmlfile, filelock, openpyxl, urllib3/MIT; click, cssselect, idna, lxml, psutil, tldextract/BSD-3-Clause; DrissionGet/BSD; requests-file, requests, websocket-client/Apache-2.0. Because DrissionPage's bundled license prohibits commercial use without authorization, the build must require an explicit `-DistributionMode PersonalNonCommercial`; any commercial/distributable build is blocked until written authorization is recorded or the helper library is replaced.

- [x] **Step 5: Implement self-contained packaging**

The build script extracts the pinned CPython embeddable runtime into an isolated staging directory, installs only the hashed locked wheels into its private `site-packages`, copies the helper source/notices, and removes caches/tests. The external `helper-manifest.json` is UTF-8 without BOM with LF endings and contains `manifestVersion`, `protocolVersion`, `executionEngineVersion`, `pythonVersion`, `distributionMode`, followed by every other direct-child/descendant regular file as a forward-slash relative path, byte length, and lowercase SHA-256 sorted by ordinal path. The manifest explicitly excludes itself. At runtime, first compare the external manifest bytes to the manifest embedded in the signed .NET executable; only then verify every listed payload file and reject absolute paths, `..`, duplicate case-insensitive paths, reparse points, and unlisted files. The target computer must not need Python or pip.

- [x] **Step 6: Build twice in independent directories and compare**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build_managed_browser_helper.ps1 -DistributionMode PersonalNonCommercial -OutputDirectory artifacts\helper-build-a`

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build_managed_browser_helper.ps1 -DistributionMode PersonalNonCommercial -OutputDirectory artifacts\helper-build-b`

Run: `Get-FileHash artifacts\helper-build-a\helper-manifest.json; Get-FileHash artifacts\helper-build-b\helper-manifest.json`

Expected: the two manifest payloads are byte-identical for identical inputs.

- [x] **Step 7: Run integrity tests and commit**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter FullyQualifiedName~ManagedBrowserHelperIntegrityVerifierTests`

Expected: PASS.

```bash
git add scripts/build_managed_browser_helper.ps1 tools/managed_browser_helper src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj src/BaiduBatchTransfer.App/Features/ManagedBrowser tests/BaiduBatchTransfer.App.Tests/Features
git commit -m "build: package verified browser helper"
```

### Task 9: Implement the private helper client lifecycle

**Files:**
- Create: `src/BaiduBatchTransfer.App/Features/ManagedBrowser/ManagedBrowserHelperClient.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/ManagedBrowserHelperClientTests.cs`

- [x] **Step 1: Write RED tests with a fake child process**

Cover successful nonce/parent-PID/helper-PID/protocol/engine handshake, stderr safe event codes, unexpected stdout bytes, timeout, cancellation, child exit, late response, repeated correlation ID, and bounded graceful shutdown followed by kill.

- [x] **Step 2: Run focused tests and verify RED**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter FullyQualifiedName~ManagedBrowserHelperClientTests`

Expected: FAIL because the client does not exist.

- [x] **Step 3: Implement process bootstrap and handshake**

Use redirected stdin/stdout as the only protocol transport. Generate a 256-bit nonce per process and send it in the first length-framed message after the child is created; never put it in an environment variable, command line, named pipe, or file. The helper must echo it together with the expected parent PID, its actual helper PID, protocol version, and `ManagedChromium-v1`; reject any mismatch before accepting later commands. Never place URLs, Cookie values, extraction codes, UID, or page text in stderr.

- [x] **Step 4: Implement bounded request correlation and cancellation**

Permit one response for each live correlation ID, discard and safely report late/duplicate responses, propagate cancellation as a typed request cancellation, and on disposal attempt `Shutdown` before killing only the owned helper PID.

- [x] **Step 5: Run focused tests**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter FullyQualifiedName~ManagedBrowserHelperClientTests`

Expected: PASS.

- [x] **Step 6: Commit**

```bash
git add src/BaiduBatchTransfer.App/Features/ManagedBrowser/ManagedBrowserHelperClient.cs tests/BaiduBatchTransfer.App.Tests/Features/ManagedBrowserHelperClientTests.cs
git commit -m "feat: manage private browser helper lifecycle"
```

### Task 10: Launch and own a visible local Chromium process

**Files:**
- Create: `src/BaiduBatchTransfer.App/Features/ManagedBrowser/DevToolsActivePortReader.cs`
- Create: `src/BaiduBatchTransfer.App/Features/ManagedBrowser/WindowsTcpOwnerTable.cs`
- Create: `src/BaiduBatchTransfer.App/Features/ManagedBrowser/WindowsJobObject.cs`
- Create: `src/BaiduBatchTransfer.App/Features/ManagedBrowser/ManagedChromiumProcessManager.cs`
- Modify: `src/BaiduBatchTransfer.App/ApplicationShutdownCoordinator.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/DevToolsActivePortReaderTests.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/WindowsTcpOwnerTableTests.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/ManagedChromiumProcessManagerTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/ApplicationShutdownCoordinatorTests.cs`

- [ ] **Step 1: Write parser and lifecycle RED tests**

Cover truncated/oversized/stale `DevToolsActivePort` and non-loopback endpoints.

- [ ] **Step 2: Run focused tests and verify RED**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter "FullyQualifiedName~DevToolsActivePortReaderTests|FullyQualifiedName~ManagedChromiumProcessManagerTests"`

Expected: FAIL because the process manager does not exist.

- [ ] **Step 3: Implement the bounded port-file parser**

Read `DevToolsActivePort` only through a pinned handle beneath the exact app-owned profile, cap byte count and wait duration, reject stale timestamps/extra lines/path replacement, and accept loopback ports only. Run `DevToolsActivePortReaderTests` and expect PASS.

- [ ] **Step 4: Write owned-launch RED tests**

Cover profile mismatch, wrong/replaced executable identity, unowned-launch race, missing/ambiguous TCP listener owner, listener owned by an unrelated process, user-closed process, arbitrary-port reattach rejection, one process per account, restart/reconnect of the same account/family/profile, and shutdown affecting only app-owned processes.

- [ ] **Step 5: Implement race-free owned process launch**

Immediately before launch, repeat the complete pinned browser directory/executable verification from Task 4. Use Windows `CreateProcess` suspended, assign the root process to an app Job Object before resuming it, then generate an argument list (not a concatenated command string) containing the verified executable, managed `--user-data-dir`, literal `--remote-debugging-port=0` (Chromium chooses the free port), and `https://pan.baidu.com/`. Do not preselect or probe a port and do not hide the browser window. Record root PID, BrowserId, family, ProfileId, profile ownership ID, canonical profile path, and selected executable file identity.

- [ ] **Step 6: Implement TCP listener ownership validation**

`WindowsTcpOwnerTable` uses `GetExtendedTcpTable(TCP_TABLE_OWNER_PID_LISTENER)` to resolve the actual PID listening on the `DevToolsActivePort` loopback port. Require exactly one listener, prove that PID is the root/descendant in the recorded Job/process tree, and re-open that PID's executable to match the verified file identity. Add race tests in which the port owner exits or changes between lookup and attachment; fail closed.

- [ ] **Step 7: Implement endpoint and target ownership validation**

Do not treat `/json/version` as proof by itself. Before returning a session handle, require all of: loopback endpoint from the pinned profile's `DevToolsActivePort`; the listener-PID proof from Step 6; matching executable file identity and browser family; matching canonical profile path and ownership marker; expected BrowserId; and a selected TargetId whose current route is in the exact HTTPS allowlist. On restart, reconnect only when the same profile/ownership/identity tuple and listener ownership are proven; otherwise launch a fresh owned process. Never attach merely because a port is open.

- [ ] **Step 8: Integrate owned shutdown and run tests**

Register the manager with `ApplicationShutdownCoordinator`. Gracefully close helper/browser, then terminate only PIDs in the owned Job after a bounded timeout. Preserve unowned Chrome/Edge windows.

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter "FullyQualifiedName~WindowsTcpOwnerTableTests|FullyQualifiedName~ManagedChromiumProcessManagerTests|FullyQualifiedName~ApplicationShutdownCoordinatorTests"`

Expected: PASS.

- [ ] **Step 9: Perform a manual no-account smoke test**

The smoke test may open the app-owned Chrome/Edge window but must not log in, navigate to a share link, or execute any remote write.

- [ ] **Step 10: Commit**

```bash
git add src/BaiduBatchTransfer.App/Features/ManagedBrowser src/BaiduBatchTransfer.App/ApplicationShutdownCoordinator.cs tests/BaiduBatchTransfer.App.Tests/Features tests/BaiduBatchTransfer.App.Tests/ApplicationShutdownCoordinatorTests.cs
git commit -m "feat: launch app-owned local browser"
```

### Task 11: Add browser setup/login entry without switching adapters

**Files:**
- Create: `src/BaiduBatchTransfer.App/Features/ManagedBrowser/ManagedBrowserLoginLauncher.cs`
- Modify: `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- Modify: `src/BaiduBatchTransfer.App/Services/DialogService.cs`
- Modify: `src/BaiduBatchTransfer.App/Services/BaiduAccountLoginLauncher.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Accounts/AccountManagementViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Accounts/BaiduLoginViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Accounts/BaiduLoginWindow.xaml`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/ManagedBrowserLoginLauncherTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/BaiduLoginPolicyTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/AccountManagementViewModelTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Services/BaiduAccountLoginLauncherTests.cs`

- [ ] **Step 1: Write RED UI/application tests**

Cover first-use folder prompt, saved folder reuse, visible browser launch, login pending, stable UID binding, typed `BrowserUnavailable`/`LoginRequired`/`AccountIdentityMismatch` results, timeout/cancellation, browser closed, reselect command, and no Cookie extraction/storage call. Assert the active transfer/share adapter remains WebView2 during this foundation milestone.

- [ ] **Step 2: Run focused tests and verify RED**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter "FullyQualifiedName~ManagedBrowserLoginLauncherTests|FullyQualifiedName~BaiduLoginPolicyTests|FullyQualifiedName~AccountManagementViewModelTests|FullyQualifiedName~BaiduAccountLoginLauncherTests"`

Expected: FAIL because managed login and existing-account selection are absent.

- [ ] **Step 3: Implement explicit add-versus-existing-account requests**

Replace the ambiguous parameterless managed login call with a typed request: `AddNew(reservedAccountId)` reserves one new local ID for the add flow, while `ReloginExisting(accountId)` is emitted only from the selected row in `AccountManagementViewModel`. Validate the existing account is active and the observed stable UID equals its stored Baidu UID before updating its managed session. Never generate a new account ID when re-login or browser-family switching was requested.

- [ ] **Step 4: Implement the typed login-ready flow**

The UI should explain in simple Chinese:

```text
首次使用请选择包含 chrome.exe 或 msedge.exe 的安装目录。
浏览器打开后，请在专用窗口中登录百度网盘。
```

The launcher connects the helper only after `ManagedChromiumProcessManager` returns the verified endpoint/BrowserId/TargetId binding. It persists `ManagedBrowserSessionReference` only after a successful typed identity observation that matches the selected existing account, or after the add-account service creates the reserved account from the observed identity. It must never call `IBaiduSessionStore.SaveAsync` for the managed engine. Timeout, cancellation, login-required, browser-unavailable, and UID mismatch leave the old account/session binding unchanged and display only a safe Chinese result.

- [ ] **Step 5: Assert production adapters remain unchanged**

Add composition tests proving `IBaiduNetdiskAdapter` and `IBaiduShareAdapter` still resolve to the WebView2 path and all pre-existing operations use their persisted engine snapshot. The managed helper can perform only health and identity observation in this milestone.

- [ ] **Step 6: Wire the real lifecycle into removal and family switching**

Replace `NullManagedBrowserLifecycle` in production DI with the managed process/profile coordinator. Account removal now stops the exact owned browser before advancing the durable quarantine intent from Task 6. Existing-account browser-family switching uses the Task 6 retire/insert state machine and never changes any started/recovering operation snapshot. Add App composition assertions for both paths.

- [ ] **Step 7: Run focused tests and App composition tests**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter "FullyQualifiedName~ManagedBrowserLoginLauncherTests|FullyQualifiedName~BaiduLoginPolicyTests|FullyQualifiedName~AccountManagementViewModelTests|FullyQualifiedName~BaiduAccountLoginLauncherTests|FullyQualifiedName~AppBootstrapTests"`

Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/BaiduBatchTransfer.App tests/BaiduBatchTransfer.App.Tests
git commit -m "feat: add managed browser login setup"
```

### Task 12: Verify the foundation and document the next-plan boundary

**Files:**
- Create: `docs/testing/managed-browser-foundation-checklist.md`

- [ ] **Step 1: Run formatting and static checks**

Run: `git diff --check`

Run: `dotnet build BaiduBatchTransfer.sln --no-restore`

Expected: 0 warnings, 0 errors.

- [ ] **Step 2: Run relevant full suites serially**

Run:

```text
dotnet test tests/BaiduBatchTransfer.Core.Tests/BaiduBatchTransfer.Core.Tests.csproj --no-build
dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj --no-build
dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj --no-build
dotnet test tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduBatchTransfer.BaiduAdapter.Tests.csproj --no-build
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --no-build
```

Expected: all tests pass. If the known 200k-row performance test exceeds its existing bound, record it separately and rerun every changed test class; do not report the feature complete without the focused evidence.

- [ ] **Step 3: Perform the safe manual smoke test**

1. Start the app.
2. Choose the exact Chrome/Edge installation directory.
3. Confirm the visible app-owned browser opens with a new per-account profile.
4. With an explicitly selected test account, log in manually and confirm only the typed stable-UID observation is persisted; then restart and confirm the same account/family profile is reused.
5. Confirm cancellation, timeout, wrong-account, and reselect messages are safe and leave existing bindings unchanged.
6. Confirm closing the app closes only its helper/managed browser and preserves unrelated Chrome/Edge windows.
7. Do not open an imported share link or click transfer/share controls in this milestone.

- [ ] **Step 4: Record the handoff state**

Document the exact persisted schema, helper protocol version, browser identity rules, known residual same-user DevTools risk, and the explicit fact that production adapters still use WebView2 until the share-execution plan is completed.

- [ ] **Step 5: Request code review, address findings, and commit**

Use `superpowers:requesting-code-review`, rerun verification after every fix, then commit:

```bash
git add docs/testing/managed-browser-foundation-checklist.md
git commit -m "docs: verify managed browser foundation"
```

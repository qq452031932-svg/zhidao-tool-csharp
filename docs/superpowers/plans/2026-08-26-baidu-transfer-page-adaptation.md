# Baidu Transfer Page Adaptation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the visible WebView2 transfer path recognize the three confirmed Baidu pages, select or create the exact target directory, submit once, and recover by reading only the target directory.

**Architecture:** Extend the existing fail-closed candidate protocol instead of replacing it. Read operations use a separate typed, immutable request that is available to ordinary and recovery sessions; every input or click uses a fresh mutation permit bound to the exact account, operation, stage, Baidu UID, source IDs, and target ID. Persist complete source, target, baseline, submission, and directory-creation evidence so a crash can be resolved without reopening or unlocking the source link.

**Tech Stack:** .NET 8, C# 12, WPF, Microsoft WebView2, SQLite, xUnit.

---

## File structure

- `src/BaiduBatchTransfer.BaiduAdapter/WebAutomation/TransferAutomationContracts.cs`: focused typed source/directory/result proofs shared by the flow and WebView2 session.
- `src/BaiduBatchTransfer.BaiduAdapter/WebAutomation/TransferDirectoryPreparationFlow.cs`: segment-boundary path traversal and recoverable directory creation.
- `src/BaiduBatchTransfer.BaiduAdapter/WebAutomation/TransferAutomationFlow.cs`: prepare, submit-once, correlation, and read-only recovery orchestration.
- `src/BaiduBatchTransfer.App/Features/BrowserAutomation/BaiduCandidateDomProtocol.cs`: immutable read/mutation request DTOs and sanitized response DTOs.
- `src/BaiduBatchTransfer.App/Features/BrowserAutomation/BaiduTransferCandidateScripts.cs`: fixed built-in JavaScript for the confirmed transfer pages; callers cannot supply selectors or script text.
- `src/BaiduBatchTransfer.App/Features/BrowserAutomation/WebView2CandidateDomExecutor.cs`: serialize typed arguments and strictly parse the bounded script response.
- `src/BaiduBatchTransfer.App/Features/BrowserAutomation/WebView2AutomationSession.cs`: implement the typed transfer session over the candidate executor.
- `src/BaiduBatchTransfer.Core/Abstractions/TransferAutomationEvidence.cs`: durable source, target, submission, and directory-mutation records without secrets.
- `src/BaiduBatchTransfer.Core/Abstractions/IWebAutomationAttemptStore.cs`: atomic persistence operations for the new evidence.
- `src/BaiduBatchTransfer.Infrastructure/Database/Migrations/015_TransferAutomationEvidence.sql`: schema for transfer evidence and directory segment intents.
- `src/BaiduBatchTransfer.Infrastructure/Database/SqliteWebAutomationAttemptStore.cs`: transactional evidence writes and validated reads.
- `src/BaiduBatchTransfer.App/Features/BrowserAutomation/TransferRecoveryAuthorizationSource.cs`: store-backed read-only authorization for unresolved directory-parent and submitted target recovery navigation.
- `src/BaiduBatchTransfer.App/Features/Acceptance/AcceptanceWriteLeaseCoordinator.cs`: exact transfer-stage action matrix.
- `src/BaiduBatchTransfer.App/Features/BrowserAutomation/WebViewBaiduNetdiskAdapter.cs`: connect directory preparation, transfer execution, and unbound read-only recovery.

Persistence-facing records live in Core so `IWebAutomationAttemptStore` never depends on BaiduAdapter or App. Page-state, DOM-response, and script-only records stay in BaiduAdapter/App.

### Task 1: Complete typed transfer evidence contracts

**Files:**
- Create: `src/BaiduBatchTransfer.BaiduAdapter/WebAutomation/TransferAutomationContracts.cs`
- Modify: `src/BaiduBatchTransfer.BaiduAdapter/WebAutomation/TransferAutomationFlow.cs:6-146`
- Create: `src/BaiduBatchTransfer.Core/Abstractions/TransferAutomationEvidence.cs`
- Test: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/WebAutomation/TransferAutomationFlowTests.cs`
- Test: `tests/BaiduBatchTransfer.Core.Tests/TransferAutomationEvidenceTests.cs`

- [ ] **Step 1: Write failing tests for complete source and directory proofs**

Add tests named `SourceListingRequiresCompleteStableExactIdentitySet`, `DirectoryListingRequiresVerifiedPathAndAncestorChain`, and `EvidenceToStringRedactsRemoteValues`. Cover duplicate IDs, missing total/end marker, blank generation token, missing/mismatched Baidu UID, missing/mismatched four-part capability, duplicate ancestor IDs, mismatched target IDs, and control characters.

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduBatchTransfer.BaiduAdapter.Tests.csproj --filter "FullyQualifiedName~TransferAutomationFlowTests" --no-restore
dotnet test tests/BaiduBatchTransfer.Core.Tests/BaiduBatchTransfer.Core.Tests.csproj --filter "FullyQualifiedName~TransferAutomationEvidenceTests" --no-restore
```

Expected: FAIL because the exact proof and durable evidence types do not exist.

- [ ] **Step 3: Add immutable contracts**

Implement the following shape, with constructor validation and redacted `ToString()` methods:

```csharp
public sealed record TransferListingCompleteness(
    bool IsComplete,
    bool IsStable,
    int TotalCount,
    bool ReachedEnd,
    string GenerationToken);

public enum TransferRemoteItemKind { File, Folder }

public sealed record TransferRemoteItem(
    RemoteItemIdentity Identity,
    TransferRemoteItemKind Kind,
    string ExactServerName);

public sealed record TransferSourceListing(
    TransferSourceEvidence Evidence);

public sealed record TransferDirectoryListing(
    IReadOnlyList<TransferRemoteItem> Items,
    TransferListingCompleteness Completeness,
    string BaiduUserId,
    string ParentExactId,
    RemotePath CanonicalPath,
    IReadOnlyList<string> AncestorExactIds,
    string ObservationFingerprint);

public sealed record TransferIdentityMappingEntry(
    string SourceExactId,
    string DestinationExactId);

public sealed record TransferIdentityMapping(
    IReadOnlyList<TransferIdentityMappingEntry> Entries,
    string Fingerprint);

public sealed record TransferSourceEvidence(
    RemoteItemIdentitySet Items,
    int TotalCount,
    bool ReachedEnd,
    bool IsStable,
    string GenerationToken,
    string BaiduUserId,
    AdapterCapabilityVersion Capability,
    string ObservationFingerprint);

public sealed record TransferTargetPreparationEvidence(
    string TargetExactId,
    RemotePath TargetPath,
    int TotalCount,
    bool ReachedEnd,
    bool IsStable,
    string TargetGenerationToken,
    string BaiduUserId,
    WebAutomationBaseline Baseline,
    string ObservationFingerprint);

public sealed record TransferPreparationEvidence(
    TransferSourceEvidence Source,
    TransferTargetPreparationEvidence Target);

public sealed record TransferSubmissionEvidence(
    string SourceFingerprint,
    string TargetExactId,
    string TargetBaselineFingerprint,
    string BaiduUserId,
    AdapterCapabilityVersion Capability,
    DateTimeOffset MarkedSubmittingAtUtc);

public enum TransferDirectoryMutationState { Intent, Confirmed, OutcomeUnknown }

public sealed record TransferDirectoryMutationRecord(
    Guid OperationId,
    Guid AccountId,
    int Generation,
    int SegmentOrdinal,
    string ParentExactId,
    string? ChildExactId,
    RemotePath CanonicalChildPath,
    string ListingGenerationToken,
    string BaiduUserId,
    AdapterCapabilityVersion Capability,
    TransferDirectoryMutationState State,
    long OwningAttemptRevision);

public enum TransferDirectoryPreparationStatus { Prepared, NeedsAttention }

public sealed record TransferDirectoryPreparationOutcome(
    TransferDirectoryPreparationStatus Status,
    RemoteDirectory? Directory,
    RemotePath? ConfirmedPartialPath,
    BaiduErrorKind? ErrorKind)
{
    public override string ToString() =>
        $"TransferDirectoryPreparationOutcome {{ Status = {Status}, Values = [REDACTED] }}";
}
```

Allow an empty directory `Items` list only when `TotalCount == 0`; require exact IDs to be globally unique and require source listings to use `RemoteItemIdentitySet`. `TransferIdentityMapping` must cover every persisted source ID exactly once and every destination ID exactly once, with strict canonical ordering and a verified fingerprint. Persist the stable Baidu UID and exact four-part `AdapterCapabilityVersion` before the first directory mutation and repeat them in every mutation/submission claim used after restart. Keep extraction codes and complete share URLs out of these types.

Use the existing `RemoteItemIdentitySet` canonical UTF-16 big-endian sort keys. Compute new fingerprints with SHA-256 over a version tag followed by length-prefixed UTF-8 fields in strict canonical order; mapping entries order by source sort key then destination sort key, and observation fingerprints frame UID, route family, page state, parent ID/path, ancestor IDs, generation token, total/end evidence, and item IDs. Tests must pin byte-for-byte vectors so implementations cannot silently change framing.

- [ ] **Step 4: Make `TransferAutomationFlow` consume exact source sets**

Replace `TopLevelItemCount` correlation with equality between the persisted source identity set and the official source-to-destination mapping. Preserve `DefinitelyNotAccepted` before the durable boundary and `ResultPending` afterward.

- [ ] **Step 5: Run focused tests and verify GREEN**

Run the two commands from Step 2. Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/BaiduBatchTransfer.BaiduAdapter/WebAutomation src/BaiduBatchTransfer.Core/Abstractions tests/BaiduBatchTransfer.BaiduAdapter.Tests/WebAutomation/TransferAutomationFlowTests.cs tests/BaiduBatchTransfer.Core.Tests/TransferAutomationEvidenceTests.cs
git commit -m "feat: model exact transfer page evidence"
```

### Task 2: Persist source, target, submission, and directory mutation evidence

**Files:**
- Create: `src/BaiduBatchTransfer.Infrastructure/Database/Migrations/015_TransferAutomationEvidence.sql`
- Modify: `src/BaiduBatchTransfer.Core/Abstractions/WebAutomationAttemptRecord.cs`
- Modify: `src/BaiduBatchTransfer.Core/Abstractions/IWebAutomationAttemptStore.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteWebAutomationAttemptStore.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/DatabaseMigrator.cs`
- Test: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/MigrationTests.cs`
- Test: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/WebAutomationAttemptStoreTests.cs`

- [ ] **Step 1: Write failing migration and store tests**

Prove that source evidence can be persisted while the attempt remains `Preparing`, target completeness/baseline evidence later advances it to `BaselinePersisted`, submission evidence is durably stored at the `Submitting` boundary, and all three round-trip in canonical order. Prove directory intents are unique across generations by operation/account/canonical parent+segment, `Intent -> Confirmed` and `Intent -> OutcomeUnknown` use revision/CAS semantics, and accepted mapping/request ID can be persisted once. Add corrupt source/target counts, target end/stability/fingerprint corruption, corrupt sort keys, wrong-account, wrong-generation, forged cross-generation claim, and duplicate-child rejection tests.

- [ ] **Step 2: Run the database tests and verify RED**

Run:

```powershell
dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj --filter "FullyQualifiedName~MigrationTests|FullyQualifiedName~WebAutomationAttemptStoreTests" --no-restore
```

Expected: FAIL because migration 015 and evidence store methods are missing.

- [ ] **Step 3: Add migration 015**

Add bounded metadata columns to `WebAutomationAttempts` for source total/end/stable/generation/fingerprint, target total/end/stable/generation/ID/path/baseline/observation fingerprints, stable Baidu UID, exact adapter kind/protocol/selector/build versions, durable submission source/target/baseline fingerprints and timestamp, official request ID, and mapping fingerprint. Add child tables `WebAutomationAttemptSourceItems`, `WebAutomationTransferMappings`, and `WebAutomationDirectoryMutations` with composite foreign keys, canonical ordinals/sort keys, one-to-one source/destination uniqueness, exact UID/capability columns on each mutation claim, and explicit state checks. Add a partial unique index for unresolved directory mutations over `(OperationId, AccountId, ParentExactId, CanonicalSegment)` without `Generation`, whose predicate includes both `Intent` and `OutcomeUnknown`; retain `OriginGeneration`, owning attempt revision, and canonical parent/child paths in each row so a retry can reconcile the earlier attempt. Do not add extraction-code, cookie, raw URL, DOM, script, or telemetry columns.

- [ ] **Step 4: Add atomic store operations**

Add these interface operations and implement them transactionally:

```csharp
Task<bool> TryPersistTransferSourceEvidenceAsync(
    Guid operationId, Guid accountId, int generation, long expectedRevision,
    TransferSourceEvidence evidence,
    DateTimeOffset updatedAt, CancellationToken cancellationToken = default);

Task<bool> TryPersistTransferTargetPreparationAsync(
    Guid operationId, Guid accountId, int generation, long expectedRevision,
    TransferTargetPreparationEvidence evidence,
    DateTimeOffset updatedAt, CancellationToken cancellationToken = default);

Task<bool> TryBeginDirectoryMutationAsync(
    TransferDirectoryMutationRecord intent, long expectedAttemptRevision,
    DateTimeOffset updatedAt, CancellationToken cancellationToken = default);

Task<bool> TryResolveDirectoryMutationAsync(
    TransferDirectoryMutationRecord resolved, long expectedAttemptRevision,
    DateTimeOffset updatedAt, CancellationToken cancellationToken = default);

Task<IReadOnlyList<TransferDirectoryMutationRecord>> GetDirectoryMutationsAsync(
    Guid operationId, Guid accountId, int generation,
    CancellationToken cancellationToken = default);

Task<TransferDirectoryMutationRecord?> GetUnresolvedDirectoryMutationAsync(
    Guid operationId, Guid accountId, string parentExactId,
    RemotePath canonicalChildPath, CancellationToken cancellationToken = default);

Task<bool> TryPersistTransferSubmissionAsync(
    Guid operationId, Guid accountId, int generation, long expectedRevision,
    TransferSubmissionEvidence evidence, DateTimeOffset updatedAt,
    CancellationToken cancellationToken = default);

Task<bool> TryPersistTransferAcceptanceAsync(
    Guid operationId, Guid accountId, int generation, long expectedRevision,
    string officialRequestId, TransferIdentityMapping mapping,
    string observationFingerprint, DateTimeOffset updatedAt,
    CancellationToken cancellationToken = default);
```

`TryPersistTransferSourceEvidenceAsync` stays in `Preparing` and advances the revision; it also freezes UID and capability before any directory write. `TryPersistTransferTargetPreparationAsync` requires that exact source evidence, complete target total/end/stability proof and target observation fingerprint, then moves to `BaselinePersisted`. `TryPersistTransferSubmissionAsync` atomically stores the descriptor plus the same UID/capability while moving to `Submitting`. `TryBeginDirectoryMutationAsync` must require that frozen UID/capability and fail while any earlier generation owns the same unresolved parent+child claim. The partial unique-index predicate includes both `Intent` and `OutcomeUnknown`; `GetUnresolvedDirectoryMutationAsync` returns the origin generation and owning attempt revision required for CAS resolution. Load methods validate row counts, canonical sort keys, pinned byte framing/fingerprints, exact UID/capability equality, complete one-to-one mapping coverage, state combinations, and parent/child path continuity before returning a record.

- [ ] **Step 5: Run database tests and verify GREEN**

Run the command from Step 2. Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/BaiduBatchTransfer.Core/Abstractions src/BaiduBatchTransfer.Infrastructure/Database tests/BaiduBatchTransfer.Infrastructure.Tests/Database
git commit -m "feat: persist exact transfer automation evidence"
```

### Task 3: Versioned transfer candidate requests and secret-safe payloads

**Files:**
- Modify: `src/BaiduBatchTransfer.BaiduAdapter/WebAutomation/BaiduAutomationAction.cs`
- Modify: `src/BaiduBatchTransfer.BaiduAdapter/WebAutomation/BaiduPageState.cs`
- Modify: `src/BaiduBatchTransfer.BaiduAdapter/WebAutomation/BaiduSelectorCatalog.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/BaiduCandidateDomProtocol.cs`
- Test: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/WebAutomation/BaiduSelectorCatalogTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/BaiduCandidateDomProtocolTests.cs`

- [ ] **Step 1: Write failing protocol tests**

Add tests for `ExtractionCode`, `ExternalShare`, `DestinationPicker`, `OwnDrive`, and `TransferResult`; add actions `SubmitExtraction`, `OpenDestinationPicker`, `EnterDirectory`, `CreateDirectory`, `ConfirmDirectoryCreation`, `CancelDestinationPicker`, and `ClickConfirmTransfer`. Prove read requests do not require a write binding, mutation requests do, request equality binds Baidu UID/source IDs/parent ID/path, and every `ToString()` redacts typed text and remote identities.

- [ ] **Step 2: Run focused tests and verify RED**

```powershell
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter "FullyQualifiedName~BaiduCandidateDomProtocolTests" --no-restore
dotnet test tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduBatchTransfer.BaiduAdapter.Tests.csproj --filter "FullyQualifiedName~BaiduSelectorCatalogTests" --no-restore
```

Expected: FAIL on missing states, actions, requests, and transfer-specific versions.

- [ ] **Step 3: Separate reads from mutations**

Implement immutable request types:

```csharp
public sealed record BaiduCandidateDomReadRequest(
    Uri OfficialUri,
    BaiduCandidatePageState ExpectedPageState,
    string ExpectedBaiduUserId,
    ImmutableArray<string> ExpectedExactIds,
    string? ExpectedParentExactId,
    RemotePath? ExpectedCanonicalPath);

public sealed record BaiduCandidateDomMutationRequest(
    AdapterWriteAuthorizationRequest Authorization,
    AdapterWriteAuthorizationBinding Binding,
    BaiduAutomationAction Action,
    BaiduCandidateDomReadRequest ReadBinding,
    string? TypedValue);
```

`TypedValue` is allowed only for `SetExtractionCode` and `CreateDirectory`; it is serialized directly to the current in-memory script call and is never returned, hashed, persisted, logged, included in exceptions, or represented by `ToString()`.

- [ ] **Step 4: Introduce independent transfer versions**

Add `TransferProtocolVersion = "baidu-visible-transfer-v2"` and `TransferSelectorVersion = "baidu-transfer-pages-v2"`. Keep `Production` fail-closed and keep existing share versions/acceptance records independent.

- [ ] **Step 5: Run focused tests and verify GREEN**

Run the two commands from Step 2. Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/BaiduBatchTransfer.BaiduAdapter/WebAutomation src/BaiduBatchTransfer.App/Features/BrowserAutomation/BaiduCandidateDomProtocol.cs tests/BaiduBatchTransfer.BaiduAdapter.Tests/WebAutomation/BaiduSelectorCatalogTests.cs tests/BaiduBatchTransfer.App.Tests/Features/BaiduCandidateDomProtocolTests.cs
git commit -m "feat: define transfer page candidate v2"
```

### Task 4: Read-only semantic page recognition and complete enumeration

**Files:**
- Create: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/BaiduTransferCandidateScripts.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/WebView2CandidateDomExecutor.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/BaiduTransferCandidateScriptsTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/BaiduCandidateDomProtocolTests.cs`

- [ ] **Step 1: Write failing response-parser and script-shape tests**

Use sanitized synthetic responses to cover: unique extraction form, unique external resource root, unique destination dialog, verified drive root, official result root, hidden duplicate controls, ad/download buttons outside the trusted root, overlay/hit-test failure, virtual-list end marker, total mismatch, cursor cycle/gap, duplicate stable IDs, changing generation, wrong UID, wrong parent, wrong breadcrumb, and unapproved shadow roots.

- [ ] **Step 2: Run focused tests and verify RED**

```powershell
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter "FullyQualifiedName~BaiduTransferCandidateScriptsTests|FullyQualifiedName~BaiduCandidateDomProtocolTests" --no-restore
```

Expected: FAIL because the v2 scripts and strict response fields do not exist.

- [ ] **Step 3: Implement the fixed read script**

The script must:

1. require exact `https://pan.baidu.com` route families;
2. locate one trusted root by semantic title, native/ARIA role, and ancestor chain;
3. require candidates to be visible, enabled, connected, unmasked, and topmost by hit-test;
4. enumerate every page/cursor/virtual window until a verified total or end marker;
5. return only bounded typed fields: page state, control count, stable exact IDs, kinds, exact server names, UID, parent ID, canonical path, ancestor IDs, total/end evidence, generation token, and SHA-256 observation token;
6. return `notValidated` with `mutationCount: 0` for every ambiguity.

No raw DOM, HTML, cookies, headers, full source URL, or extraction code may be returned.

- [ ] **Step 4: Strictly parse and bind read results**

Reject unknown/duplicate fields, responses over the size cap, unsorted or duplicate IDs, unbounded text, non-HTTPS/URI mismatch, mismatched UID/path/parent/ancestor chain, incomplete enumeration, and nonzero mutation counts. Recovery uses this same read parser without an authorization binding.

- [ ] **Step 5: Run focused tests and verify GREEN**

Run the command from Step 2. Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/BaiduBatchTransfer.App/Features/BrowserAutomation tests/BaiduBatchTransfer.App.Tests/Features
git commit -m "feat: observe baidu transfer pages safely"
```

### Task 5: Mutation script and exact stage/action permits

**Files:**
- Modify: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/BaiduTransferCandidateScripts.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/WebView2CandidateDomExecutor.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Acceptance/AcceptanceWriteLeaseCoordinator.cs:382-495`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/BaiduTransferCandidateScriptsTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/BaiduCandidateDomProtocolTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/AcceptanceWriteLeaseCoordinatorTests.cs`

- [ ] **Step 1: Write failing action-matrix tests**

Prove `CreateDirectory`/`ConfirmDirectoryCreation` are allowed only in `EnsureDirectory`; `ClickConfirmTransfer` only in `SubmitTransfer`; `PrepareTransfer` and recovery allow no DOM mutation; each input/select/click consumes a newly acquired permit; expired/released/cross-account/cross-operation/cross-generation permits produce zero script mutations.

- [ ] **Step 2: Run focused tests and verify RED**

```powershell
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter "FullyQualifiedName~AcceptanceWriteLeaseCoordinatorTests|FullyQualifiedName~BaiduCandidateDomProtocolTests|FullyQualifiedName~BaiduTransferCandidateScriptsTests" --no-restore
```

Expected: FAIL on missing actions and incorrect stage authorization.

- [ ] **Step 3: Implement same-turn mutations**

For every action, reacquire the trusted root and target node in the same JavaScript invocation, repeat URI/UID/state/ancestor/role/visibility/enabled/connected/topmost/count/source/target/token checks, perform exactly one logical action, and return `mutationCount: 1`. Implement extraction-code input and submit, opening the destination picker, exact resource selection, entering a folder, creating and confirming one directory segment, cancelling the preparation picker, and the one final transfer confirmation.

- [ ] **Step 4: Make permits one-shot per action**

Keep `IBaiduCandidateMutationPermit` scoped around one script execution. Update `IsCandidateActionAllowedForStage` and adapter-kind whitelists to this exact matrix; leave share action rules unchanged:

```text
EnsureDirectory: SetExtractionCode, SubmitExtraction, OpenDestinationPicker,
                 EnterDirectory, CreateDirectory, ConfirmDirectoryCreation,
                 CancelDestinationPicker
PrepareTransfer: no mutations
SubmitTransfer:  SetExtractionCode, SubmitExtraction, SelectExactItems,
                 OpenDestinationPicker, EnterDirectory, ClickConfirmTransfer
Recovery:        no mutations
```

- [ ] **Step 5: Add secret-erasure regression tests**

Use a fake code such as `T3ST`; force serializer, host, parser, cancellation, and exception failures and assert the value never appears in observation tokens, `ToString()`, exception data/messages, test snapshots, or telemetry payloads.

- [ ] **Step 6: Run focused tests and verify GREEN**

Run the command from Step 2. Expected: PASS.

- [ ] **Step 7: Commit**

```powershell
git add src/BaiduBatchTransfer.App/Features/BrowserAutomation src/BaiduBatchTransfer.App/Features/Acceptance tests/BaiduBatchTransfer.App.Tests/Features
git commit -m "feat: mutate exact transfer controls by stage"
```

### Task 6: Recoverable exact directory preparation

**Files:**
- Create: `src/BaiduBatchTransfer.BaiduAdapter/WebAutomation/TransferDirectoryPreparationFlow.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/WebView2AutomationSession.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/WebViewBaiduNetdiskAdapter.cs:62-103`
- Create: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/TransferRecoveryAuthorizationSource.cs`
- Modify: `src/BaiduBatchTransfer.Application/Scheduling/OperationBoundBaiduNetdiskAdapter.cs`
- Modify: `src/BaiduBatchTransfer.Application/Scheduling/CoordinatedBaiduAdapters.cs`
- Modify: `src/BaiduBatchTransfer.Application/Scheduling/OperationExecutor.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Jobs/JobResultsViewModel.cs`
- Create: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/WebAutomation/TransferDirectoryPreparationFlowTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/WebView2AutomationSessionTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/WebViewBaiduNetdiskAdapterTests.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/TransferRecoveryAuthorizationSourceTests.cs`
- Modify: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/OperationExecutorTests.cs`
- Modify: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/NetworkExecutionGateTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/JobResultsViewModelTests.cs`

- [ ] **Step 1: Write failing path and crash-recovery tests**

Cover existing path, one/two missing segments, valid non-root prefix, segment-boundary false prefix, verified return to root, root identity unavailable, ancestor-chain mismatch, same-name non-folder, Unicode/whitespace server rename, concurrent duplicate, lost create response, crash after intent, lease expiry, same-generation restart, and new-generation retry. Assert a prior-generation unresolved claim is the first branch, is reconciled by reading only its exact parent, makes zero source-page calls, performs no extraction-code handling, requests zero mutation permits, and never creates the segment twice. Add changed UID/capability/build tests and assert an ambiguous creation returns the sanitized typed confirmed partial path, which the executor persists and `JobResultsViewModel` displays only as user-facing status—not in adapter diagnostics, exceptions, logs, or telemetry.

- [ ] **Step 2: Run focused tests and verify RED**

```powershell
dotnet test tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduBatchTransfer.BaiduAdapter.Tests.csproj --filter "FullyQualifiedName~TransferDirectoryPreparationFlowTests" --no-restore
dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj --filter "FullyQualifiedName~OperationExecutorTests|FullyQualifiedName~NetworkExecutionGateTests" --no-restore
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter "FullyQualifiedName~WebView2AutomationSessionTests|FullyQualifiedName~WebViewBaiduNetdiskAdapterTests|FullyQualifiedName~TransferRecoveryAuthorizationSourceTests|FullyQualifiedName~JobResultsViewModelTests" --no-restore
```

Expected: FAIL because automatic directory traversal does not exist and the adapter still asks for manual confirmation.

- [ ] **Step 3: Reconcile any unresolved directory intent before touching the source**

Before opening the share link, query unresolved `Intent`/`OutcomeUnknown` records across every generation for this operation/account. Implement the directory-mutation scope of `ITransferRecoveryAuthorizationSource` using `IWebAutomationAttemptStore` plus `IWebAutomationRequestOwnershipStore`; it validates the originating operation/batch/account/generation, owning revision, unresolved state, exact parent ID/path, persisted stable UID, and exact persisted adapter/protocol/selector/build capability before issuing a navigation/read-only binding. Use that binding to open and fully enumerate only the exact original parent. Mark it `Confirmed` by its owning attempt revision only when exactly one matching folder with a stable child ID exists; otherwise retain `OutcomeUnknown` and stop for human handling. Forged/stale/changed-version bindings fail, and this branch never opens/unlocks the source or obtains a mutation permit.

- [ ] **Step 4: Persist source evidence before normal directory mutation**

Start `EnsureDirectoryForOperationAsync` at `context.SourceShare.OriginalUrl`, not `OfficialFilesUri`. Unlock the source when required, prove the complete stable source set/generation/fingerprint, and call `TryPersistTransferSourceEvidenceAsync` while the attempt remains `Preparing`. Only after that durable write may the flow open the destination picker. A restart loads this source evidence; it never relies on an in-memory listing.

- [ ] **Step 5: Implement segment-boundary traversal**

Bind `(start exact ID, start canonical path, target canonical path)`. Process only a segment-boundary suffix; otherwise navigate to a verified root or return `NotValidated`. At every level fully enumerate the parent and verify UID, parent ID, canonical path, breadcrumb, and ancestor exact IDs.

- [ ] **Step 6: Persist intent before create, then prove creation**

The partial unique index must still prove no unresolved cross-generation claim exists. Persist the new segment intent before requesting a permit. After one create click, fully re-enumerate the same parent and require exactly one server-named `kind=folder` child with a stable exact ID. Persist `Confirmed` only after proof; on lost response, process interruption, generation change, or ambiguity persist `OutcomeUnknown`, stop, and report the already confirmed partial path.

- [ ] **Step 7: Cancel the preparation picker without submitting**

At the exact target, verify target ID/path/UID and execute only `CancelDestinationPicker`. If cancellation behavior is not field-validated, return `NotValidated` and do not enter transfer preparation.

- [ ] **Step 8: Replace manual confirmation and carry partial progress to the UI**

Change the operation-bound method to return `TransferDirectoryPreparationOutcome`, and propagate it through the coordinated adapter. Use `TransferDirectoryPreparationFlow` from `EnsureDirectoryForOperationAsync`; keep the existing user handoff UI only for CAPTCHA/login/risk-control handling, not for routine target directory creation. On `NeedsAttention`, `OperationExecutor` writes a bounded user-facing status such as `目录创建结果不确定；已确认创建到：<normalized partial path>` into the operation result row, while adapter diagnostics/exceptions/telemetry retain only a fixed code. `JobResultsViewModel` displays this persisted status. Tests prove control characters are rejected and the path appears only in the intended UI/status channel.

- [ ] **Step 9: Run focused tests and verify GREEN**

Run the two commands from Step 2. Expected: PASS.

- [ ] **Step 10: Commit**

```powershell
git add src/BaiduBatchTransfer.BaiduAdapter/WebAutomation/TransferDirectoryPreparationFlow.cs src/BaiduBatchTransfer.App/Features/BrowserAutomation tests/BaiduBatchTransfer.BaiduAdapter.Tests/WebAutomation/TransferDirectoryPreparationFlowTests.cs tests/BaiduBatchTransfer.App.Tests/Features
git commit -m "feat: prepare exact baidu target directories"
```

### Task 7: Exact prepare and single-submit transfer flow

**Files:**
- Modify: `src/BaiduBatchTransfer.BaiduAdapter/WebAutomation/TransferAutomationFlow.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/WebView2AutomationSession.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/WebViewBaiduNetdiskAdapter.cs:125-263`
- Modify: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/WebAutomation/TransferAutomationFlowTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/WebView2AutomationSessionTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/WebViewBaiduNetdiskAdapterTests.cs`

- [ ] **Step 1: Write failing prepare/submit tests**

Prove an APK, one folder, and multiple top-level resources load the full source set/generation previously persisted by `EnsureDirectory`; incomplete/unstable/no-ID source evidence stops before target preparation; target baseline is fully enumerated; submit reopens the source in a fresh visible session, rechecks the same IDs, selects the same target ID, marks the durable boundary immediately before one final confirmation, and never creates a directory.

- [ ] **Step 2: Run focused tests and verify RED**

```powershell
dotnet test tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduBatchTransfer.BaiduAdapter.Tests.csproj --filter "FullyQualifiedName~TransferAutomationFlowTests" --no-restore
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter "FullyQualifiedName~WebView2AutomationSessionTests|FullyQualifiedName~WebViewBaiduNetdiskAdapterTests" --no-restore
```

Expected: FAIL because the current flow stores only source count and most WebView2 transfer methods are fail-closed placeholders.

- [ ] **Step 3: Make mutation-free prepare load source evidence and append target evidence**

`PrepareAsync` must not reopen, unlock, select, or click on the source. Load the durable `TransferSourceEvidence` written by `EnsureDirectory`, open only the exact target in read mode, fully enumerate its baseline, and atomically persist `TransferTargetPreparationEvidence` while advancing to `BaselinePersisted`.

- [ ] **Step 4: Implement submit page transitions in the typed WebView2 session**

Navigate to the official share URI, recognize the extraction page when present, input the transient code, submit extraction, read the complete source listing, open the picker, select the exact target, and return a typed official acceptance. Every transition waits for the next expected page state and fails closed on timeout, CAPTCHA, login expiry, route change, or UID change.

Immediately before the only final confirmation, call `TryPersistTransferSubmissionAsync` to store the source/target/baseline/UID descriptor and advance to `Submitting`; only after that CAS succeeds may the fresh `ClickConfirmTransfer` permit be requested. If the click/response/window then fails, advance to `ResultPending` while retaining this descriptor for target-only recovery.

- [ ] **Step 5: Require the combined durable preparation evidence**

After target preparation, loading the attempt must produce `TransferPreparationEvidence(Source, Target)`. `SubmitAsync` requires that exact combined evidence rather than accepting only a baseline argument, and rejects any source generation, target ID/path/generation, UID, or fingerprint mismatch.

- [ ] **Step 6: Correlate only official exact results**

Accept a result only when the official result container supplies a nonempty request ID, target exact ID, and source-to-destination mapping that covers the complete source set. Re-enumerate the target and require all baseline IDs still present plus exactly the mapped destination IDs. Toast text, count changes, names, icons, sizes, or button disappearance are insufficient.

- [ ] **Step 7: Run focused tests and verify GREEN**

Run the two commands from Step 2. Expected: PASS.

- [ ] **Step 8: Commit**

```powershell
git add src/BaiduBatchTransfer.BaiduAdapter/WebAutomation/TransferAutomationFlow.cs src/BaiduBatchTransfer.App/Features/BrowserAutomation tests/BaiduBatchTransfer.BaiduAdapter.Tests/WebAutomation/TransferAutomationFlowTests.cs tests/BaiduBatchTransfer.App.Tests/Features
git commit -m "feat: execute exact visible baidu transfers"
```

### Task 8: Target-only recovery without a write lease

**Files:**
- Modify: `src/BaiduBatchTransfer.BaiduAdapter/WebAutomation/TransferAutomationFlow.cs:281-336`
- Modify: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/IVisibleWebAutomationBroker.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/VisibleWebAutomationQueue.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/WebView2AutomationSession.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/WebViewBaiduNetdiskAdapter.cs:265-367`
- Modify: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/TransferRecoveryAuthorizationSource.cs`
- Modify: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/WebAutomation/TransferAutomationFlowTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/VisibleWebAutomationQueueTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/WebViewBaiduNetdiskAdapterTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/TransferRecoveryAuthorizationSourceTests.cs`

- [ ] **Step 1: Write failing recovery tests**

For a protected source link and a temporary candidate acceptance, prove recovery opens only the verified target URI, performs complete read enumeration, makes zero source-page calls, passes no extraction code, requests no candidate mutation permit, and still checks operation/batch/account/generation/Baidu UID/source IDs/baseline/submission/capability. Test both official mapping present and lost-response mapping absent. Add forged binding, wrong owner, wrong target, stale generation, non-pending state, missing submission descriptor, changed build/version, and process-restart tests. Wrong UID, incomplete target evidence, or absent mapping without another approved content identity remains `ResultPending` rather than blocking the read.

- [ ] **Step 2: Run focused tests and verify RED**

```powershell
dotnet test tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduBatchTransfer.BaiduAdapter.Tests.csproj --filter "FullyQualifiedName~TransferAutomationFlowTests" --no-restore
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter "FullyQualifiedName~VisibleWebAutomationQueueTests|FullyQualifiedName~WebViewBaiduNetdiskAdapterTests|FullyQualifiedName~TransferRecoveryAuthorizationSourceTests" --no-restore
```

Expected: FAIL because `ConfirmExactAsync` currently reopens and unlocks the source and the queue creates a candidate executor only for bound write sessions.

- [ ] **Step 3: Add a durable read-only recovery authorizer**

Extend the Task 6 `ITransferRecoveryAuthorizationSource` with a submission-recovery scope. It authorizes only when durable ownership matches operation/batch/account/generation, the attempt is `ResultPending`, complete source and target evidence plus the pre-click `TransferSubmissionEvidence` are present, and requested target ID/path/UID plus adapter/protocol/selector/build capability equal the persisted values. Official request/mapping is optional at this authorization layer because the submit response may have been lost. The returned authorization is a short-lived immutable proof generated from current store data; it grants navigation/read only and cannot be converted into a mutation permit.

- [ ] **Step 4: Add an explicitly read-only recovery session path**

Allow a visible request to carry the store-authorized `ReadOnlyTransferRecoveryBinding(operation/batch/account/generation/target/UID/source fingerprint/baseline fingerprint/submission fingerprint/optional mapping fingerprint/capability)` without `AdapterWriteAuthorizationBinding`. Revalidate it immediately before exact-target navigation and immediately before accepting the listing. Construct the candidate executor for reads, but make mutation request construction impossible in this session. Navigation is limited to the exact official target URI.

- [ ] **Step 5: Rewrite confirmation to use persisted evidence only**

Change `ConfirmExactAsync` to accept `TransferPreparationEvidence`, `TransferSubmissionEvidence`, and optional persisted official acceptance, call only `ReadTargetCurrentListingAsync`, and compare current target IDs against the baseline/mapping. Remove calls to `OpenExternalShareAsync` and `EnterExtractionCodeAsync` from recovery. When mapping is absent and no versioned approved content-identity rule proves correlation, return `ResultPending` after the read; never reject authorization merely because the response evidence was lost.

- [ ] **Step 6: Run focused tests and verify GREEN**

Run the two commands from Step 2. Expected: PASS, including zero recovery permits and zero source mutations.

- [ ] **Step 7: Commit**

```powershell
git add src/BaiduBatchTransfer.BaiduAdapter/WebAutomation/TransferAutomationFlow.cs src/BaiduBatchTransfer.App/Features/BrowserAutomation tests/BaiduBatchTransfer.BaiduAdapter.Tests/WebAutomation/TransferAutomationFlowTests.cs tests/BaiduBatchTransfer.App.Tests/Features
git commit -m "fix: recover transfers from target evidence only"
```

### Task 9: Version binding, adapter integration, and regression verification

**Files:**
- Modify: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/WebViewBaiduNetdiskAdapter.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/IVisibleWebAutomationBroker.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/VisibleWebAutomationQueue.cs`
- Modify: `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/WebViewBaiduNetdiskAdapterTests.cs`
- Modify: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/LiveAdapterAvailabilityTests.cs`

- [ ] **Step 1: Write failing integration tests**

Prove candidate transfer v2 is selected only for an exact matching build/protocol/selector record or a single active Debug acceptance lease; old transfer records and all share records do not authorize it; account session must contain the matching stable `BaiduUserId`; the store-backed recovery authorizer is registered once and survives process restart; production remains disabled until field acceptance is recorded.

- [ ] **Step 2: Run focused tests and verify RED**

```powershell
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter "FullyQualifiedName~AppBootstrapTests|FullyQualifiedName~WebViewBaiduNetdiskAdapterTests" --no-restore
dotnet test tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduBatchTransfer.BaiduAdapter.Tests.csproj --filter "FullyQualifiedName~LiveAdapterAvailabilityTests" --no-restore
```

Expected: FAIL because integration still binds the shared v1 selector version and does not bind the remote UID.

- [ ] **Step 3: Wire the transfer-specific capability**

Pass the stable Baidu UID into transfer read/mutation bindings, select the v2 transfer capability everywhere the netdisk adapter is constructed, register `TransferRecoveryAuthorizationSource` with the attempt and ownership stores, preserve independent share versions, and keep normal production/recovery calls fail-closed without exact durable evidence.

- [ ] **Step 4: Run the complete automated verification**

```powershell
dotnet test BaiduBatchTransfer.sln --no-restore
dotnet build BaiduBatchTransfer.sln -c Debug --no-restore -warnaserror
dotnet build BaiduBatchTransfer.sln -c Release --no-restore -warnaserror
git diff --check
git status --short
```

Expected: all tests pass, both builds succeed with zero warnings/errors, `git diff --check` is clean, and only intended files are modified before commit.

- [ ] **Step 5: Commit**

```powershell
git add src tests
git commit -m "feat: integrate baidu transfer page adaptation"
```

### Task 10: Debug field acceptance and controlled handoff

**Files:**
- Modify only if the field run exposes a mismatch: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/BaiduTransferCandidateScripts.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/BaiduTransferCandidateScriptsTests.cs`
- Verify unchanged integration: `src/BaiduBatchTransfer.Application/Jobs/LocalResultExportCoordinator.cs`
- Verify unchanged manifest contract: `src/BaiduBatchTransfer.Application/Jobs/LocalResultWorkbookManifest.cs`
- Verify unchanged confirmation UI: `src/BaiduBatchTransfer.App/Features/Acceptance/AcceptanceConfirmationWindow.xaml.cs`
- Test: `tests/BaiduBatchTransfer.Application.Tests/Jobs/LocalResultExportCoordinatorTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/SqliteShareAutomationAcceptanceEvidenceStoreTests.cs`
- Evidence output: existing acceptance evidence directory selected by `BaiduAcceptanceProbeOutputStore`

- [ ] **Step 1: Build the Debug acceptance binary**

Run:

```powershell
dotnet build src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj -c Debug --no-restore -warnaserror
```

Expected: build succeeds with zero warnings/errors.

- [ ] **Step 2: Run one controlled `--webview-acceptance` case**

Use the already-authorized visible application flow and a disposable test link/account. Start with one APK and an existing target directory. Do not use DevTools, remote debugging, XHR/fetch, cookies, or an external browser controller. If CAPTCHA/login/risk-control appears, stop for user handoff.

- [ ] **Step 3: Inspect only sanitized typed evidence**

Confirm page state, control uniqueness, stable IDs, target exact ID/path, UID match, final request/mapping, zero duplicate submit, and no extraction code/full URL/raw DOM in the exported evidence. A field mismatch keeps the protocol `NotValidated`; it does not justify a guess or coordinate click.

- [ ] **Step 4: Export and re-verify the actual local result workbook**

Run the real `LocalResultExportCoordinator` path for the acceptance operation and reopen the produced workbook. Use the existing manifest contract without changing it: verify `ExportKey` owner/kind/generation, layout fingerprint, transfer adapter kind, account/operation/generation, operation content fingerprint, and artifact fingerprint. Correlate capability/build, source, target, destination, official mapping, and recovery fingerprints outside the workbook through the durable attempt plus sanitized acceptance evidence. Missing or mismatched evidence must leave both formal transfer and share acceptance records absent.

- [ ] **Step 5: Correct one proven mismatch with TDD, if necessary**

First add a sanitized regression fixture/test, verify RED, make the smallest change in the responsible contract/parser/script/store file, then rerun the focused test and Debug build. Bump the transfer selector/protocol version if route, trusted-root, semantic, action/payload, or identity rules change. If field evidence contradicts the approved contract, pause and revise this plan/spec rather than forcing the mismatch into the script.

- [ ] **Step 6: Expand controlled acceptance and workbook correlation**

After the first case succeeds, test one folder, one/two automatically created directory levels, multiple top-level resources, cancellation, and window-close/result-pending recovery. Each live case must obtain its own one-use acceptance lease and exact binding; no lease, binding, permit, or operation identity may be reused across scenarios. For every successful-result case, repeat the workbook manifest/artifact and outside-the-workbook durable evidence correlation. Confirm both transfer and share formal records remain absent throughout these runs.

- [ ] **Step 7: Run final verification and commit field-backed adjustments**

```powershell
dotnet test BaiduBatchTransfer.sln --no-restore
dotnet build BaiduBatchTransfer.sln -c Debug --no-restore -warnaserror
dotnet build BaiduBatchTransfer.sln -c Release --no-restore -warnaserror
git diff --check
```

Expected: all commands succeed, the workbook/export tests prove the exact manifest correlation, all required live scenarios are complete, and transfer/share formal acceptance records are still absent. If Step 5 changed code, commit the exact responsible files (not a blanket script-only commit) with:

```powershell
git add <exact field-backed source and test files>
git commit -m "fix: align transfer candidate with field evidence"
```

- [ ] **Step 8: Require the single second formal confirmation last**

Only after Steps 1–7 pass, show the existing second confirmation UI and require the user to confirm the exact build/protocol/selector/correlated evidence fingerprint. Verify the resulting formal record is transfer-only and the independent share formal record remains absent. Cancelling or closing the confirmation leaves both records absent. Do not run more code-changing or acceptance scenarios after this record is written; any later change invalidates it and requires the field sequence again.

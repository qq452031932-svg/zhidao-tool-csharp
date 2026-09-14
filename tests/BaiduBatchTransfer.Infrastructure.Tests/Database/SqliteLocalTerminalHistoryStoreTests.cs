using System.Security.Cryptography;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Tests.Database;

public sealed class SqliteLocalTerminalHistoryStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "history-cleanup-" + Guid.NewGuid().ToString("N"));
    private const string At = "2026-09-01T00:00:00Z";
    private const string RetryableTransferFailureSql = """
        DELETE FROM WebAutomationDirectoryMutations WHERE OperationId='target-share';
        DELETE FROM WebAutomationAttempts WHERE OperationId='target-share';
        DELETE FROM ShareOperations WHERE Id='target-share';
        UPDATE BatchJobs SET ShareOperationCount=0 WHERE Id='target';
        UPDATE TransferOperations SET Status='Failed',OutcomeKind='Failed',SubmitState='NotPrepared',TargetSlotState='Released'
        WHERE Id='target-transfer';
        UPDATE AccountAllocations SET FailedItemCount=1,TargetSuccessCount=1,EffectiveTargetSuccessCount=1 WHERE BatchJobId='target';
        """;

    [Theory]
    [InlineData(LocalHistoryKind.Transfer)]
    [InlineData(LocalHistoryKind.StandaloneShare)]
    public async Task PreviewSelectsOnlyTerminalBatchesAndEmptyConfirmationIsAuthenticated(LocalHistoryKind kind)
    {
        var (f, store) = await CreateAsync();
        var empty = await store.PreviewAsync(kind);
        Assert.Equal(0, empty.TerminalBatchCount);
        Assert.False(empty.HasConflicts);
        Assert.Equal(LocalHistoryCleanupDisposition.NothingToDelete, (await store.DeleteAsync(empty)).Disposition);
        Assert.Equal(LocalHistoryCleanupDisposition.Conflict,
            (await store.DeleteAsync(new(kind, 0, false, null))).Disposition);
        foreach (var status in kind == LocalHistoryKind.Transfer
                     ? new[] { "Completed", "CompletedWithShortfall", "Cancelled", "Active", "Paused", "Queued", "AwaitingCandidates", "PausedReady" }
                     : new[] { "Completed", "CompletedWithFailures", "Cancelled", "Active", "Paused", "Queued" })
            await SeedAsync(f, kind, Guid.NewGuid().ToString("N"), status);
        var preview = await store.PreviewAsync(kind);
        Assert.Equal(3, preview.TerminalBatchCount);
        Assert.False(preview.HasConflicts);
        Assert.InRange(preview.SnapshotToken!.Length, 32, LocalHistoryCleanupPreview.MaximumSnapshotTokenLength);
        Assert.Equal(preview.SnapshotToken, (await store.PreviewAsync(kind)).SnapshotToken);
    }

    [Theory]
    [InlineData(LocalHistoryKind.Transfer)]
    [InlineData(LocalHistoryKind.StandaloneShare)]
    public async Task DeletesCompleteEvidenceClosurePreservingOtherWorkImportsAndFiles(LocalHistoryKind kind)
    {
        var (f, store) = await CreateAsync();
        await SeedAsync(f, kind, "target");
        await SeedAsync(f, kind, "active", "Active");
        await SeedAsync(f, Other(kind), "other");
        await AddEvidenceAsync(f, kind, "target");
        await AddEvidenceAsync(f, kind, "active");
        await AddEvidenceAsync(f, Other(kind), "other");
        var paths = new[] { "result.xlsx", "result.csv", "license-state.json" }.Select(p => Path.Combine(root, p)).ToArray();
        foreach (var path in paths) await File.WriteAllTextAsync(path, "must remain unchanged: " + path);
        var hashes = paths.Select(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).ToArray();
        var times = paths.Select(File.GetLastWriteTimeUtc).ToArray();
        using var licenseGuard = new FileStream(paths[2], FileMode.Open, FileAccess.Read, FileShare.None);
        var preview = await store.PreviewAsync(kind);
        Assert.False(preview.HasConflicts);
        var result = await store.DeleteAsync(preview);
        Assert.Equal(LocalHistoryCleanupDisposition.Deleted, result.Disposition);
        Assert.Equal(1, result.DeletedBatchCount);
        Assert.Equal(0, await CountAsync(f, $"SELECT count(*) FROM {BatchTable(kind)} WHERE Id='target'"));
        Assert.Equal(1, await CountAsync(f, $"SELECT count(*) FROM {BatchTable(kind)} WHERE Id='active'"));
        Assert.Equal(1, await CountAsync(f, $"SELECT count(*) FROM {BatchTable(Other(kind))} WHERE Id='other'"));
        foreach (var table in new[] { "WebAutomationAttempts", "WebAutomationDirectoryMutations", "WebAutomationAttemptSourceItems", "WebAutomationAttemptRemoteItems", "WebAutomationTransferMappings", "WebAutomationAttemptAcceptedRemoteItems", "WebAutomationShareAcceptances" })
            Assert.Equal(0, await CountAsync(f, $"SELECT count(*) FROM {table} WHERE OperationId LIKE 'target-%'"));
        Assert.Equal(0, await CountAsync(f, "SELECT count(*) FROM LocalResultExports WHERE OwnerId='target'"));
        Assert.Equal(2, await CountAsync(f, "SELECT count(*) FROM LocalResultExports"));
        Assert.Equal(1, await CountAsync(f, "SELECT count(*) FROM Accounts WHERE EncryptedSession=X'010203' AND Status='Active'"));
        Assert.Equal(1, await CountAsync(f, "SELECT count(*) FROM ImportSessions"));
        Assert.Equal(1, await CountAsync(f, "SELECT count(*) FROM StagedImportRows"));
        if (kind == LocalHistoryKind.Transfer)
            Assert.Equal(1, await CountAsync(f, "SELECT count(*) FROM ImportSessions WHERE CreatedBatchJobId IS NULL AND AppendToBatchJobId IS NULL"));
        await AssertNoForeignKeyIssuesAsync(f);
        licenseGuard.Dispose();
        Assert.Equal(hashes, paths.Select(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))));
        Assert.Equal(times, paths.Select(File.GetLastWriteTimeUtc));
    }

    public static IEnumerable<object[]> Conflicts()
    {
        foreach (var status in new[] { "Waiting", "Reserved", "Preparing", "Submitting", "ResultPending", "Paused" })
            yield return [LocalHistoryKind.Transfer, $"UPDATE TransferOperations SET Status='{status}' WHERE Id='target-transfer'"];
        foreach (var status in new[] { "WaitingShare", "Reserved", "Preparing", "Submitting", "ResultPending", "Paused" })
            yield return [LocalHistoryKind.Transfer, $"UPDATE ShareOperations SET Status='{status}' WHERE Id='target-share'"];
        yield return [LocalHistoryKind.Transfer, "UPDATE ShareOperations SET Status='WaitingTransfer',ExactItemSetFingerprint=NULL,LeaseGeneration=0 WHERE Id='target-share'"];
        yield return [LocalHistoryKind.Transfer, "UPDATE ShareOperations SET Status='Failed',SubmissionAcceptance='DefinitelyNotAccepted',NextReconcileAt='2099-01-01' WHERE Id='target-share'"];
        yield return [LocalHistoryKind.Transfer, "UPDATE ShareOperations SET Status='Failed',RequestDescriptorJson='{}' WHERE Id='target-share'"];
        yield return [LocalHistoryKind.Transfer, RetryableTransferFailureSql];
        yield return [LocalHistoryKind.Transfer, "UPDATE TransferOperations SET Status='Failed',OutcomeKind='Failed',SubmitState='NotPrepared',TargetSlotState='Released',AdapterRequestId='unresolved-request' WHERE Id='target-transfer'"];
        yield return [LocalHistoryKind.Transfer, "UPDATE TransferOperations SET SubmitState='PendingConfirmation',ConfirmationDescriptorJson='{}' WHERE Id='target-transfer'"];
        yield return [LocalHistoryKind.Transfer, "UPDATE TransferOperations SET TargetSlotState='Reserved' WHERE Id='target-transfer'"];
        yield return [LocalHistoryKind.Transfer, "UPDATE TransferOperations SET LeaseExpiresAt='2099-01-01' WHERE Id='target-transfer'"];
        yield return [LocalHistoryKind.Transfer, "UPDATE ShareOperations SET LeaseExpiresAt='2099-01-01' WHERE Id='target-share'"];
        yield return [LocalHistoryKind.Transfer, "UPDATE ResourceItems SET CandidateStatus='Reserved',ReservationExpiresAt='2099-01-01' WHERE Id='target-resource'"];
        foreach (var status in new[] { "Queued", "Reserved", "Preparing", "Submitting", "ResultPending", "NeedsDecision", "Paused" })
            yield return [LocalHistoryKind.StandaloneShare, $"UPDATE StandaloneShareOperations SET Status='{status}' WHERE Id='target-standalone'"];
        yield return [LocalHistoryKind.StandaloneShare, "UPDATE StandaloneShareOperations SET Status='Failed',SubmissionState='DefinitelyNotAccepted' WHERE Id='target-standalone'"];
        yield return [LocalHistoryKind.StandaloneShare, "UPDATE StandaloneShareOperations SET Status='Failed',SubmissionState='NotStarted' WHERE Id='target-standalone'"];
        yield return [LocalHistoryKind.StandaloneShare, "UPDATE StandaloneShareOperations SET SubmissionState='ResultPending' WHERE Id='target-standalone'"];
        yield return [LocalHistoryKind.StandaloneShare, "UPDATE StandaloneShareOperations SET LeaseOwnerId='owner',LeaseExpiresAt='2099-01-01' WHERE Id='target-standalone'"];
        yield return [LocalHistoryKind.StandaloneShare, "UPDATE StandaloneShareOperations SET NextReconcileAt='2099-01-01' WHERE Id='target-standalone'"];
        foreach (var kind in new[] { LocalHistoryKind.Transfer, LocalHistoryKind.StandaloneShare })
        {
            yield return [kind, "UPDATE LocalResultExports SET Status='InProgress',LeaseOwnerId='owner',LeaseExpiresAt='2000-01-01' WHERE OwnerId='target'"];
            yield return [kind, "UPDATE LocalResultExports SET Status='Failed',LeaseOwnerId='owner',LeaseExpiresAt='2099-01-01' WHERE OwnerId='target'"];
            foreach (var state in new[] { "Preparing", "BaselinePersisted", "Submitting", "ResultPending", "NeedsDecision" })
                yield return [kind, $"UPDATE WebAutomationAttempts SET State='{state}' WHERE OperationId LIKE 'target-%'"];
            yield return [kind, "UPDATE WebAutomationAttempts SET State='UserHandoff',ResumeStage='Preparing',IsWaitingForUser=1 WHERE OperationId LIKE 'target-%'"];
        }
    }

    [Theory]
    [MemberData(nameof(Conflicts))]
    public async Task ActiveOrRecoverableDescendantsBlockPreviewAndDeleteWithoutPartialCleanup(LocalHistoryKind kind, string mutation)
    {
        var (f, store) = await CreateAsync();
        await SeedAsync(f, kind, "target", kind == LocalHistoryKind.Transfer ? "CompletedWithShortfall" : "CompletedWithFailures");
        await AddEvidenceAsync(f, kind, "target");
        await SeedAsync(f, kind, "safe");
        var confirmed = await store.PreviewAsync(kind);
        Assert.False(confirmed.HasConflicts);
        await ExecuteAsync(f, mutation);
        var preview = await store.PreviewAsync(kind);
        Assert.True(preview.HasConflicts);
        foreach (var candidate in new[] { confirmed, preview })
        {
            var result = await store.DeleteAsync(candidate);
            Assert.Equal(LocalHistoryCleanupDisposition.Conflict, result.Disposition);
            Assert.Equal(0, result.DeletedBatchCount);
        }
        Assert.Equal(2, await CountAsync(f, $"SELECT count(*) FROM {BatchTable(kind)}"));
        Assert.True(await CountAsync(f, "SELECT count(*) FROM WebAutomationAttempts") > 0);
    }

    [Theory]
    [InlineData(LocalHistoryKind.Transfer)]
    [InlineData(LocalHistoryKind.StandaloneShare)]
    public async Task ForgedCrossKindCrossStoreAndCountTokensCannotDelete(LocalHistoryKind kind)
    {
        var (f, store) = await CreateAsync();
        await SeedAsync(f, kind, "target");
        await SeedAsync(f, Other(kind), "other");
        var preview = await store.PreviewAsync(kind);
        var foreign = await new SqliteLocalTerminalHistoryStore(f).PreviewAsync(kind);
        var tampered = (preview.SnapshotToken![0] == 'A' ? "B" : "A") + preview.SnapshotToken[1..];
        foreach (var candidate in new[] { new(kind, 1, false, "forged"), new(kind, 1, false, tampered), new(kind, 0, false, preview.SnapshotToken), new(Other(kind), 1, false, preview.SnapshotToken), foreign })
            Assert.Equal(LocalHistoryCleanupDisposition.Conflict, (await store.DeleteAsync(candidate)).Disposition);
        Assert.Equal(1, await CountAsync(f, $"SELECT count(*) FROM {BatchTable(kind)}"));
        Assert.Equal(1, await CountAsync(f, $"SELECT count(*) FROM {BatchTable(Other(kind))}"));
    }

    [Theory]
    [InlineData("addition")]
    [InlineData("removal")]
    [InlineData("status")]
    [InlineData("operation")]
    [InlineData("evidence")]
    public async Task ConfirmationRejectsTargetSetOrStateDrift(string drift)
    {
        var (f, store) = await CreateAsync();
        await SeedAsync(f, LocalHistoryKind.Transfer, "target");
        await AddEvidenceAsync(f, LocalHistoryKind.Transfer, "target");
        var preview = await store.PreviewAsync(LocalHistoryKind.Transfer);
        if (drift == "addition") await SeedAsync(f, LocalHistoryKind.Transfer, "new");
        else if (drift == "removal") await ExecuteAsync(f, "UPDATE BatchJobs SET Status='Active' WHERE Id='target'");
        else if (drift == "status") await ExecuteAsync(f, "UPDATE BatchJobs SET Status='Cancelled' WHERE Id='target'");
        else if (drift == "operation") await ExecuteAsync(f, "UPDATE ShareOperations SET Status='ReusedExisting' WHERE Id='target-share'");
        else await ExecuteAsync(f, "UPDATE WebAutomationAttempts SET Revision=Revision+1 WHERE OperationId='target-transfer'");
        var result = await store.DeleteAsync(preview);
        Assert.Equal(LocalHistoryCleanupDisposition.Conflict, result.Disposition);
        Assert.Equal(1, await CountAsync(f, "SELECT count(*) FROM BatchJobs WHERE Id='target'"));
    }

    [Fact]
    public async Task SqlFailureRollsBackEarlierDeletes()
    {
        var (f, store) = await CreateAsync();
        await SeedAsync(f, LocalHistoryKind.Transfer, "target");
        await AddEvidenceAsync(f, LocalHistoryKind.Transfer, "target");
        await ExecuteAsync(f, "CREATE TRIGGER FailCleanup BEFORE DELETE ON BatchJobs BEGIN SELECT RAISE(ABORT,'injected failure'); END;");
        var preview = await store.PreviewAsync(LocalHistoryKind.Transfer);
        await Assert.ThrowsAsync<SqliteException>(() => store.DeleteAsync(preview));
        Assert.Equal(1, await CountAsync(f, "SELECT count(*) FROM LocalResultExports"));
        Assert.Equal(2, await CountAsync(f, "SELECT count(*) FROM WebAutomationAttempts"));
        Assert.Equal(1, await CountAsync(f, "SELECT count(*) FROM ShareOperations"));
        Assert.Equal(1, await CountAsync(f, "SELECT count(*) FROM ImportSessions WHERE CreatedBatchJobId='target'"));
        await AssertNoForeignKeyIssuesAsync(f);
    }

    [Theory]
    [InlineData(LocalHistoryKind.Transfer)]
    [InlineData(LocalHistoryKind.StandaloneShare)]
    public async Task ExpiredLeasesOnOtherwiseFinalRowsDoNotPermanentlyBlockCleanup(LocalHistoryKind kind)
    {
        var (f, store) = await CreateAsync();
        await SeedAsync(f, kind, "target");
        await AddEvidenceAsync(f, kind, "target");
        if (kind == LocalHistoryKind.Transfer)
            await ExecuteAsync(f, "UPDATE TransferOperations SET LeaseExpiresAt='2000-01-01'; UPDATE ShareOperations SET LeaseExpiresAt='2000-01-01';");
        else
            await ExecuteAsync(f, "UPDATE StandaloneShareOperations SET LeaseOwnerId='old',LeaseExpiresAt='2000-01-01';");
        await ExecuteAsync(f, "UPDATE LocalResultExports SET Status='Failed',LeaseOwnerId='old',LeaseExpiresAt='2000-01-01';");
        var preview = await store.PreviewAsync(kind);
        Assert.False(preview.HasConflicts);
        Assert.Equal(LocalHistoryCleanupDisposition.Deleted, (await store.DeleteAsync(preview)).Disposition);
        await AssertNoForeignKeyIssuesAsync(f);
    }

    [Fact]
    public async Task PreexistingUnrelatedForeignKeyIssueDoesNotPreventCleanup()
    {
        var (f, store) = await CreateAsync();
        await SeedAsync(f, LocalHistoryKind.Transfer, "target");
        await ExecuteAsync(f, "PRAGMA foreign_keys=OFF; INSERT INTO AccountRules(Id,AccountId,RuleType,RuleValue,Priority) VALUES('orphan','missing','x','y',1);");
        var result = await store.DeleteAsync(await store.PreviewAsync(LocalHistoryKind.Transfer));
        Assert.Equal(LocalHistoryCleanupDisposition.Deleted, result.Disposition);
        Assert.Equal(1, await CountAsync(f, "SELECT count(*) FROM AccountRules"));
    }

    [Fact]
    public async Task NewlyIntroducedForeignKeyIssueRollsBackCleanup()
    {
        var (f, store) = await CreateAsync();
        await SeedAsync(f, LocalHistoryKind.Transfer, "target");
        await ExecuteAsync(f, "CREATE TABLE CleanupProbe(Id TEXT, AccountId TEXT REFERENCES Accounts(Id) DEFERRABLE INITIALLY DEFERRED); CREATE TRIGGER IntroduceOrphan AFTER DELETE ON BatchJobs BEGIN INSERT INTO CleanupProbe VALUES('new','missing'); END;");
        await Assert.ThrowsAsync<InvalidDataException>(() => DeleteCurrentAsync(store, LocalHistoryKind.Transfer));
        Assert.Equal(1, await CountAsync(f, "SELECT count(*) FROM BatchJobs"));
        Assert.Equal(0, await CountAsync(f, "SELECT count(*) FROM CleanupProbe"));
    }

    [Fact]
    public async Task CrossBatchResourceReferenceCannotCascadeDeleteActiveWork()
    {
        var (f, store) = await CreateAsync();
        await SeedAsync(f, LocalHistoryKind.Transfer, "target");
        await SeedAsync(f, LocalHistoryKind.Transfer, "active", "Active");
        await ExecuteAsync(f, """
            DELETE FROM ExecutionLogs WHERE Id='active-log';
            DELETE FROM ShareOperations WHERE Id='active-share';
            UPDATE TransferOperations SET ResourceItemId='target-resource',Status='Waiting',TargetPath='/other',NormalizedTargetPath='/other'
            WHERE Id='active-transfer';
            """);
        var preview = await store.PreviewAsync(LocalHistoryKind.Transfer);
        Assert.True(preview.HasConflicts);
        Assert.Equal(LocalHistoryCleanupDisposition.Conflict, (await store.DeleteAsync(preview)).Disposition);
        Assert.Equal(1, await CountAsync(f, "SELECT count(*) FROM TransferOperations WHERE Id='active-transfer'"));
    }

    [Fact]
    public async Task TargetResidueAbortsAndRollsBackEvenWhenTriggerSilentlyIgnoresDelete()
    {
        var (f, store) = await CreateAsync();
        await SeedAsync(f, LocalHistoryKind.Transfer, "target");
        await AddEvidenceAsync(f, LocalHistoryKind.Transfer, "target");
        await ExecuteAsync(f, "CREATE TRIGGER KeepEvidence BEFORE DELETE ON WebAutomationDirectoryMutations BEGIN SELECT RAISE(IGNORE); END;");
        await Assert.ThrowsAsync<InvalidDataException>(() => DeleteCurrentAsync(store, LocalHistoryKind.Transfer));
        Assert.Equal(1, await CountAsync(f, "SELECT count(*) FROM BatchJobs"));
        Assert.Equal(2, await CountAsync(f, "SELECT count(*) FROM WebAutomationAttempts"));
    }

    [Theory]
    [InlineData("Transfer", "Intent", "DefinitelyNotSubmitted")]
    [InlineData("Transfer", "OutcomeUnknown", "DefinitelyNotSubmitted")]
    [InlineData("TransferShare", "Intent", "DefinitelyNotSubmitted")]
    [InlineData("TransferShare", "OutcomeUnknown", "DefinitelyNotSubmitted")]
    [InlineData("StandaloneShare", "Intent", "DefinitelyNotSubmitted")]
    [InlineData("StandaloneShare", "OutcomeUnknown", "DefinitelyNotSubmitted")]
    [InlineData("Transfer", "Intent", "Succeeded")]
    [InlineData("Transfer", "OutcomeUnknown", "Succeeded")]
    [InlineData("TransferShare", "Intent", "Succeeded")]
    [InlineData("TransferShare", "OutcomeUnknown", "Succeeded")]
    [InlineData("StandaloneShare", "Intent", "Succeeded")]
    [InlineData("StandaloneShare", "OutcomeUnknown", "Succeeded")]
    public async Task UnresolvedDirectoryMutationBlocksCleanupDespiteTerminalOwnerAndAttempt(
        string owner, string mutationState, string attemptState)
    {
        var kind = owner == "StandaloneShare" ? LocalHistoryKind.StandaloneShare : LocalHistoryKind.Transfer;
        var (f, store) = await CreateAsync();
        await SeedAsync(f, kind, "target", "Cancelled");
        await AddEvidenceAsync(f, kind, "target");
        await ExecuteAsync(f, "UPDATE WebAutomationAttempts SET State=$state WHERE OwnerKind=$owner;",
            ("$state", attemptState), ("$owner", owner));
        var confirmed = await store.PreviewAsync(kind);
        Assert.False(confirmed.HasConflicts);
        await ExecuteAsync(f, """
            UPDATE WebAutomationDirectoryMutations SET State=$state
            WHERE OperationId IN (SELECT OperationId FROM WebAutomationAttempts WHERE OwnerKind=$owner);
            """, ("$state", mutationState), ("$owner", owner));
        var preview = await store.PreviewAsync(kind);
        Assert.True(preview.HasConflicts);
        Assert.Equal(LocalHistoryCleanupDisposition.Conflict, (await store.DeleteAsync(preview)).Disposition);
        Assert.Equal(LocalHistoryCleanupDisposition.Conflict, (await store.DeleteAsync(confirmed)).Disposition);
        Assert.Equal(1, await CountAsync(f, $"SELECT count(*) FROM {BatchTable(kind)}"));
        Assert.Equal(kind == LocalHistoryKind.Transfer ? 2 : 1, await CountAsync(f, "SELECT count(*) FROM WebAutomationDirectoryMutations"));
        Assert.Equal(kind == LocalHistoryKind.Transfer ? 2 : 1, await CountAsync(f, "SELECT count(*) FROM WebAutomationAttempts"));
        Assert.Equal(1, await CountAsync(f, "SELECT count(*) FROM LocalResultExports"));
    }

    [Theory]
    [InlineData("GloballyInvalid", "Invalid", "NotPrepared")]
    [InlineData("GloballyInvalid", "Invalid", "PreparingToSubmit")]
    [InlineData("GloballyInvalid", "Valid", "NotPrepared")]
    [InlineData("Failed", "Invalid", "NotPrepared")]
    public async Task ConfirmedNonretryableFailureCanBeDeletedFromCompletedWithShortfall(
        string outcome, string validity, string submitState)
    {
        var (f, store) = await CreateAsync();
        await SeedAsync(f, LocalHistoryKind.Transfer, "target", "CompletedWithShortfall");
        await ExecuteAsync(f, RetryableTransferFailureSql);
        await ExecuteAsync(f, """
            UPDATE TransferOperations SET OutcomeKind=$outcome,SubmitState=$submit WHERE Id='target-transfer';
            UPDATE ResourceItems SET GlobalValidityStatus=$validity WHERE Id='target-resource';
            """, ("$outcome", outcome), ("$submit", submitState), ("$validity", validity));
        var preview = await store.PreviewAsync(LocalHistoryKind.Transfer);
        Assert.Equal(1, preview.TerminalBatchCount);
        Assert.False(preview.HasConflicts);
        var result = await store.DeleteAsync(preview);
        Assert.Equal(LocalHistoryCleanupDisposition.Deleted, result.Disposition);
        Assert.Equal(1, result.DeletedBatchCount);
        Assert.Equal(0, await CountAsync(f, "SELECT count(*) FROM BatchJobs"));
        Assert.Equal(0, await CountAsync(f, "SELECT count(*) FROM TransferOperations"));
        Assert.Equal(1, await CountAsync(f, "SELECT count(*) FROM Accounts"));
        await AssertNoForeignKeyIssuesAsync(f);
    }

    [Theory]
    [InlineData("NotPrepared")]
    [InlineData("PreparingToSubmit")]
    public async Task GenuinelyRetryableTransferFailureBlocksCleanup(string submitState)
    {
        var (f, store) = await CreateAsync();
        await SeedAsync(f, LocalHistoryKind.Transfer, "target", "CompletedWithShortfall");
        await ExecuteAsync(f, RetryableTransferFailureSql);
        await ExecuteAsync(f, "UPDATE TransferOperations SET SubmitState=$submit WHERE Id='target-transfer';", ("$submit", submitState));
        var preview = await store.PreviewAsync(LocalHistoryKind.Transfer);
        Assert.True(preview.HasConflicts);
        var result = await store.DeleteAsync(preview);
        Assert.Equal(LocalHistoryCleanupDisposition.Conflict, result.Disposition);
        Assert.Equal(0, result.DeletedBatchCount);
        Assert.Equal(1, await CountAsync(f, "SELECT count(*) FROM TransferOperations WHERE OutcomeKind='Failed'"));
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("DefinitelyNotAccepted")]
    public async Task GenuinelyRetryableShareFailureBlocksCleanup(string submissionAcceptance)
    {
        var (f, store) = await CreateAsync();
        await SeedAsync(f, LocalHistoryKind.Transfer, "target", "CompletedWithShortfall");
        await ExecuteAsync(f, """
            UPDATE TransferOperations SET SubmitState='ResultReceived',ConfirmationDescriptorJson='{}',RemoteItemIdsJson='["remote"]'
            WHERE Id='target-transfer';
            UPDATE ShareOperations SET Status='Failed',SubmissionAcceptance=$acceptance WHERE Id='target-share';
            """, ("$acceptance", submissionAcceptance));
        var preview = await store.PreviewAsync(LocalHistoryKind.Transfer);
        Assert.True(preview.HasConflicts);
        var result = await store.DeleteAsync(preview);
        Assert.Equal(LocalHistoryCleanupDisposition.Conflict, result.Disposition);
        Assert.Equal(0, result.DeletedBatchCount);
        Assert.Equal(1, await CountAsync(f, "SELECT count(*) FROM ShareOperations WHERE Status='Failed'"));
        Assert.Equal(1, await CountAsync(f, "SELECT count(*) FROM ShareOperationGenerations"));
        Assert.Equal(1, await CountAsync(f, "SELECT count(*) FROM ShareOperationRemoteItems"));
    }

    [Theory]
    [InlineData("2099-01-01T00:00:00+00:00")]
    [InlineData("invalid-time")]
    [InlineData("now")]
    [InlineData("2451545")]
    [InlineData("2000-02-30T00:00:00Z")]
    [InlineData("2000-01-01T00:00:00")]
    public async Task ActiveOrUntrustedDiscoveryLeaseBlocksCleanup(string expiresAt)
    {
        var (f, store) = await CreateAsync();
        await SeedAsync(f, LocalHistoryKind.Transfer, "target");
        await AddDiscoveryRunAsync(f, "target", expiresAt);
        var preview = await store.PreviewAsync(LocalHistoryKind.Transfer);
        Assert.True(preview.HasConflicts);
        Assert.Equal(LocalHistoryCleanupDisposition.Conflict, (await store.DeleteAsync(preview)).Disposition);
        Assert.Equal(1, await CountAsync(f, "SELECT count(*) FROM BatchJobs"));
        Assert.Equal(1, await CountAsync(f, "SELECT count(*) FROM BatchShareDiscoveryRuns"));
        Assert.Equal(1, await CountAsync(f, "SELECT count(*) FROM BatchShareDiscoveryRunExactSets"));
        Assert.Equal(1, await CountAsync(f, "SELECT count(*) FROM BatchShareDiscoveryStaging"));
    }

    [Theory]
    [InlineData("2000-01-01T00:00:00Z")]
    [InlineData("2000-01-01T08:00:00.1234567+08:00")]
    public async Task ExpiredDiscoveryRunAndChildrenAreDeletedWithoutAffectingOtherWork(string expiresAt)
    {
        var (f, store) = await CreateAsync();
        await SeedAsync(f, LocalHistoryKind.Transfer, "target");
        await AddDiscoveryRunAsync(f, "target", expiresAt);
        await AddEvidenceAsync(f, LocalHistoryKind.Transfer, "target");
        await SeedAsync(f, LocalHistoryKind.Transfer, "active", "Active");
        await AddDiscoveryRunAsync(f, "active", "2099-01-01T00:00:00Z");
        var preview = await store.PreviewAsync(LocalHistoryKind.Transfer);
        Assert.False(preview.HasConflicts);
        var result = await store.DeleteAsync(preview);
        Assert.Equal(LocalHistoryCleanupDisposition.Deleted, result.Disposition);
        Assert.Equal(1, result.DeletedBatchCount);
        foreach (var table in new[] { "BatchShareDiscoveryRuns", "BatchShareDiscoveryRunExactSets", "BatchShareDiscoveryStaging" })
        {
            Assert.Equal(0, await CountAsync(f, $"SELECT count(*) FROM {table} WHERE RunId='target-run'"));
            Assert.Equal(1, await CountAsync(f, $"SELECT count(*) FROM {table} WHERE RunId='active-run'"));
        }
        Assert.Equal(0, await CountAsync(f, "SELECT count(*) FROM WebAutomationDirectoryMutations WHERE OperationId LIKE 'target-%'"));
        Assert.Equal(1, await CountAsync(f, "SELECT count(*) FROM BatchJobs WHERE Id='active'"));
        await AssertNoForeignKeyIssuesAsync(f);
    }

    [Theory]
    [InlineData("2000-01-01T00:00:00Z", "2099-01-01T00:00:00Z")]
    [InlineData("2099-01-01T00:00:00Z", "2000-01-01T00:00:00Z")]
    [InlineData("2000-01-01T00:00:00Z", "2001-01-01T00:00:00Z")]
    public async Task DiscoveryLeaseChangeInvalidatesTheConfirmedSnapshot(string oldExpiry, string newExpiry)
    {
        var (f, store) = await CreateAsync();
        await SeedAsync(f, LocalHistoryKind.Transfer, "target");
        await AddDiscoveryRunAsync(f, "target", oldExpiry);
        var confirmed = await store.PreviewAsync(LocalHistoryKind.Transfer);
        await ExecuteAsync(f, "UPDATE BatchShareDiscoveryRuns SET ExpiresAt=$expires;", ("$expires", newExpiry));
        var current = await store.PreviewAsync(LocalHistoryKind.Transfer);
        Assert.NotEqual(confirmed.SnapshotToken, current.SnapshotToken);
        Assert.Equal(LocalHistoryCleanupDisposition.Conflict, (await store.DeleteAsync(confirmed)).Disposition);
        Assert.Equal(1, await CountAsync(f, "SELECT count(*) FROM BatchShareDiscoveryRuns"));
    }

    [Fact]
    public async Task DiscoveryExactSetChangeInvalidatesTheConfirmedSnapshot()
    {
        var (f, store) = await CreateAsync();
        await SeedAsync(f, LocalHistoryKind.Transfer, "target");
        await AddDiscoveryRunAsync(f, "target", "2000-01-01T00:00:00Z");
        var confirmed = await store.PreviewAsync(LocalHistoryKind.Transfer);
        await ExecuteAsync(f, "INSERT INTO BatchShareDiscoveryRunExactSets VALUES('target-run','account','additional-exact-set');");
        var current = await store.PreviewAsync(LocalHistoryKind.Transfer);
        Assert.NotEqual(confirmed.SnapshotToken, current.SnapshotToken);
        Assert.Equal(LocalHistoryCleanupDisposition.Conflict, (await store.DeleteAsync(confirmed)).Disposition);
        Assert.Equal(2, await CountAsync(f, "SELECT count(*) FROM BatchShareDiscoveryRunExactSets"));
    }

    [Fact]
    public async Task DiscoveryStagingOwnedByAnotherBatchCannotBeDeletedByCascade()
    {
        var (f, store) = await CreateAsync();
        await SeedAsync(f, LocalHistoryKind.Transfer, "target");
        await AddDiscoveryRunAsync(f, "target", "2000-01-01T00:00:00Z");
        await SeedAsync(f, LocalHistoryKind.Transfer, "active", "Active");
        await ExecuteAsync(f, "UPDATE BatchShareDiscoveryStaging SET BatchJobId='active' WHERE RunId='target-run';");
        var preview = await store.PreviewAsync(LocalHistoryKind.Transfer);
        Assert.True(preview.HasConflicts);
        Assert.Equal(LocalHistoryCleanupDisposition.Conflict, (await store.DeleteAsync(preview)).Disposition);
        Assert.Equal(1, await CountAsync(f, "SELECT count(*) FROM BatchShareDiscoveryStaging WHERE BatchJobId='active'"));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task DiscoveryLeaseUsesTheInjectedUtcClockAndStrictExpiryBoundary(int secondsUntilExpiry, bool expectedConflict)
    {
        var (f, _) = await CreateAsync();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 14, 10, 0, 0, TimeSpan.Zero));
        var store = new SqliteLocalTerminalHistoryStore(f, clock);
        await SeedAsync(f, LocalHistoryKind.Transfer, "target");
        await AddDiscoveryRunAsync(f, "target", clock.UtcNow.AddSeconds(secondsUntilExpiry).ToString("O"));
        var preview = await store.PreviewAsync(LocalHistoryKind.Transfer);
        Assert.Equal(expectedConflict, preview.HasConflicts);
        Assert.Equal(expectedConflict ? LocalHistoryCleanupDisposition.Conflict : LocalHistoryCleanupDisposition.Deleted,
            (await store.DeleteAsync(preview)).Disposition);
        await AssertNoForeignKeyIssuesAsync(f);
    }

    [Theory]
    [InlineData(-1, -2)]
    [InlineData(1, 2)]
    public async Task DiscoveryLeaseEffectiveStateChangeInvalidatesSnapshotWithoutAClockValueInTheToken(int secondsUntilExpiry, int clockAdvanceSeconds)
    {
        var (f, _) = await CreateAsync();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 14, 10, 0, 0, TimeSpan.Zero));
        var store = new SqliteLocalTerminalHistoryStore(f, clock);
        await SeedAsync(f, LocalHistoryKind.Transfer, "target");
        await AddDiscoveryRunAsync(f, "target", clock.UtcNow.AddSeconds(secondsUntilExpiry).ToString("O"));
        var confirmed = await store.PreviewAsync(LocalHistoryKind.Transfer);
        Assert.Equal(confirmed.SnapshotToken, (await store.PreviewAsync(LocalHistoryKind.Transfer)).SnapshotToken);
        clock.UtcNow = clock.UtcNow.AddSeconds(clockAdvanceSeconds);
        var current = await store.PreviewAsync(LocalHistoryKind.Transfer);
        Assert.NotEqual(confirmed.HasConflicts, current.HasConflicts);
        Assert.NotEqual(confirmed.SnapshotToken, current.SnapshotToken);
        Assert.Equal(LocalHistoryCleanupDisposition.Conflict, (await store.DeleteAsync(confirmed)).Disposition);
        Assert.Equal(1, await CountAsync(f, "SELECT count(*) FROM BatchShareDiscoveryRuns"));
    }

    [Fact]
    public async Task TransferDeletionCascadesFullConfirmedShareGraphAndPreservesPreferences()
    {
        var (f, store) = await CreateAsync();
        await SeedAsync(f, LocalHistoryKind.Transfer, "target");
        await ExecuteAsync(f, """
            INSERT INTO DirectBackgroundWorkspacePreferences VALUES(1,'keep-directory','2026-09-01');
            UPDATE DirectBackgroundAuthorizationState SET NormalEnabled=1,UpdatedAt='2026-09-01';
            INSERT INTO BatchSourceColumns VALUES('target',0,'url','ShareUrl',1);
            INSERT INTO BatchResultDestinations VALUES('target','result-dir',1,'2026-09-01','2026-09-01');
            INSERT INTO ShareDiscoveryBaselines(ShareOperationId,Generation,ShareId) VALUES('target-share',1,'baseline');
            INSERT INTO BaiduShareResults(Id,ShareOperationId,BatchJobId,ResourceItemId,AccountId,CandidateSequence,OutputOrdinal,ExactItemSetFingerprint,SettingsFingerprint,Generation,ShareId,ShareUrl,ExtractionCode,IsAccepted,IsCurrent,SharedAt,UpdatedAt)
            VALUES('result','target-share','target','target-resource','account',1,1,'target','settings',1,'share','https://example.test','code',1,1,'2026-09-01','2026-09-01');
            INSERT INTO BatchShareDiscoverySnapshots VALUES('target','account','settings','discovery',1,1,0,'generation','2026-09-01',1);
            INSERT INTO BatchShareDecisionSnapshots VALUES('target','account','settings','Reuse','discovery',1,1,0,'generation','2026-09-01',1);
            INSERT INTO BatchShareDiscoveryMembers VALUES('target','account','settings','discovery','exact','share','https://example.test','code',1,1,1);
            INSERT INTO BatchShareDiscoveryExactSets VALUES('target','account','settings','exact');
            INSERT INTO DirectBackgroundAcceptanceAuthorizations VALUES('target-transfer','target','account',1,'action',0,1,'2026-09-01','2026-09-01');
            INSERT INTO BatchCreationReceipts VALUES(
                'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',
                'BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB',
                'CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC',
                'import','target',NULL,'2026-09-01','{}');
            """);
        Assert.Equal(LocalHistoryCleanupDisposition.Deleted, (await DeleteCurrentAsync(store, LocalHistoryKind.Transfer)).Disposition);
        foreach (var table in new[] { "BatchSourceColumns", "BatchResultDestinations", "ShareDiscoveryBaselines", "BaiduShareResults", "BatchShareDiscoverySnapshots", "BatchShareDecisionSnapshots", "BatchShareDiscoveryMembers", "BatchShareDiscoveryExactSets", "DirectBackgroundAcceptanceAuthorizations", "BatchCreationReceipts", "ShareOperationGenerations", "ShareOperationRemoteItems" })
            Assert.Equal(0, await CountAsync(f, $"SELECT count(*) FROM {table}"));
        Assert.Equal(1, await CountAsync(f, "SELECT count(*) FROM DirectBackgroundWorkspacePreferences WHERE DefaultResultDirectory='keep-directory'"));
        Assert.Equal(1, await CountAsync(f, "SELECT count(*) FROM DirectBackgroundAuthorizationState WHERE NormalEnabled=1 AND UpdatedAt='2026-09-01'"));
        Assert.Equal(1, await CountAsync(f, "SELECT count(*) FROM ImportSessions"));
        Assert.Equal(1, await CountAsync(f, "SELECT count(*) FROM StagedImportRows"));
        await AssertNoForeignKeyIssuesAsync(f);
    }

    private static async Task<LocalHistoryCleanupResult> DeleteCurrentAsync(SqliteLocalTerminalHistoryStore store, LocalHistoryKind kind) =>
        await store.DeleteAsync(await store.PreviewAsync(kind));

    private async Task<(SqliteConnectionFactory Factory, SqliteLocalTerminalHistoryStore Store)> CreateAsync()
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, Guid.NewGuid().ToString("N") + ".db"));
        await new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
        await ExecuteAsync(factory, """
            INSERT INTO Accounts(Id,BaiduUserId,DisplayName,EncryptedSession,LoginMethod,Status,DailyCountDateKey)
            VALUES('account','uid','name',X'010203','Cookie','Active','2026-09-01');
            INSERT INTO ImportSessions(Id,SourceType,SourceName,Status,CreatedAt,UpdatedAt)
            VALUES('import','Excel','input.xlsx','BatchCreated','2026-09-01','2026-09-01');
            INSERT INTO StagedImportRows(Id,ImportSessionId,SourceRowNumber,ShareUrl,Disposition,UpdatedAt)
            VALUES('staged','import',1,'https://example.test','Ready','2026-09-01');
            """);
        return (factory, new(factory));
    }

    private static async Task SeedAsync(SqliteConnectionFactory f, LocalHistoryKind kind, string id, string status = "Completed")
    {
        if (kind == LocalHistoryKind.Transfer)
            await ExecuteAsync(f, """
                INSERT INTO BatchJobs(Id,Name,SourceType,TransferMode,QuotaBasis,ShortagePolicy,DefaultTargetPath,FolderCreationMode,DuplicatePolicy,ValidationMode,Status,CreatedAt,QueueOrder,ImportSessionId)
                VALUES($id,'batch','Excel','Distribution','SuccessTarget','WaitForCandidates','/','PerItem','Skip','Strict',$status,$at,1,'import');
                INSERT INTO ResourceItems(Id,BatchJobId,StagedImportRowId,CandidateSequence,ShareUrl,NormalizedShareId,FolderCreationModeOverride,GlobalValidityStatus,CandidateStatus,CreatedAt,UpdatedAt)
                VALUES($id||'-resource',$id,'staged',1,'https://example.test',$id,'Inherit','Valid','Expanded',$at,$at);
                INSERT INTO AccountAllocations(Id,BatchJobId,AccountId,PriorityOrder,DefaultTargetPath,NormalizedDefaultTargetPath)
                VALUES($id||'-allocation',$id,'account',1,'/','/');
                INSERT INTO TransferOperations(Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,NormalizedTargetPath,AssignedAccountId,AllocationSource,Status,OperationFingerprint,SubmitState,TargetSlotState,CreatedAt,UpdatedAt,ExecutionEngineVersion,BrowserFamily,ProfileId)
                VALUES($id||'-transfer',$id||'-resource',$id,1,1,'/','/','account','Fixed','Succeeded',$id,'NotPrepared','Fulfilled',$at,$at,'WebView2-v1','LegacyWebView2','legacy-webview2-account-account');
                INSERT INTO BatchShareSettings(BatchJobId,IsPermanent,IsAccessUnlimited,ExtractionCodeMode,ExistingSharePolicy,SettingsFingerprint,CreatedAt)
                VALUES($id,1,1,'Random','Reuse','settings',$at);
                INSERT INTO BatchOutputAccounts(BatchJobId,AccountId,OutputOrdinal,OutputLabel,PlannedTransferCount,PlannedShareCount,ShareEnabled)
                VALUES($id,'account',1,'output',1,1,1);
                INSERT INTO ShareOperations(Id,TransferOperationId,ResourceItemId,BatchJobId,AccountId,OutputOrdinal,Status,ExactItemSetFingerprint,SettingsFingerprint,Generation,SubmissionAcceptance,CreatedAt,UpdatedAt,ExecutionEngineVersion,BrowserFamily,ProfileId)
                VALUES($id||'-share',$id||'-transfer',$id||'-resource',$id,'account',1,'Succeeded',$id,'settings',1,'Unknown',$at,$at,'WebView2-v1','LegacyWebView2','legacy-webview2-account-account');
                INSERT INTO ShareOperationRemoteItems(ShareOperationId,ItemOrdinal,RemoteItemId,CanonicalSortKey,DisplayName)
                VALUES($id||'-share',0,'remote',X'01','file');
                UPDATE ShareOperationGenerations SET ExactItemCount=1 WHERE ShareOperationId=$id||'-share';
                INSERT INTO ExecutionLogs(Id,BatchJobId,ResourceItemId,TransferOperationId,AccountId,Stage,Level,EventCode,SanitizedMessage,CreatedAt)
                VALUES($id||'-log',$id,$id||'-resource',$id||'-transfer','account','x','Info','x','safe',$at);
                UPDATE ImportSessions SET CreatedBatchJobId=$id,AppendToBatchJobId=$id WHERE $id='target';
                """, ("$id", id), ("$status", status), ("$at", At));
        else
            await ExecuteAsync(f, """
                INSERT INTO StandaloneShareBatches(Id,DraftFingerprint,ResultDirectoryReference,Status,OperationCount,CreatedAt,UpdatedAt)
                VALUES($id,$id,'result-ref',$status,1,$at,$at);
                INSERT INTO StandaloneShareOperations(Id,BatchId,AccountId,SequenceNumber,DisplayName,ResourceKind,ParentPath,ExactItemSetFingerprint,Status,SubmissionState,CreatedAt,UpdatedAt,ExecutionEngineVersion,BrowserFamily,ProfileId)
                VALUES($id||'-standalone',$id,'account',1,'file','File','/',$id,'Succeeded','Completed',$at,$at,'WebView2-v1','LegacyWebView2','legacy-webview2-account-account');
                INSERT INTO StandaloneShareRemoteItems(OperationId,ItemOrdinal,RemoteItemId,CanonicalSortKey,DisplayName)
                VALUES($id||'-standalone',0,'remote',X'01','file');
                INSERT INTO StandaloneShareDiscoveryBaselines(OperationId,Generation,ShareId) VALUES($id||'-standalone',1,'baseline');
                INSERT INTO StandaloneShareResults(Id,OperationId,BatchId,AccountId,Generation,ExactItemSetFingerprint,SettingsFingerprint,ShareId,ShareUrl,ExtractionCode,IsPermanent,IsAccessUnlimited,AcceptedAt,CreatedAt)
                VALUES($id||'-result',$id||'-standalone',$id,'account',1,$id,'settings',$id,'https://example.test','code',1,1,$at,$at);
                """, ("$id", id), ("$status", status), ("$at", At));
    }

    private static async Task AddEvidenceAsync(SqliteConnectionFactory f, LocalHistoryKind kind, string id)
    {
        await ExecuteAsync(f, """
            INSERT INTO LocalResultExports(Id,OwnerKind,OwnerId,ExportKind,Generation,Status,TargetFileName,FinalFileName,CreatedAt,UpdatedAt)
            VALUES($id||'-export',$owner,$id,$export,1,'Succeeded','result.xlsx','result.xlsx',$at,$at);
            """, ("$id", id), ("$owner", kind == LocalHistoryKind.Transfer ? "TransferBatch" : "StandaloneShareBatch"),
            ("$export", kind == LocalHistoryKind.Transfer ? "ReplacementWorkbook" : "StandaloneShareWorkbook"), ("$at", At));
        foreach (var owner in kind == LocalHistoryKind.Transfer ? new[] { "Transfer", "TransferShare" } : new[] { "StandaloneShare" })
        {
            var operation = id + (owner == "Transfer" ? "-transfer" : owner == "TransferShare" ? "-share" : "-standalone");
            await ExecuteAsync(f, """
                INSERT INTO WebAutomationAttempts(OperationId,AccountId,Generation,OwnerKind,State,BaselineFingerprint,BaselinePersistedAt,CreatedAt,UpdatedAt,ExecutionEngineVersion,BrowserFamily,ProfileId)
                VALUES($op,'account',1,$owner,'Succeeded','baseline',$at,$at,$at,'WebView2-v1','LegacyWebView2','legacy-webview2-account-account');
                INSERT INTO WebAutomationAttemptSourceItems(OperationId,AccountId,Generation,ItemOrdinal,RemoteItemId,CanonicalSortKey) VALUES($op,'account',1,0,'source',X'01');
                INSERT INTO WebAutomationAttemptRemoteItems(OperationId,AccountId,Generation,ItemOrdinal,RemoteItemId,CanonicalSortKey) VALUES($op,'account',1,0,'remote',X'02');
                INSERT INTO WebAutomationAttemptAcceptedRemoteItems(OperationId,AccountId,Generation,ItemOrdinal,RemoteItemId,CanonicalSortKey) VALUES($op,'account',1,0,'accepted',X'03');
                INSERT INTO WebAutomationTransferMappings(OperationId,AccountId,Generation,ItemOrdinal,SourceRemoteItemId,SourceCanonicalSortKey,DestinationRemoteItemId,DestinationCanonicalSortKey) VALUES($op,'account',1,0,'source',X'01','dest',X'04');
                INSERT INTO WebAutomationDirectoryMutations(OperationId,AccountId,OriginGeneration,SegmentOrdinal,ParentExactId,CanonicalSegment,ChildExactId,CanonicalChildPath,ListingGenerationToken,BaiduUserId,CapabilityAdapterKind,CapabilityProtocolVersion,CapabilitySelectorVersion,CapabilityBuildVersion,State,OwningAttemptRevision,CreatedAt,UpdatedAt)
                VALUES($op,'account',1,0,'root','folder','child','/folder','listing','uid','WebView2','1','1','1','Confirmed',0,$at,$at);
                """, ("$op", operation), ("$owner", owner), ("$at", At));
            if (owner != "Transfer")
                await ExecuteAsync(f, """
                    INSERT INTO WebAutomationShareAcceptances(OperationId,AccountId,Generation,OwnerKind,AdapterRequestId,ShareId,ShareUrl,ExtractionCode,ExtractionCodeMode,ExactItemSetFingerprint,ObservationFingerprint,AcceptedAt)
                    VALUES($op,'account',1,$owner,'request','share','https://example.test','code','Random','exact','observation',$at);
                    """, ("$op", operation), ("$owner", owner), ("$at", At));
        }
    }

    private static Task AddDiscoveryRunAsync(SqliteConnectionFactory f, string batchId, string expiresAt) =>
        ExecuteAsync(f, """
            INSERT INTO BatchShareDiscoveryRuns(RunId,BatchJobId,AccountId,SettingsFingerprint,ExpectedWorkCount,CreatedAt,ExpiresAt)
            VALUES($batch||'-run',$batch,'account','settings',1,$at,$expires);
            INSERT INTO BatchShareDiscoveryRunExactSets(RunId,AccountId,ExactItemSetFingerprint)
            VALUES($batch||'-run','account','exact-set');
            INSERT INTO BatchShareDiscoveryStaging(RunId,BatchJobId,AccountId,SettingsFingerprint,ExactItemSetFingerprint,ShareId,ShareUrl,ExtractionCode,IsPermanent,IsAccessUnlimited,IsCompatible)
            VALUES($batch||'-run',$batch,'account','settings','exact-set','share','https://example.test','code',1,1,1);
            """, ("$batch", batchId), ("$expires", expiresAt), ("$at", At));

    private static string BatchTable(LocalHistoryKind kind) => kind == LocalHistoryKind.Transfer ? "BatchJobs" : "StandaloneShareBatches";
    private static LocalHistoryKind Other(LocalHistoryKind kind) => kind == LocalHistoryKind.Transfer ? LocalHistoryKind.StandaloneShare : LocalHistoryKind.Transfer;
    private static async Task ExecuteAsync(SqliteConnectionFactory f, string sql, params (string Name, object Value)[] parameters)
    {
        await using var c = await f.OpenConnectionAsync();
        await using var command = c.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }
    private static async Task<long> CountAsync(SqliteConnectionFactory f, string sql)
    {
        await using var c = await f.OpenConnectionAsync();
        await using var command = c.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
    private static async Task AssertNoForeignKeyIssuesAsync(SqliteConnectionFactory f)
    {
        await using var c = await f.OpenConnectionAsync();
        await using var command = c.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.False(await reader.ReadAsync());
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}

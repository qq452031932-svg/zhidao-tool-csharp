using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;

namespace BaiduBatchTransfer.Infrastructure.Tests.Database;

public sealed class WebAutomationAttemptStoreTests : IDisposable
{
    // Ownership note: browser attempts duplicate no TransferOperations/ShareOperations result facts.
    // They persist only the UI phase, sanitized observation fingerprint, and exact pre-submit
    // remote directory set needed to recover one account-bound generation.
    private readonly string root = Path.Combine(Path.GetTempPath(), $"bbt-web-attempt-{Guid.NewGuid():N}");

    [Fact]
    public async Task AttemptInheritsAndRoundTripsTheExactManagedParentSnapshot()
    {
        var graph = await CreateAsync(managedTransfer: true);
        await SetTransferGenerationAsync(graph, 1);
        var snapshot = graph.TransferExecutionProfile;

        Assert.True(await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 1, WebAutomationOwnerKind.Transfer, graph.Now, snapshot)));
        var restored = await graph.Store.GetAsync(graph.OperationId, graph.AccountId, 1);

        Assert.NotNull(restored);
        Assert.Equal(snapshot.Engine, restored.ExecutionProfile.Engine);
        Assert.Equal(snapshot.BrowserFamily, restored.ExecutionProfile.BrowserFamily);
        Assert.Equal(snapshot.ProfileId, restored.ExecutionProfile.ProfileId);
        Assert.Equal(snapshot.InstallationId, restored.ExecutionProfile.InstallationId);
        Assert.Equal(snapshot.SessionId, restored.ExecutionProfile.SessionId);
    }

    [Fact]
    public async Task HistoricalAttemptMetadataCanAdvanceAfterTheParentStartsANewerGeneration()
    {
        var graph = await CreateAsync();
        await SetTransferGenerationAsync(graph, 1);
        Assert.True(await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 1, WebAutomationOwnerKind.Transfer, graph.Now)));
        await SetTransferGenerationAsync(graph, 2);

        await using var connection = await graph.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE WebAutomationAttempts SET UpdatedAt=$updated
            WHERE OperationId=$operation AND AccountId=$account AND Generation=1;
            """;
        command.Parameters.AddWithValue("$updated", graph.Now.AddMinutes(1).ToString("O"));
        command.Parameters.AddWithValue("$operation", graph.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$account", graph.AccountId.ToString("D"));
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task ExactTransferEvidenceRoundTripsThroughPreparedSubmittedAndAcceptedStages()
    {
        var graph = await CreateAsync();
        await SetTransferGenerationAsync(graph, 1);
        Assert.True(await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 1, WebAutomationOwnerKind.Transfer, graph.Now)));
        var capability = Capability();
        var source = new TransferSourceEvidence(
            new RemoteItemIdentitySet([new("source-2", null), new("source-1", null)]),
            2, true, true, "source-generation", "baidu-user", capability, Fingerprint('1'));

        Assert.True(await graph.Store.TryPersistTransferSourceEvidenceAsync(
            graph.OperationId, graph.AccountId, 1, 0, source, graph.Now.AddSeconds(1)));
        var baseline = ExactBaseline("before");
        var targetFingerprint = TransferTargetPreparationEvidence.ComputeObservationFingerprint(
            "target-directory", new RemotePath("/自动转存/游戏"), 1, true, true,
            "target-generation", "baidu-user", TransferTargetInventoryScope.AllItems, baseline);
        var target = new TransferTargetPreparationEvidence(
            "target-directory", new RemotePath("/自动转存/游戏"), 1, true, true,
            "target-generation", "baidu-user", TransferTargetInventoryScope.AllItems,
            baseline, targetFingerprint);
        Assert.True(await graph.Store.TryPersistTransferTargetPreparationAsync(
            graph.OperationId, graph.AccountId, 1, 1, target, graph.Now.AddSeconds(2)));
        var submission = TransferSubmissionEvidence.CreateBound(
            Guid.NewGuid(), Fingerprint('2'), source.Items.Fingerprint, target.TargetExactId,
            target.Baseline.Fingerprint, "baidu-user", capability, graph.Now.AddSeconds(3));
        Assert.True(await graph.Store.TryPersistTransferSubmissionAsync(
            graph.OperationId, graph.AccountId, 1, 2, submission, graph.Now.AddSeconds(3)));
        var mapping = new TransferIdentityMapping([
            new("source-1", "destination-1"),
            new("source-2", "destination-2"),
        ]);
        Assert.True(await graph.Store.TryPersistTransferAcceptanceAsync(
            graph.OperationId, graph.AccountId, 1, 3, "official-request", mapping,
            Fingerprint('3'), graph.Now.AddSeconds(4)));

        var restored = await new SqliteWebAutomationAttemptStore(graph.Factory)
            .GetAsync(graph.OperationId, graph.AccountId, 1);
        Assert.NotNull(restored);
        Assert.Equal(WebAutomationAttemptState.ResultPending, restored.State);
        Assert.Equal(source, restored.SourceEvidence);
        Assert.Equal(target.TargetExactId, restored.TargetPreparationEvidence!.TargetExactId);
        Assert.Equal(target.TargetPath, restored.TargetPreparationEvidence.TargetPath);
        Assert.Equal(target.Baseline.Fingerprint, restored.TargetPreparationEvidence.Baseline.Fingerprint);
        Assert.Equal(target.TargetGenerationToken, restored.TargetPreparationEvidence.TargetGenerationToken);
        Assert.Equal(TransferTargetInventoryScope.AllItems, restored.TargetPreparationEvidence.Scope);
        Assert.Equal(targetFingerprint, restored.TargetPreparationEvidence.ObservationFingerprint);
        Assert.Equal(submission, restored.SubmissionEvidence);
        Assert.Equal("official-request", restored.OfficialRequestId);
        Assert.Equal(mapping.Fingerprint, restored.TransferIdentityMapping!.Fingerprint);
        Assert.Equal(Fingerprint('3'), restored.OfficialObservationFingerprint);
        Assert.Equal(mapping.Entries.ToArray(), restored.TransferIdentityMapping.Entries.ToArray());
        Assert.Equal(["destination-1", "destination-2"],
            restored.AcceptedRemoteItems!.Items.Select(item => item.ExactId));
        Assert.Equal(4, restored.Revision);
    }

    [Fact]
    public async Task EmptyOfficialResultPendingCanBindExactAcceptanceAfterSqliteReopenOnlyOnce()
    {
        var graph = await CreateBoundSubmittingAttemptAsync();
        var submitting = (await graph.Store.GetAsync(
            graph.OperationId, graph.AccountId, 1))!;
        Assert.True(await graph.Store.TryAdvanceAsync(
            graph.OperationId, graph.AccountId, 1,
            WebAutomationAttemptState.Submitting, submitting.Revision,
            WebAutomationAttemptState.ResultPending, observationFingerprint: null,
            isWaitingForUser: false, graph.Now.AddSeconds(4)));

        var reopened = new SqliteWebAutomationAttemptStore(graph.Factory);
        var emptyPending = (await reopened.GetAsync(
            graph.OperationId, graph.AccountId, 1))!;
        Assert.Equal(WebAutomationAttemptState.ResultPending, emptyPending.State);
        Assert.NotNull(emptyPending.SubmissionEvidence);
        Assert.Null(emptyPending.OfficialRequestId);
        Assert.Null(emptyPending.TransferIdentityMapping);
        Assert.Null(emptyPending.AcceptedRemoteItems);
        Assert.Null(emptyPending.OfficialObservationFingerprint);

        var mapping = new TransferIdentityMapping([new("source", "destination")]);
        Assert.False(await reopened.TryPersistTransferAcceptanceAsync(
            graph.OperationId, graph.AccountId, 1, emptyPending.Revision,
            "late-official-request", mapping, Fingerprint('9'), graph.Now.AddSeconds(5)));
        Assert.False(await reopened.TryPersistTransferLateAcceptanceAsync(
            graph.OperationId, graph.AccountId, 1, emptyPending.Revision,
            Guid.NewGuid(), emptyPending.SubmissionEvidence!.ReadinessFingerprint!,
            emptyPending.SubmissionEvidence.TargetBaselineFingerprint,
            "late-official-request", mapping, Fingerprint('9'), graph.Now.AddSeconds(5)));
        Assert.False(await reopened.TryPersistTransferLateAcceptanceAsync(
            graph.OperationId, graph.AccountId, 1, emptyPending.Revision,
            emptyPending.SubmissionEvidence.SubmissionId!.Value, Fingerprint('7'),
            emptyPending.SubmissionEvidence.TargetBaselineFingerprint,
            "late-official-request", mapping, Fingerprint('9'), graph.Now.AddSeconds(5)));
        Assert.False(await reopened.TryPersistTransferLateAcceptanceAsync(
            graph.OperationId, graph.AccountId, 1, emptyPending.Revision,
            emptyPending.SubmissionEvidence.SubmissionId!.Value,
            emptyPending.SubmissionEvidence.ReadinessFingerprint!, Fingerprint('6'),
            "late-official-request", mapping, Fingerprint('9'), graph.Now.AddSeconds(5)));
        Assert.False(await reopened.TryPersistTransferLateAcceptanceAsync(
            graph.OperationId, graph.AccountId, 1, emptyPending.Revision - 1,
            emptyPending.SubmissionEvidence.SubmissionId!.Value,
            emptyPending.SubmissionEvidence.ReadinessFingerprint!,
            emptyPending.SubmissionEvidence.TargetBaselineFingerprint,
            "late-official-request", mapping, Fingerprint('9'), graph.Now.AddSeconds(5)));
        Assert.False(await reopened.TryPersistTransferLateAcceptanceAsync(
            graph.OperationId, graph.AccountId, 1, emptyPending.Revision,
            emptyPending.SubmissionEvidence.SubmissionId!.Value,
            emptyPending.SubmissionEvidence.ReadinessFingerprint!,
            emptyPending.SubmissionEvidence.TargetBaselineFingerprint,
            "late-official-request", mapping, Fingerprint('9'), graph.Now.AddSeconds(3)));
        Assert.True(await reopened.TryPersistTransferLateAcceptanceAsync(
            graph.OperationId, graph.AccountId, 1, emptyPending.Revision,
            emptyPending.SubmissionEvidence!.SubmissionId!.Value,
            emptyPending.SubmissionEvidence.ReadinessFingerprint!,
            emptyPending.SubmissionEvidence.TargetBaselineFingerprint,
            "late-official-request", mapping, Fingerprint('9'), graph.Now.AddSeconds(5)));

        var verified = (await new SqliteWebAutomationAttemptStore(graph.Factory)
            .GetAsync(graph.OperationId, graph.AccountId, 1))!;
        Assert.Equal(WebAutomationAttemptState.ResultPending, verified.State);
        Assert.Equal(emptyPending.Revision + 1, verified.Revision);
        Assert.Equal("late-official-request", verified.OfficialRequestId);
        Assert.Equal(mapping.Fingerprint, verified.TransferIdentityMapping!.Fingerprint);
        Assert.Equal(mapping.Entries.ToArray(),
            verified.TransferIdentityMapping.Entries.ToArray());
        Assert.Equal(Fingerprint('9'), verified.OfficialObservationFingerprint);
        Assert.False(await reopened.TryPersistTransferLateAcceptanceAsync(
            graph.OperationId, graph.AccountId, 1, verified.Revision,
            emptyPending.SubmissionEvidence.SubmissionId!.Value,
            emptyPending.SubmissionEvidence.ReadinessFingerprint!,
            emptyPending.SubmissionEvidence.TargetBaselineFingerprint,
            "replacement-request", mapping, Fingerprint('8'), graph.Now.AddSeconds(6)));
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task TransferAcceptanceRejectsUncountedChildEvidenceWithoutChangingParentOrChildren(
        bool late, bool orphanMapping, bool orphanAccepted)
    {
        var graph = await CreateBoundSubmittingAttemptAsync();
        var before = (await graph.Store.GetAsync(graph.OperationId, graph.AccountId, 1))!;
        if (late)
        {
            Assert.True(await graph.Store.TryAdvanceAsync(
                graph.OperationId, graph.AccountId, 1,
                WebAutomationAttemptState.Submitting, before.Revision,
                WebAutomationAttemptState.ResultPending, null, false,
                graph.Now.AddSeconds(4)));
            before = (await graph.Store.GetAsync(graph.OperationId, graph.AccountId, 1))!;
        }
        await InsertUncountedAcceptanceEvidenceAsync(
            graph, orphanMapping, orphanAccepted, ordinal: 100);
        var mappingCount = await CountAcceptanceChildAsync(
            graph, "WebAutomationTransferMappings");
        var acceptedCount = await CountAcceptanceChildAsync(
            graph, "WebAutomationAttemptAcceptedRemoteItems");
        var mapping = new TransferIdentityMapping([new("source", "destination")]);

        var changed = late
            ? await graph.Store.TryPersistTransferLateAcceptanceAsync(
                graph.OperationId, graph.AccountId, 1, before.Revision,
                before.SubmissionEvidence!.SubmissionId!.Value,
                before.SubmissionEvidence.ReadinessFingerprint!,
                before.SubmissionEvidence.TargetBaselineFingerprint,
                "official-request", mapping, Fingerprint('9'), graph.Now.AddSeconds(5))
            : await graph.Store.TryPersistTransferAcceptanceAsync(
                graph.OperationId, graph.AccountId, 1, before.Revision,
                "official-request", mapping, Fingerprint('9'), graph.Now.AddSeconds(5));

        Assert.False(changed);
        Assert.Equal(mappingCount, await CountAcceptanceChildAsync(
            graph, "WebAutomationTransferMappings"));
        Assert.Equal(acceptedCount, await CountAcceptanceChildAsync(
            graph, "WebAutomationAttemptAcceptedRemoteItems"));
        var parent = (await new SqliteWebAutomationAttemptStore(graph.Factory)
            .GetAsync(graph.OperationId, graph.AccountId, 1))!;
        Assert.Equal(before.State, parent.State);
        Assert.Equal(before.Revision, parent.Revision);
        Assert.Null(parent.OfficialRequestId);
        Assert.Null(parent.TransferIdentityMapping);
        Assert.Null(parent.AcceptedRemoteItems);
        Assert.Null(parent.OfficialObservationFingerprint);

        await DeleteAcceptanceChildrenAsync(graph);
        var cleaned = (await new SqliteWebAutomationAttemptStore(graph.Factory)
            .GetAsync(graph.OperationId, graph.AccountId, 1))!;
        Assert.Equal(before.State, cleaned.State);
        Assert.Equal(before.Revision, cleaned.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransferAcceptanceSecondChildInsertFailureRollsBackParentAndFirstInsert(
        bool late)
    {
        var graph = await CreateBoundSubmittingAttemptAsync();
        var before = (await graph.Store.GetAsync(graph.OperationId, graph.AccountId, 1))!;
        if (late)
        {
            Assert.True(await graph.Store.TryAdvanceAsync(
                graph.OperationId, graph.AccountId, 1,
                WebAutomationAttemptState.Submitting, before.Revision,
                WebAutomationAttemptState.ResultPending, null, false,
                graph.Now.AddSeconds(4)));
            before = (await graph.Store.GetAsync(graph.OperationId, graph.AccountId, 1))!;
        }
        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var trigger = connection.CreateCommand())
        {
            trigger.CommandText = """
                CREATE TRIGGER RejectAcceptedEvidenceInsert
                BEFORE INSERT ON WebAutomationAttemptAcceptedRemoteItems
                WHEN NEW.ItemOrdinal=0
                BEGIN SELECT RAISE(ABORT,'synthetic transfer acceptance failure'); END;
                """;
            await trigger.ExecuteNonQueryAsync();
        }
        var mapping = new TransferIdentityMapping([new("source", "destination")]);

        if (late)
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                graph.Store.TryPersistTransferLateAcceptanceAsync(
                    graph.OperationId, graph.AccountId, 1, before.Revision,
                    before.SubmissionEvidence!.SubmissionId!.Value,
                    before.SubmissionEvidence.ReadinessFingerprint!,
                    before.SubmissionEvidence.TargetBaselineFingerprint,
                    "official-request", mapping, Fingerprint('9'), graph.Now.AddSeconds(5)));
        }
        else
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                graph.Store.TryPersistTransferAcceptanceAsync(
                    graph.OperationId, graph.AccountId, 1, before.Revision,
                    "official-request", mapping, Fingerprint('9'), graph.Now.AddSeconds(5)));
        }

        Assert.Equal(0, await CountAcceptanceChildAsync(
            graph, "WebAutomationTransferMappings"));
        Assert.Equal(0, await CountAcceptanceChildAsync(
            graph, "WebAutomationAttemptAcceptedRemoteItems"));
        var parent = (await new SqliteWebAutomationAttemptStore(graph.Factory)
            .GetAsync(graph.OperationId, graph.AccountId, 1))!;
        Assert.Equal(before.State, parent.State);
        Assert.Equal(before.Revision, parent.Revision);
        Assert.Null(parent.OfficialRequestId);
        Assert.Null(parent.TransferIdentityMapping);
        Assert.Null(parent.AcceptedRemoteItems);
        Assert.Null(parent.OfficialObservationFingerprint);
    }

    [Fact]
    public async Task TransferSubmissionRequiresLocalBindingAndRoundTripsTheExactPair()
    {
        var graph = await CreateTargetPreparedAttemptAsync();
        var prepared = (await graph.Store.GetAsync(graph.OperationId, graph.AccountId, 1))!;
        var source = prepared.SourceEvidence!;
        var target = prepared.TargetPreparationEvidence!;
        var unbound = new TransferSubmissionEvidence(
            source.Items.Fingerprint, target.TargetExactId, target.Baseline.Fingerprint,
            source.BaiduUserId, source.Capability, graph.Now.AddSeconds(3));

        Assert.False(await graph.Store.TryPersistTransferSubmissionAsync(
            graph.OperationId, graph.AccountId, 1, prepared.Revision, unbound, graph.Now.AddSeconds(3)));

        var submissionId = Guid.NewGuid();
        var bound = TransferSubmissionEvidence.CreateBound(
            submissionId, Fingerprint('8'), source.Items.Fingerprint, target.TargetExactId,
            target.Baseline.Fingerprint, source.BaiduUserId, source.Capability, graph.Now.AddSeconds(3));
        Assert.True(await graph.Store.TryPersistTransferSubmissionAsync(
            graph.OperationId, graph.AccountId, 1, prepared.Revision, bound, graph.Now.AddSeconds(3)));

        var restored = await new SqliteWebAutomationAttemptStore(graph.Factory)
            .GetAsync(graph.OperationId, graph.AccountId, 1);
        Assert.Equal(submissionId, restored!.SubmissionEvidence!.SubmissionId);
        Assert.Equal(Fingerprint('8'), restored.SubmissionEvidence.ReadinessFingerprint);
        Assert.True(restored.SubmissionEvidence.IsLocallyBound);
    }

    [Fact]
    public async Task OneSidedSubmissionBindingCannotBeLoadedButLegacyUnboundRowsRemainReadable()
    {
        var graph = await CreateBoundSubmittingAttemptAsync();
        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE WebAutomationAttempts SET SubmissionReadinessFingerprint=NULL
                WHERE OperationId=$operation AND AccountId=$account AND Generation=1;
                """;
            command.Parameters.AddWithValue("$operation", graph.OperationId.ToString("D"));
            command.Parameters.AddWithValue("$account", graph.AccountId.ToString("D"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        var reopened = new SqliteWebAutomationAttemptStore(graph.Factory);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            reopened.GetAsync(graph.OperationId, graph.AccountId, 1));
        Assert.Equal("WEB_SUBMISSION_BINDING_MISMATCH", error.Message);

        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE WebAutomationAttempts SET SubmissionId=NULL
                WHERE OperationId=$operation AND AccountId=$account AND Generation=1;
                """;
            command.Parameters.AddWithValue("$operation", graph.OperationId.ToString("D"));
            command.Parameters.AddWithValue("$account", graph.AccountId.ToString("D"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        var legacy = await reopened.GetAsync(graph.OperationId, graph.AccountId, 1);
        Assert.NotNull(legacy);
        Assert.False(legacy.SubmissionEvidence!.IsLocallyBound);
    }

    [Theory]
    [InlineData("SubmissionId")]
    [InlineData("SubmissionReadinessFingerprint")]
    public async Task GenericSuccessRejectsEveryHalfBoundManagedTransferRow(string clearedColumn)
    {
        var (graph, pending, _) = await CreateOfficiallyAcceptedAttemptAsync();
        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                UPDATE WebAutomationAttempts SET {clearedColumn}=NULL
                WHERE OperationId=$operation AND AccountId=$account AND Generation=1;
                """;
            command.Parameters.AddWithValue("$operation", graph.OperationId.ToString("D"));
            command.Parameters.AddWithValue("$account", graph.AccountId.ToString("D"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        Assert.False(await graph.Store.TryAdvanceAsync(
            graph.OperationId, graph.AccountId, 1, WebAutomationAttemptState.ResultPending,
            pending.Revision, WebAutomationAttemptState.Succeeded, Fingerprint('f'), false,
            graph.Now.AddSeconds(5)));

        await using var reopened = await graph.Factory.OpenConnectionAsync();
        await using var verify = reopened.CreateCommand();
        verify.CommandText = """
            SELECT COUNT(*) FROM WebAutomationAttempts
            WHERE OperationId=$operation AND AccountId=$account AND Generation=1
              AND State='ResultPending' AND Revision=$revision;
            """;
        verify.Parameters.AddWithValue("$operation", graph.OperationId.ToString("D"));
        verify.Parameters.AddWithValue("$account", graph.AccountId.ToString("D"));
        verify.Parameters.AddWithValue("$revision", pending.Revision);
        Assert.Equal(1L, Convert.ToInt64(await verify.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task KnownNotDispatchedResolutionRequiresExactDurableSubmissionCasAndIsIdempotent()
    {
        var graph = await CreateBoundSubmittingAttemptAsync();
        var current = (await graph.Store.GetAsync(graph.OperationId, graph.AccountId, 1))!;
        var submission = current.SubmissionEvidence!;

        Assert.False(await graph.Store.TryResolveTransferKnownNotDispatchedAsync(
            graph.OperationId, graph.AccountId, 1, current.Revision,
            Guid.NewGuid(), submission.ReadinessFingerprint!, graph.Now.AddSeconds(4)));
        Assert.False(await new SqliteWebAutomationAttemptStore(graph.Factory)
            .TryResolveTransferKnownNotDispatchedAsync(
                graph.OperationId, graph.AccountId, 1, current.Revision,
                submission.SubmissionId!.Value, Fingerprint('9'), graph.Now.AddSeconds(4)));
        var rejected = await new SqliteWebAutomationAttemptStore(graph.Factory)
            .GetAsync(graph.OperationId, graph.AccountId, 1);
        Assert.Equal(WebAutomationAttemptState.Submitting, rejected!.State);
        Assert.Equal(current.Revision, rejected.Revision);

        Assert.True(await graph.Store.TryResolveTransferKnownNotDispatchedAsync(
            graph.OperationId, graph.AccountId, 1, current.Revision,
            submission.SubmissionId.Value, submission.ReadinessFingerprint!, graph.Now.AddSeconds(4)));
        Assert.False(await new SqliteWebAutomationAttemptStore(graph.Factory)
            .TryResolveTransferKnownNotDispatchedAsync(
                graph.OperationId, graph.AccountId, 1, current.Revision,
                submission.SubmissionId.Value, submission.ReadinessFingerprint!, graph.Now.AddSeconds(4)));
        var restored = await new SqliteWebAutomationAttemptStore(graph.Factory)
            .GetAsync(graph.OperationId, graph.AccountId, 1);
        Assert.Equal(WebAutomationAttemptState.DefinitelyNotSubmitted, restored!.State);
        Assert.Equal(current.Revision + 1, restored.Revision);
        Assert.Equal(submission, restored.SubmissionEvidence);
    }

    [Fact]
    public async Task TargetConfirmationAloneFinalizesOfficiallyBoundTransferAndSeparatesObservations()
    {
        var (graph, pending, mapping) = await CreateOfficiallyAcceptedAttemptAsync();
        Assert.False(await graph.Store.TryAdvanceAsync(
            graph.OperationId, graph.AccountId, 1, WebAutomationAttemptState.ResultPending,
            pending.Revision, WebAutomationAttemptState.Succeeded, Fingerprint('f'), false,
            graph.Now.AddSeconds(5)));
        var confirmation = TransferTargetConfirmationEvidence.Create(
            pending.SubmissionEvidence!, pending.OfficialRequestId!, mapping,
            pending.TargetPreparationEvidence!, ExactBaseline("before", "destination"),
            Fingerprint('4'), graph.Now.AddSeconds(5));

        Assert.True(await graph.Store.TryPersistTransferTargetConfirmationAsync(
            graph.OperationId, graph.AccountId, 1, pending.Revision, confirmation));
        Assert.False(await new SqliteWebAutomationAttemptStore(graph.Factory)
            .TryPersistTransferTargetConfirmationAsync(
                graph.OperationId, graph.AccountId, 1, pending.Revision, confirmation));

        var restored = await new SqliteWebAutomationAttemptStore(graph.Factory)
            .GetAsync(graph.OperationId, graph.AccountId, 1);
        Assert.Equal(WebAutomationAttemptState.Succeeded, restored!.State);
        Assert.Equal(Fingerprint('3'), restored.OfficialObservationFingerprint);
        Assert.Equal(Fingerprint('4'), restored.ObservationFingerprint);
        Assert.Equal(confirmation, restored.TargetConfirmationEvidence);
        Assert.Equal(pending.Revision + 1, restored.Revision);
    }

    [Fact]
    public async Task TargetConfirmationCannotReplaceOfficialEvidenceOrUseAnotherSubmissionBinding()
    {
        var (graph, pending, mapping) = await CreateOfficiallyAcceptedAttemptAsync();
        var differentSubmission = TransferSubmissionEvidence.CreateBound(
            pending.SubmissionEvidence!.SubmissionId!.Value, Fingerprint('9'),
            pending.SubmissionEvidence.SourceFingerprint, pending.SubmissionEvidence.TargetExactId,
            pending.SubmissionEvidence.TargetBaselineFingerprint, pending.SubmissionEvidence.BaiduUserId,
            pending.SubmissionEvidence.Capability, pending.SubmissionEvidence.MarkedSubmittingAtUtc);
        var rejected = TransferTargetConfirmationEvidence.Create(
            differentSubmission, pending.OfficialRequestId!, mapping, pending.TargetPreparationEvidence!,
            ExactBaseline("before", "destination"), Fingerprint('4'), graph.Now.AddSeconds(5));

        Assert.False(await graph.Store.TryPersistTransferTargetConfirmationAsync(
            graph.OperationId, graph.AccountId, 1, pending.Revision, rejected));

        var restored = await new SqliteWebAutomationAttemptStore(graph.Factory)
            .GetAsync(graph.OperationId, graph.AccountId, 1);
        Assert.Equal(WebAutomationAttemptState.ResultPending, restored!.State);
        Assert.Equal("official-request", restored.OfficialRequestId);
        Assert.Equal(mapping.Fingerprint, restored.TransferIdentityMapping!.Fingerprint);
        Assert.Equal(Fingerprint('3'), restored.OfficialObservationFingerprint);
        Assert.Null(restored.TargetConfirmationEvidence);
    }

    [Fact]
    public async Task LegacyTargetFingerprintCannotBeLoadedAsAnAllItemsBaseline()
    {
        var graph = await CreateTargetPreparedAttemptAsync();
        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE WebAutomationAttempts
                SET TargetObservationFingerprint=$legacy,ObservationFingerprint=$legacy
                WHERE OperationId=$operation AND AccountId=$account AND Generation=1;
                """;
            command.Parameters.AddWithValue("$legacy", Fingerprint('2'));
            command.Parameters.AddWithValue("$operation", graph.OperationId.ToString("D"));
            command.Parameters.AddWithValue("$account", graph.AccountId.ToString("D"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SqliteWebAutomationAttemptStore(graph.Factory).GetAsync(
                graph.OperationId, graph.AccountId, 1));

        Assert.Equal("WEB_TARGET_OBSERVATION_FINGERPRINT_MISMATCH", error.Message);
    }

    [Fact]
    public async Task TargetCountMismatchCannotBeLoadedAsACompleteBaseline()
    {
        var graph = await CreateTargetPreparedAttemptAsync();
        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE WebAutomationAttempts SET TargetTotalCount=2
                WHERE OperationId=$operation AND AccountId=$account AND Generation=1;
                """;
            command.Parameters.AddWithValue("$operation", graph.OperationId.ToString("D"));
            command.Parameters.AddWithValue("$account", graph.AccountId.ToString("D"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SqliteWebAutomationAttemptStore(graph.Factory).GetAsync(
                graph.OperationId, graph.AccountId, 1));

        Assert.Equal("WEB_TARGET_COUNT_MISMATCH", error.Message);
    }

    [Theory]
    [InlineData("TargetExactId", " target-directory ", "WEB_TARGET_EXACT_ID_NOT_CANONICAL")]
    [InlineData("TargetPath", "/自动转存//游戏", "WEB_TARGET_PATH_NOT_CANONICAL")]
    [InlineData("TargetPath", "\\\\自动转存\\\\游戏", "WEB_TARGET_PATH_NOT_CANONICAL")]
    [InlineData("TargetGenerationToken", " target-generation ", "WEB_TARGET_GENERATION_NOT_CANONICAL")]
    [InlineData("BaiduUserId", " baidu-user ", "WEB_TARGET_UID_NOT_CANONICAL")]
    public async Task NonCanonicalRawTargetFieldsCannotReuseTheCanonicalFingerprint(
        string columnName,
        string mutatedValue,
        string expectedError)
    {
        var graph = await CreateTargetPreparedAttemptAsync();
        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                UPDATE WebAutomationAttempts SET {columnName}=$mutated
                WHERE OperationId=$operation AND AccountId=$account AND Generation=1;
                """;
            command.Parameters.AddWithValue("$mutated", mutatedValue);
            command.Parameters.AddWithValue("$operation", graph.OperationId.ToString("D"));
            command.Parameters.AddWithValue("$account", graph.AccountId.ToString("D"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SqliteWebAutomationAttemptStore(graph.Factory).GetAsync(
                graph.OperationId, graph.AccountId, 1));

        Assert.Equal(expectedError, error.Message);
        Assert.DoesNotContain(mutatedValue, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnresolvedDirectoryMutationBlocksSameTargetAcrossGenerationsUntilConfirmed()
    {
        var graph = await CreateAsync();
        var capability = Capability();
        await SetTransferGenerationAsync(graph, 1);
        Assert.True(await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 1, WebAutomationOwnerKind.Transfer, graph.Now)));
        Assert.True(await graph.Store.TryPersistTransferSourceEvidenceAsync(
            graph.OperationId, graph.AccountId, 1, 0,
            SourceEvidence(capability, '4'), graph.Now.AddSeconds(1)));
        var first = Mutation(graph, 1, 1, capability, TransferDirectoryMutationState.Intent);
        Assert.True(await graph.Store.TryBeginDirectoryMutationAsync(first, graph.Now.AddSeconds(2)));

        await SetTransferGenerationAsync(graph, 2);
        Assert.True(await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 2, WebAutomationOwnerKind.Transfer, graph.Now.AddMinutes(1))));
        Assert.True(await graph.Store.TryPersistTransferSourceEvidenceAsync(
            graph.OperationId, graph.AccountId, 2, 0,
            SourceEvidence(capability, '5'), graph.Now.AddMinutes(1).AddSeconds(1)));
        var second = Mutation(graph, 2, 1, capability, TransferDirectoryMutationState.Intent);
        Assert.False(await graph.Store.TryBeginDirectoryMutationAsync(second, graph.Now.AddMinutes(1).AddSeconds(2)));

        Assert.True(await graph.Store.TryResolveDirectoryMutationAsync(
            first, TransferDirectoryMutationState.OutcomeUnknown, null, graph.Now.AddSeconds(3)));
        var unresolved = await graph.Store.GetUnresolvedDirectoryMutationAsync(
            graph.OperationId, graph.AccountId, "root", "游戏");
        Assert.NotNull(unresolved);
        Assert.Equal(1, unresolved.Generation);
        Assert.Equal(1, unresolved.OwningAttemptRevision);
        Assert.False(await graph.Store.TryBeginDirectoryMutationAsync(second, graph.Now.AddMinutes(1).AddSeconds(3)));

        var unknown = unresolved;
        Assert.True(await graph.Store.TryResolveDirectoryMutationAsync(
            unknown, TransferDirectoryMutationState.Confirmed, "created-directory",
            graph.Now.AddMinutes(1).AddSeconds(4)));
        Assert.True(await graph.Store.TryBeginDirectoryMutationAsync(second, graph.Now.AddMinutes(1).AddSeconds(5)));
        Assert.Equal(2, (await graph.Store.GetDirectoryMutationsAsync(
            graph.OperationId, graph.AccountId)).Count);
    }

    [Fact]
    public async Task TransferBaseline_RoundTripsExactPreSubmitDirectorySet()
    {
        var graph = await CreateAsync();
        await SetTransferGenerationAsync(graph, 2);
        var attempt = WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 2, WebAutomationOwnerKind.Transfer, graph.Now);

        Assert.True(await graph.Store.BeginAsync(attempt));
        Assert.True(await graph.Store.TryPersistBaselineAsync(
            graph.OperationId, graph.AccountId, 2, 0,
            ExactBaseline("before-2", "before-1"), Fingerprint('a'), graph.Now.AddSeconds(1)));

        var restored = await new SqliteWebAutomationAttemptStore(graph.Factory)
            .GetAsync(graph.OperationId, graph.AccountId, 2);

        Assert.NotNull(restored);
        Assert.Equal(WebAutomationAttemptState.BaselinePersisted, restored.State);
        Assert.Equal(
            new[] { "before-1", "before-2" },
            restored.BaselineRemoteItems.Select(item => item.ExactId).Order());
    }

    [Fact]
    public async Task OfficialAcceptanceRoundTripsExactTargetIdsBeforeSucceededTransition()
    {
        var graph = await CreateAsync();
        await SetTransferGenerationAsync(graph, 2);
        Assert.True(await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 2, WebAutomationOwnerKind.Transfer, graph.Now)));
        Assert.True(await graph.Store.TryPersistBaselineAsync(
            graph.OperationId, graph.AccountId, 2, 0,
            ExactBaseline("before"), Fingerprint('a'), graph.Now.AddSeconds(1)));
        Assert.True(await graph.Store.TryAdvanceAsync(
            graph.OperationId, graph.AccountId, 2,
            WebAutomationAttemptState.BaselinePersisted, 1,
            WebAutomationAttemptState.Submitting, Fingerprint('b'), false, graph.Now.AddSeconds(2)));
        var accepted = new RemoteItemIdentitySet([
            new("target-2", "显示名不应持久化"),
            new("target-1", "另一个显示名"),
        ]);

        Assert.True(await graph.Store.TryPersistOfficialAcceptanceAsync(
            graph.OperationId, graph.AccountId, 2, 2,
            accepted, Fingerprint('c'), graph.Now.AddSeconds(3)));

        var restored = await graph.Store.GetAsync(graph.OperationId, graph.AccountId, 2);
        Assert.Equal(WebAutomationAttemptState.ResultPending, restored!.State);
        Assert.Equal(accepted, restored.AcceptedRemoteItems);
        Assert.Equal(["target-1", "target-2"],
            restored.AcceptedRemoteItems!.Items.Select(item => item.ExactId));
        await using var connection = await graph.Factory.OpenConnectionAsync();
        await using var columns = connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info(WebAutomationAttemptAcceptedRemoteItems);";
        var names = new List<string>();
        await using var reader = await columns.ExecuteReaderAsync();
        while (await reader.ReadAsync()) names.Add(reader.GetString(1));
        Assert.DoesNotContain("DisplayName", names);
    }

    [Theory]
    [InlineData(WebAutomationOwnerKind.TransferShare)]
    [InlineData(WebAutomationOwnerKind.StandaloneShare)]
    public async Task ShareOwnersPersistExactAcceptanceBeforeSucceededTransition(
        WebAutomationOwnerKind ownerKind)
    {
        var graph = await CreateAsync();
        var operationId = ownerKind == WebAutomationOwnerKind.TransferShare
            ? graph.ShareOperationId
            : graph.StandaloneOperationId;
        Assert.True(await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            operationId, graph.AccountId, 1, ownerKind, graph.Now)));
        Assert.True(await graph.Store.TryPersistBaselineAsync(
            operationId, graph.AccountId, 1, 0,
            ExactBaseline("existing-share"), Fingerprint('d'), graph.Now.AddSeconds(1)));
        Assert.True(await graph.Store.TryAdvanceAsync(
            operationId, graph.AccountId, 1,
            WebAutomationAttemptState.BaselinePersisted, 1,
            WebAutomationAttemptState.Submitting, Fingerprint('e'), false, graph.Now.AddSeconds(2)));
        Assert.False(await graph.Store.TryAdvanceAsync(
            operationId, graph.AccountId, 1,
            WebAutomationAttemptState.ResultPending, 2,
            WebAutomationAttemptState.Succeeded, null, false, graph.Now.AddSeconds(3)));

        var accepted = new RemoteItemIdentitySet([new("exact-share-target", null)]);
        Assert.True(await graph.Store.TryPersistOfficialAcceptanceAsync(
            operationId, graph.AccountId, 1, 2,
            accepted, Fingerprint('f'), graph.Now.AddSeconds(3)));
        Assert.True(await graph.Store.TryAdvanceAsync(
            operationId, graph.AccountId, 1,
            WebAutomationAttemptState.ResultPending, 3,
            WebAutomationAttemptState.Succeeded, null, false, graph.Now.AddSeconds(4)));

        var restored = await graph.Store.GetAsync(operationId, graph.AccountId, 1);
        Assert.Equal(WebAutomationAttemptState.Succeeded, restored!.State);
        Assert.Equal(accepted, restored.AcceptedRemoteItems);
    }

    [Fact]
    public async Task OfficialAcceptancePartialWriteFailureRollsBackWholeExactSetAndStage()
    {
        var graph = await CreateSubmittingAttemptAsync();
        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var trigger = connection.CreateCommand())
        {
            trigger.CommandText = """
                CREATE TRIGGER RejectSecondAcceptedItem
                BEFORE INSERT ON WebAutomationAttemptAcceptedRemoteItems
                WHEN NEW.ItemOrdinal=1
                BEGIN SELECT RAISE(ABORT,'synthetic acceptance failure'); END;
                """;
            await trigger.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAnyAsync<Exception>(() => graph.Store.TryPersistOfficialAcceptanceAsync(
            graph.OperationId, graph.AccountId, 2, 2,
            new RemoteItemIdentitySet([new("target-1", null), new("target-2", null)]),
            Fingerprint('c'), graph.Now.AddSeconds(3)));

        var restored = await graph.Store.GetAsync(graph.OperationId, graph.AccountId, 2);
        Assert.Equal(WebAutomationAttemptState.Submitting, restored!.State);
        Assert.Null(restored.AcceptedRemoteItems);
        Assert.Equal(0L, await ScalarAsync(graph.Factory,
            "SELECT COUNT(*) FROM WebAutomationAttemptAcceptedRemoteItems;"));
    }

    [Fact]
    public async Task OfficialAcceptanceRejectsWrongIdentityAndStaleGenerationWithoutWritingEvidence()
    {
        var graph = await CreateSubmittingAttemptAsync();
        var accepted = new RemoteItemIdentitySet([new("target", null)]);

        Assert.False(await graph.Store.TryPersistOfficialAcceptanceAsync(
            Guid.NewGuid(), graph.AccountId, 2, 2, accepted, Fingerprint('c'), graph.Now.AddSeconds(3)));
        Assert.False(await graph.Store.TryPersistOfficialAcceptanceAsync(
            graph.OperationId, graph.OtherAccountId, 2, 2, accepted, Fingerprint('c'), graph.Now.AddSeconds(3)));
        Assert.False(await graph.Store.TryPersistOfficialAcceptanceAsync(
            graph.OperationId, graph.AccountId, 1, 2, accepted, Fingerprint('c'), graph.Now.AddSeconds(3)));

        Assert.Equal(0L, await ScalarAsync(graph.Factory,
            "SELECT COUNT(*) FROM WebAutomationAttemptAcceptedRemoteItems;"));
        Assert.Equal(WebAutomationAttemptState.Submitting,
            (await graph.Store.GetAsync(graph.OperationId, graph.AccountId, 2))!.State);
    }

    [Fact]
    public async Task BaselineReadPagesPastTheOldPerQueryBoundaryWithoutTruncation()
    {
        var graph = await CreateAsync(baselineReadPageSize: 2);
        await SetTransferGenerationAsync(graph, 1);
        var ids = Enumerable.Range(1, 5).Select(index => $"before-{index}").ToArray();
        await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 1, WebAutomationOwnerKind.Transfer, graph.Now));
        Assert.True(await graph.Store.TryPersistBaselineAsync(
            graph.OperationId, graph.AccountId, 1, 0,
            ExactBaseline(ids), Fingerprint('d'), graph.Now.AddSeconds(1)));

        var reopened = new SqliteWebAutomationAttemptStore(
            graph.Factory, maximumBaselineItemCount: 100, baselineReadPageSize: 2);
        var restored = await reopened.GetAsync(graph.OperationId, graph.AccountId, 1);

        Assert.Equal(ids, restored!.BaselineRemoteItems.Select(item => item.ExactId));
        Assert.Equal(5, restored.Baseline!.Items.Length);
    }

    [Fact]
    public async Task OversizedBaselineIsRejectedBeforeAnyEvidenceIsWritten()
    {
        var graph = await CreateAsync(maximumBaselineItemCount: 2);
        await SetTransferGenerationAsync(graph, 1);
        await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 1, WebAutomationOwnerKind.Transfer, graph.Now));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => graph.Store.TryPersistBaselineAsync(
            graph.OperationId, graph.AccountId, 1, 0,
            ExactBaseline("one", "two", "three"), Fingerprint('e'), graph.Now.AddSeconds(1)));

        var unchanged = await graph.Store.GetAsync(graph.OperationId, graph.AccountId, 1);
        Assert.Equal(WebAutomationAttemptState.Preparing, unchanged!.State);
        Assert.Equal(0, unchanged.Revision);
        Assert.Null(unchanged.Baseline);
        Assert.Equal(0L, await ScalarAsync(graph.Factory,
            "SELECT COUNT(*) FROM WebAutomationAttemptRemoteItems;"));
    }

    [Fact]
    public async Task CorruptCanonicalSortKeyCannotMasqueradeAsAnExactBaseline()
    {
        var graph = await CreateAsync();
        await SetTransferGenerationAsync(graph, 1);
        await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 1, WebAutomationOwnerKind.Transfer, graph.Now));
        Assert.True(await graph.Store.TryPersistBaselineAsync(
            graph.OperationId, graph.AccountId, 1, 0,
            ExactBaseline("before"), Fingerprint('f'), graph.Now.AddSeconds(1)));
        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE WebAutomationAttemptRemoteItems SET CanonicalSortKey=X'00';";
            await command.ExecuteNonQueryAsync();
        }

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SqliteWebAutomationAttemptStore(graph.Factory).GetAsync(
                graph.OperationId, graph.AccountId, 1));

        Assert.Equal("WEB_BASELINE_CANONICAL_SORT_KEY_MISMATCH", error.Message);
    }

    [Fact]
    public async Task BaselineSchemaAndModelDoNotRetainRemoteDisplayNames()
    {
        var graph = await CreateAsync();
        await SetTransferGenerationAsync(graph, 2);
        var baseline = ExactBaseline("private-item");
        await using var connection = await graph.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(WebAutomationAttemptRemoteItems);";
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(1));

        Assert.DoesNotContain("DisplayName", columns, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            baseline.Items.Single().GetType().GetProperties(),
            property => property.Name.Contains("Display", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Name", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BeginAcceptsEveryOwnerKindOnlyForItsAuthoritativeBusinessRow()
    {
        var graph = await CreateAsync();
        await SetTransferGenerationAsync(graph, 2);

        Assert.True(await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 2, WebAutomationOwnerKind.Transfer, graph.Now)));
        Assert.True(await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.ShareOperationId, graph.AccountId, 1, WebAutomationOwnerKind.TransferShare, graph.Now)));
        Assert.True(await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.StandaloneOperationId, graph.AccountId, 1, WebAutomationOwnerKind.StandaloneShare, graph.Now)));
    }

    [Fact]
    public async Task LoadFailsClosedWhenThePersistedAttemptNoLongerMatchesItsParentOwnerTuple()
    {
        var graph = await CreateAsync();
        await SetTransferGenerationAsync(graph, 1);
        Assert.True(await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 1, WebAutomationOwnerKind.Transfer, graph.Now)));

        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                DROP TRIGGER TR_WebAutomationAttempts_ExecutionSnapshotImmutable;
                DROP TRIGGER TR_WebAutomationAttempts_ExactOwnerSnapshot_Update;
                PRAGMA ignore_check_constraints=ON;
                UPDATE WebAutomationAttempts SET ProfileId='corrupt-profile'
                WHERE OperationId=$operation AND AccountId=$account AND Generation=1;
                """;
            command.Parameters.AddWithValue("$operation", graph.OperationId.ToString("D"));
            command.Parameters.AddWithValue("$account", graph.AccountId.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SqliteWebAutomationAttemptStore(graph.Factory).GetAsync(
                graph.OperationId, graph.AccountId, 1));

        Assert.Equal("WEB_ATTEMPT_OWNER_SNAPSHOT_MISMATCH", error.Message);
    }

    [Fact]
    public async Task RequestOwnershipRequiresTheExactAuthoritativeBatchAccountAndGeneration()
    {
        var graph = await CreateAsync();
        await SetTransferGenerationAsync(graph, 7);
        var ownership = new SqliteWebAutomationRequestOwnershipStore(graph.Factory);

        Assert.True(await ownership.IsBoundAsync(
            graph.OperationId, graph.BatchId, graph.AccountId, 7, WebAutomationOwnerKind.Transfer));
        Assert.True(await ownership.IsBoundAsync(
            graph.ShareOperationId, graph.BatchId, graph.AccountId, 1, WebAutomationOwnerKind.TransferShare));
        Assert.True(await ownership.IsBoundAsync(
            graph.StandaloneOperationId, graph.StandaloneBatchId, graph.AccountId, 1,
            WebAutomationOwnerKind.StandaloneShare));
        Assert.False(await ownership.IsBoundAsync(
            graph.OperationId, Guid.NewGuid(), graph.AccountId, 7, WebAutomationOwnerKind.Transfer));
        Assert.False(await ownership.IsBoundAsync(
            graph.ShareOperationId, graph.BatchId, graph.AccountId, 2, WebAutomationOwnerKind.TransferShare));
        Assert.False(await ownership.IsBoundAsync(
            graph.StandaloneOperationId, graph.StandaloneBatchId, graph.OtherAccountId, 1,
            WebAutomationOwnerKind.StandaloneShare));
    }

    [Fact]
    public async Task TransferRequestAndAttemptRequireCurrentAuthoritativeAttemptCount()
    {
        var graph = await CreateAsync();
        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE TransferOperations SET AttemptCount=2 WHERE Id=$operation;";
            command.Parameters.AddWithValue("$operation", graph.OperationId.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }
        var ownership = new SqliteWebAutomationRequestOwnershipStore(graph.Factory);

        Assert.True(await ownership.IsBoundAsync(
            graph.OperationId, graph.BatchId, graph.AccountId, 2, WebAutomationOwnerKind.Transfer));
        Assert.False(await ownership.IsBoundAsync(
            graph.OperationId, graph.BatchId, graph.AccountId, 1, WebAutomationOwnerKind.Transfer));
        Assert.False(await ownership.IsBoundAsync(
            graph.OperationId, graph.BatchId, graph.AccountId, 3, WebAutomationOwnerKind.Transfer));
        Assert.False(await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 1, WebAutomationOwnerKind.Transfer, graph.Now)));
        Assert.True(await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 2, WebAutomationOwnerKind.Transfer, graph.Now)));
        Assert.False(await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 3, WebAutomationOwnerKind.Transfer, graph.Now)));

        await SetTransferGenerationAsync(graph, 3);
        var restartedOwnership = new SqliteWebAutomationRequestOwnershipStore(graph.Factory);
        var restartedAttempts = new SqliteWebAutomationAttemptStore(graph.Factory);
        Assert.False(await restartedOwnership.IsBoundAsync(
            graph.OperationId, graph.BatchId, graph.AccountId, 2, WebAutomationOwnerKind.Transfer));
        Assert.False(await restartedAttempts.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 1, WebAutomationOwnerKind.Transfer, graph.Now)));
        Assert.True(await restartedOwnership.IsBoundAsync(
            graph.OperationId, graph.BatchId, graph.AccountId, 3, WebAutomationOwnerKind.Transfer));
    }

    [Theory]
    [InlineData(WebAutomationOwnerKind.Transfer)]
    [InlineData(WebAutomationOwnerKind.TransferShare)]
    [InlineData(WebAutomationOwnerKind.StandaloneShare)]
    public async Task BeginRejectsWrongAccountAndNonexistentBusinessOwner(WebAutomationOwnerKind ownerKind)
    {
        var graph = await CreateAsync();
        var operationId = ownerKind switch
        {
            WebAutomationOwnerKind.Transfer => graph.OperationId,
            WebAutomationOwnerKind.TransferShare => graph.ShareOperationId,
            WebAutomationOwnerKind.StandaloneShare => graph.StandaloneOperationId,
            _ => throw new InvalidOperationException(),
        };

        Assert.False(await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            operationId, graph.OtherAccountId, 1, ownerKind, graph.Now)));
        Assert.False(await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            Guid.NewGuid(), graph.AccountId, 1, ownerKind, graph.Now)));
    }

    [Theory]
    [InlineData(WebAutomationAttemptState.Preparing)]
    [InlineData(WebAutomationAttemptState.BaselinePersisted)]
    [InlineData(WebAutomationAttemptState.Submitting)]
    [InlineData(WebAutomationAttemptState.ResultPending)]
    public async Task UserHandoffAfterRestartCanResumeOnlyItsExactPriorStage(
        WebAutomationAttemptState priorStage)
    {
        var graph = await CreateAsync();
        var generation = (int)priorStage + 20;
        var beforeHandoff = await MoveToStageAsync(graph, generation, priorStage);
        Assert.True(await graph.Store.TryAdvanceAsync(
            graph.OperationId, graph.AccountId, generation, priorStage, beforeHandoff.Revision,
            WebAutomationAttemptState.UserHandoff, Fingerprint('7'), true,
            graph.Now.AddMinutes(1)));

        var reopened = new SqliteWebAutomationAttemptStore(graph.Factory);
        var handoff = (await reopened.GetAsync(graph.OperationId, graph.AccountId, generation))!;
        Assert.Equal(WebAutomationAttemptState.UserHandoff, handoff.State);
        Assert.Equal(priorStage, handoff.ResumeStage);
        Assert.Equal(beforeHandoff.Baseline?.Fingerprint, handoff.Baseline?.Fingerprint);
        var wrongStage = priorStage == WebAutomationAttemptState.Preparing
            ? WebAutomationAttemptState.BaselinePersisted
            : WebAutomationAttemptState.Preparing;
        Assert.False(await reopened.TryResumeFromUserHandoffAsync(
            graph.OperationId, graph.AccountId, generation, handoff.Revision,
            wrongStage, Fingerprint('8'), graph.Now.AddMinutes(2)));
        Assert.False(await reopened.TryResumeFromUserHandoffAsync(
            graph.OperationId, graph.AccountId, generation, handoff.Revision - 1,
            priorStage, Fingerprint('8'), graph.Now.AddMinutes(2)));
        Assert.False(await reopened.TryResumeFromUserHandoffAsync(
            graph.OperationId, graph.OtherAccountId, generation, handoff.Revision,
            priorStage, Fingerprint('8'), graph.Now.AddMinutes(2)));
        Assert.False(await reopened.TryResumeFromUserHandoffAsync(
            graph.OperationId, graph.AccountId, generation + 1, handoff.Revision,
            priorStage, Fingerprint('8'), graph.Now.AddMinutes(2)));
        Assert.False(await reopened.TryAdvanceAsync(
            graph.OperationId, graph.AccountId, generation, WebAutomationAttemptState.UserHandoff,
            handoff.Revision, WebAutomationAttemptState.Succeeded, Fingerprint('8'), false,
            graph.Now.AddMinutes(2)));

        Assert.True(await reopened.TryResumeFromUserHandoffAsync(
            graph.OperationId, graph.AccountId, generation, handoff.Revision,
            priorStage, Fingerprint('8'), graph.Now.AddMinutes(2)));
        var resumed = (await reopened.GetAsync(graph.OperationId, graph.AccountId, generation))!;
        Assert.Equal(priorStage, resumed.State);
        Assert.Null(resumed.ResumeStage);
        Assert.False(resumed.IsWaitingForUser);
        Assert.Equal(beforeHandoff.Baseline?.Fingerprint, resumed.Baseline?.Fingerprint);
    }

    [Theory]
    [InlineData(WebAutomationAttemptState.Preparing, WebAutomationAttemptState.DefinitelyNotSubmitted)]
    [InlineData(WebAutomationAttemptState.BaselinePersisted, WebAutomationAttemptState.DefinitelyNotSubmitted)]
    [InlineData(WebAutomationAttemptState.Submitting, WebAutomationAttemptState.NeedsDecision)]
    [InlineData(WebAutomationAttemptState.ResultPending, WebAutomationAttemptState.NeedsDecision)]
    public async Task UserHandoffSkipAtomicallyPersistsOnlyItsSafeTerminalState(
        WebAutomationAttemptState priorStage,
        WebAutomationAttemptState terminalState)
    {
        var graph = await CreateAsync();
        var generation = (int)priorStage + 40;
        var beforeHandoff = await MoveToStageAsync(graph, generation, priorStage);
        Assert.True(await graph.Store.TryAdvanceAsync(
            graph.OperationId, graph.AccountId, generation, priorStage, beforeHandoff.Revision,
            WebAutomationAttemptState.UserHandoff, Fingerprint('9'), true,
            graph.Now.AddMinutes(1)));
        var handoff = (await graph.Store.GetAsync(
            graph.OperationId, graph.AccountId, generation))!;
        var unsafeTerminal = terminalState == WebAutomationAttemptState.NeedsDecision
            ? WebAutomationAttemptState.DefinitelyNotSubmitted
            : WebAutomationAttemptState.NeedsDecision;

        Assert.False(await graph.Store.TryResolveUserHandoffAsync(
            graph.OperationId, graph.AccountId, generation, handoff.Revision,
            priorStage, unsafeTerminal, Fingerprint('a'), graph.Now.AddMinutes(2)));
        Assert.False(await graph.Store.TryResolveUserHandoffAsync(
            graph.OperationId, graph.OtherAccountId, generation, handoff.Revision,
            priorStage, terminalState, Fingerprint('a'), graph.Now.AddMinutes(2)));
        Assert.True(await graph.Store.TryResolveUserHandoffAsync(
            graph.OperationId, graph.AccountId, generation, handoff.Revision,
            priorStage, terminalState, Fingerprint('a'), graph.Now.AddMinutes(2)));
        Assert.False(await graph.Store.TryResolveUserHandoffAsync(
            graph.OperationId, graph.AccountId, generation, handoff.Revision,
            priorStage, terminalState, Fingerprint('a'), graph.Now.AddMinutes(2)));

        var restored = await new SqliteWebAutomationAttemptStore(graph.Factory)
            .GetAsync(graph.OperationId, graph.AccountId, generation);
        Assert.Equal(terminalState, restored!.State);
        Assert.Null(restored.ResumeStage);
        Assert.False(restored.IsWaitingForUser);
    }

    [Fact]
    public async Task Advance_RejectsOldGenerationWrongAccountStaleRevisionAndInvalidTransition()
    {
        var graph = await CreateAsync();
        await SetTransferGenerationAsync(graph, 2);
        await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 2, WebAutomationOwnerKind.Transfer, graph.Now));

        Assert.False(await graph.Store.TryAdvanceAsync(
            graph.OperationId, graph.AccountId, 1, WebAutomationAttemptState.Preparing, 0,
            WebAutomationAttemptState.DefinitelyNotSubmitted, Fingerprint('b'), false, graph.Now));
        Assert.False(await graph.Store.TryAdvanceAsync(
            graph.OperationId, Guid.NewGuid(), 2, WebAutomationAttemptState.Preparing, 0,
            WebAutomationAttemptState.DefinitelyNotSubmitted, Fingerprint('b'), false, graph.Now));
        Assert.False(await graph.Store.TryAdvanceAsync(
            graph.OperationId, graph.AccountId, 2, WebAutomationAttemptState.Preparing, 1,
            WebAutomationAttemptState.DefinitelyNotSubmitted, Fingerprint('b'), false, graph.Now));
        Assert.False(await graph.Store.TryAdvanceAsync(
            graph.OperationId, graph.AccountId, 2, WebAutomationAttemptState.Preparing, 0,
            WebAutomationAttemptState.Succeeded, Fingerprint('b'), false, graph.Now));
    }

    [Fact]
    public async Task ResultCannotSucceedFromFingerprintWithoutExactPersistedBaseline()
    {
        var graph = await CreateAsync();
        await SetTransferGenerationAsync(graph, 1);
        await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 1, WebAutomationOwnerKind.Transfer, graph.Now));

        var changed = await graph.Store.TryAdvanceAsync(
            graph.OperationId, graph.AccountId, 1, WebAutomationAttemptState.Preparing, 0,
            WebAutomationAttemptState.Succeeded, Fingerprint('c'), false, graph.Now.AddMinutes(1));

        Assert.False(changed);
        Assert.Equal(WebAutomationAttemptState.Preparing,
            (await graph.Store.GetAsync(graph.OperationId, graph.AccountId, 1))!.State);
    }

    [Fact]
    public async Task RecoveryReadsAreBoundedAndModelsRedactEvidence()
    {
        var graph = await CreateAsync();
        for (var index = 0; index < 3; index++)
        {
            await SetTransferGenerationAsync(graph, index + 1);
            await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
                graph.OperationId, graph.AccountId, index + 1, WebAutomationOwnerKind.Transfer,
                graph.Now.AddSeconds(index)));
        }

        var page = await graph.Store.GetRecoveryPageAsync(new PageQuery(1, 2));

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(3, page.TotalCount);
        var text = page.Items[0].ToString();
        Assert.Contains("[REDACTED]", text);
        Assert.DoesNotContain(Fingerprint('a'), text, StringComparison.Ordinal);
    }

    private static WebAutomationBaseline ExactBaseline(params string[] ids) =>
        WebAutomationBaseline.Create(ids);

    private static AdapterCapabilityVersion Capability() => new(
        "BaiduTransfer", "protocol-1", "selectors-1", "build-1");

    private static TransferSourceEvidence SourceEvidence(AdapterCapabilityVersion capability, char fingerprint) =>
        new(new RemoteItemIdentitySet([new("source", null)]), 1, true, true,
            $"source-generation-{fingerprint}", "baidu-user", capability, Fingerprint(fingerprint));

    private static TransferDirectoryMutationRecord Mutation(
        Graph graph,
        int generation,
        long revision,
        AdapterCapabilityVersion capability,
        TransferDirectoryMutationState state) =>
        new(graph.OperationId, graph.AccountId, generation, 0, "root", null,
            new RemotePath("/自动转存/游戏"), $"listing-{generation}", "baidu-user", capability,
            state, revision);

    private static async Task<WebAutomationAttemptRecord> MoveToStageAsync(
        Graph graph,
        int generation,
        WebAutomationAttemptState stage)
    {
        await SetTransferGenerationAsync(graph, generation);
        Assert.True(await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, generation, WebAutomationOwnerKind.Transfer, graph.Now)));
        if (stage == WebAutomationAttemptState.Preparing)
            return (await graph.Store.GetAsync(graph.OperationId, graph.AccountId, generation))!;

        Assert.True(await graph.Store.TryPersistBaselineAsync(
            graph.OperationId, graph.AccountId, generation, 0,
            ExactBaseline($"before-{generation}"), Fingerprint('6'), graph.Now.AddSeconds(1)));
        var current = (await graph.Store.GetAsync(graph.OperationId, graph.AccountId, generation))!;
        if (stage == WebAutomationAttemptState.BaselinePersisted) return current;

        Assert.True(await graph.Store.TryAdvanceAsync(
            graph.OperationId, graph.AccountId, generation, WebAutomationAttemptState.BaselinePersisted,
            current.Revision, WebAutomationAttemptState.Submitting, Fingerprint('6'), false,
            graph.Now.AddSeconds(2)));
        current = (await graph.Store.GetAsync(graph.OperationId, graph.AccountId, generation))!;
        if (stage == WebAutomationAttemptState.Submitting) return current;

        Assert.True(await graph.Store.TryAdvanceAsync(
            graph.OperationId, graph.AccountId, generation, WebAutomationAttemptState.Submitting,
            current.Revision, WebAutomationAttemptState.ResultPending, Fingerprint('6'), false,
            graph.Now.AddSeconds(3)));
        return (await graph.Store.GetAsync(graph.OperationId, graph.AccountId, generation))!;
    }

    private static string Fingerprint(char value) => new(value, 64);

    private static async Task SetTransferGenerationAsync(Graph graph, int generation)
    {
        await using var connection = await graph.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE TransferOperations SET AttemptCount=$generation WHERE Id=$operation;";
        command.Parameters.AddWithValue("$generation", generation);
        command.Parameters.AddWithValue("$operation", graph.OperationId.ToString("D"));
        await command.ExecuteNonQueryAsync();
    }

    private async Task<Graph> CreateSubmittingAttemptAsync()
    {
        var graph = await CreateAsync();
        await SetTransferGenerationAsync(graph, 2);
        Assert.True(await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 2, WebAutomationOwnerKind.Transfer, graph.Now)));
        Assert.True(await graph.Store.TryPersistBaselineAsync(
            graph.OperationId, graph.AccountId, 2, 0,
            ExactBaseline("before"), Fingerprint('a'), graph.Now.AddSeconds(1)));
        Assert.True(await graph.Store.TryAdvanceAsync(
            graph.OperationId, graph.AccountId, 2,
            WebAutomationAttemptState.BaselinePersisted, 1,
            WebAutomationAttemptState.Submitting, Fingerprint('b'), false, graph.Now.AddSeconds(2)));
        return graph;
    }

    private async Task<Graph> CreateTargetPreparedAttemptAsync()
    {
        var graph = await CreateAsync();
        await SetTransferGenerationAsync(graph, 1);
        Assert.True(await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 1, WebAutomationOwnerKind.Transfer, graph.Now)));
        var capability = Capability();
        Assert.True(await graph.Store.TryPersistTransferSourceEvidenceAsync(
            graph.OperationId, graph.AccountId, 1, 0,
            SourceEvidence(capability, '1'), graph.Now.AddSeconds(1)));
        var baseline = ExactBaseline("before");
        var fingerprint = TransferTargetPreparationEvidence.ComputeObservationFingerprint(
            "target-directory", new RemotePath("/自动转存/游戏"), 1, true, true,
            "target-generation", "baidu-user", TransferTargetInventoryScope.AllItems, baseline);
        var target = new TransferTargetPreparationEvidence(
            "target-directory", new RemotePath("/自动转存/游戏"), 1, true, true,
            "target-generation", "baidu-user", TransferTargetInventoryScope.AllItems,
            baseline, fingerprint);
        Assert.True(await graph.Store.TryPersistTransferTargetPreparationAsync(
            graph.OperationId, graph.AccountId, 1, 1, target, graph.Now.AddSeconds(2)));
        return graph;
    }

    private async Task<Graph> CreateBoundSubmittingAttemptAsync()
    {
        var graph = await CreateTargetPreparedAttemptAsync();
        var prepared = (await graph.Store.GetAsync(graph.OperationId, graph.AccountId, 1))!;
        var source = prepared.SourceEvidence!;
        var target = prepared.TargetPreparationEvidence!;
        var submission = TransferSubmissionEvidence.CreateBound(
            Guid.NewGuid(), Fingerprint('8'), source.Items.Fingerprint, target.TargetExactId,
            target.Baseline.Fingerprint, source.BaiduUserId, source.Capability, graph.Now.AddSeconds(3));
        Assert.True(await graph.Store.TryPersistTransferSubmissionAsync(
            graph.OperationId, graph.AccountId, 1, prepared.Revision, submission, graph.Now.AddSeconds(3)));
        return graph;
    }

    private async Task<(Graph Graph, WebAutomationAttemptRecord Pending, TransferIdentityMapping Mapping)>
        CreateOfficiallyAcceptedAttemptAsync()
    {
        var graph = await CreateBoundSubmittingAttemptAsync();
        var submitting = (await graph.Store.GetAsync(graph.OperationId, graph.AccountId, 1))!;
        var mapping = new TransferIdentityMapping([new("source", "destination")]);
        Assert.True(await graph.Store.TryPersistTransferAcceptanceAsync(
            graph.OperationId, graph.AccountId, 1, submitting.Revision,
            "official-request", mapping, Fingerprint('3'), graph.Now.AddSeconds(4)));
        var pending = (await new SqliteWebAutomationAttemptStore(graph.Factory)
            .GetAsync(graph.OperationId, graph.AccountId, 1))!;
        return (graph, pending, mapping);
    }

    private async Task<Graph> CreateAsync(
        int maximumBaselineItemCount = 100_000,
        int baselineReadPageSize = 1_000,
        bool managedTransfer = false)
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "app.db"));
        await new DatabaseMigrator(factory,
            new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
        var accountId = Guid.NewGuid();
        var otherAccountId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var shareOperationId = Guid.NewGuid();
        var standaloneBatchId = Guid.NewGuid();
        var standaloneOperationId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);
        var transferExecutionProfile = managedTransfer
            ? ExecutionProfileSnapshot.Managed(
                BrowserFamily.Chrome, $"managed-attempt-profile-{Guid.NewGuid():N}",
                Guid.NewGuid(), Guid.NewGuid())
            : ExecutionProfileSnapshot.LegacyForAccount(accountId);
        await SeedBusinessOwnersAsync(
            factory, accountId, otherAccountId, batchId, resourceId, operationId,
            shareOperationId, standaloneBatchId, standaloneOperationId, now, transferExecutionProfile);
        return new Graph(
            new SqliteWebAutomationAttemptStore(factory, maximumBaselineItemCount, baselineReadPageSize),
            factory,
            operationId, shareOperationId, standaloneOperationId, batchId, standaloneBatchId,
            accountId, otherAccountId, now, transferExecutionProfile);
    }

    private static async Task SeedBusinessOwnersAsync(
        SqliteConnectionFactory factory,
        Guid accountId,
        Guid otherAccountId,
        Guid batchId,
        Guid resourceId,
        Guid operationId,
        Guid shareOperationId,
        Guid standaloneBatchId,
        Guid standaloneOperationId,
        DateTimeOffset now,
        ExecutionProfileSnapshot transferExecutionProfile)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Accounts(Id,BaiduUserId,DisplayName,LoginMethod,Status,DailyLimit,DailyExecutedCount,
              SortOrder,IsEnabled,DailyCountDateKey,IsDeleted)
            VALUES($account,'owner','owner','WebView2','Active',0,0,0,1,'2026-08-25',0),
                  ($other,'other','other','WebView2','Active',0,0,1,1,'2026-08-25',0);
            INSERT INTO BrowserInstallations(
              InstallationId,BrowserFamily,InstallationDirectory,ExecutablePath,Publisher,ProductName,
              FileIdentity,State,Revision,CreatedAt,UpdatedAt)
            SELECT $installation,$family,$browserDirectory,$executable,'Google LLC','Google Chrome',$fileIdentity,
                   'Active',1,$now,$now
            WHERE $managed=1;
            INSERT INTO ManagedBrowserSessions(
              SessionId,AccountId,BaiduUserId,InstallationId,BrowserFamily,ProfileId,ProfileOwnershipId,
              State,Revision,CreatedAt,UpdatedAt)
            SELECT $session,$account,'owner',$installation,$family,$transferProfile,$ownership,
                   'Active',1,$now,$now
            WHERE $managed=1;
            INSERT INTO BatchJobs(Id,Name,SourceType,TransferMode,QuotaBasis,ShortagePolicy,ResourceCount,
              OperationCount,DefaultTargetPath,FolderCreationMode,DuplicatePolicy,ValidationMode,Status,CreatedAt,QueueOrder)
            VALUES($batch,'job','Text','Distribution','TaskCount','ProportionalReduction',1,1,'/','None','Skip',
              'Immediate','Queued',$now,1);
            INSERT INTO ResourceItems(Id,BatchJobId,CandidateSequence,ShareUrl,NormalizedShareId,ExtractionCode,
              FolderCreationModeOverride,GlobalValidityStatus,CandidateStatus,CreatedAt,UpdatedAt)
            VALUES($resource,$batch,1,'https://pan.baidu.com/s/test','test','1111','None','Valid','Expanded',$now,$now);
            INSERT INTO TransferOperations(Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,
              NormalizedTargetPath,CreateFolderName,AssignedAccountId,AllocationSource,Status,AttemptCount,
              OperationFingerprint,SubmitState,TargetSlotState,CreatedAt,UpdatedAt,
              ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
            VALUES($operation,$resource,$batch,1,1,'/','/','',$account,'Automatic','Waiting',0,'transfer-fp',
              'NotPrepared','Unoccupied',$now,$now,$transferEngine,$family,$transferProfile,$installation,$session);
            INSERT INTO BatchOutputAccounts(BatchJobId,AccountId,OutputOrdinal,OutputLabel,PlannedTransferCount,
              PlannedShareCount,ShareEnabled,CreatedShareOperationCount)
            VALUES($batch,$account,1,'owner',1,1,1,0);
            INSERT INTO BatchShareSettings(BatchJobId,IsPermanent,IsAccessUnlimited,ExtractionCodeMode,
              FixedExtractionCode,ExistingSharePolicy,SettingsFingerprint,CreatedAt)
            VALUES($batch,1,1,'Random',NULL,'Ask','settings-fp',$now);
            INSERT INTO ShareOperations(Id,TransferOperationId,ResourceItemId,BatchJobId,AccountId,OutputOrdinal,
              Status,ExactItemSetFingerprint,SettingsFingerprint,Generation,SubmissionAcceptance,AttemptCount,
              LeaseGeneration,BaselineShareCount,CreatedAt,UpdatedAt,
              ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
            VALUES($share,$operation,$resource,$batch,$account,1,'WaitingTransfer',NULL,'settings-fp',1,'Unknown',0,0,0,$now,$now,
              'WebView2-v1','LegacyWebView2',$legacyProfile,NULL,NULL);
            INSERT INTO StandaloneShareBatches(Id,DraftFingerprint,ResultDirectoryReference,Status,OperationCount,
              Revision,CreatedAt,UpdatedAt)
            VALUES($standaloneBatch,'draft-fp',$directory,'Queued',1,0,$now,$now);
            INSERT INTO StandaloneShareOperations(Id,BatchId,AccountId,SequenceNumber,DisplayName,ResourceKind,
              ParentPath,ExactItemSetFingerprint,Generation,Status,SubmissionState,SubmissionAcceptance,
              LeaseGeneration,Revision,CreatedAt,UpdatedAt,
              ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
            VALUES($standaloneOperation,$standaloneBatch,$account,1,'item','File','/','exact-fp',1,'Queued',
              'NotStarted','Unknown',0,0,$now,$now,'WebView2-v1','LegacyWebView2',$legacyProfile,NULL,NULL);
            INSERT INTO StandaloneShareRemoteItems(OperationId,ItemOrdinal,RemoteItemId,CanonicalSortKey,DisplayName)
            VALUES($standaloneOperation,0,'remote-id',X'01','item');
            """;
        command.Parameters.AddWithValue("$account", accountId.ToString("D"));
        command.Parameters.AddWithValue("$other", otherAccountId.ToString("D"));
        command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        command.Parameters.AddWithValue("$resource", resourceId.ToString("D"));
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        command.Parameters.AddWithValue("$share", shareOperationId.ToString("D"));
        command.Parameters.AddWithValue("$standaloneBatch", standaloneBatchId.ToString("D"));
        command.Parameters.AddWithValue("$standaloneOperation", standaloneOperationId.ToString("D"));
        command.Parameters.AddWithValue("$legacyProfile", ExecutionProfileSnapshot.LegacyForAccount(accountId).ProfileId);
        command.Parameters.AddWithValue("$managed", transferExecutionProfile.Engine == BaiduExecutionEngine.ManagedChromiumV1 ? 1 : 0);
        command.Parameters.AddWithValue("$transferEngine", transferExecutionProfile.Engine == BaiduExecutionEngine.ManagedChromiumV1
            ? "ManagedChromium-v1" : "WebView2-v1");
        command.Parameters.AddWithValue("$family", transferExecutionProfile.BrowserFamily.ToString());
        command.Parameters.AddWithValue("$transferProfile", transferExecutionProfile.ProfileId);
        command.Parameters.AddWithValue("$installation", (object?)transferExecutionProfile.InstallationId?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$session", (object?)transferExecutionProfile.SessionId?.ToString("D") ?? DBNull.Value);
        var directory = Path.Combine(Path.GetTempPath(), $"managed-attempt-{operationId:N}");
        command.Parameters.AddWithValue("$browserDirectory", directory);
        command.Parameters.AddWithValue("$executable", Path.Combine(directory, "chrome.exe"));
        command.Parameters.AddWithValue("$fileIdentity", $"managed-attempt-file-{operationId:N}");
        command.Parameters.AddWithValue("$ownership", $"managed-attempt-owner-{operationId:N}");
        command.Parameters.AddWithValue("$directory", Path.Combine(Path.GetTempPath(), $"web-owner-{standaloneBatchId:N}"));
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(SqliteConnectionFactory factory, string sql)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task InsertUncountedAcceptanceEvidenceAsync(
        Graph graph, bool mapping, bool accepted, int ordinal)
    {
        await using var connection = await graph.Factory.OpenConnectionAsync();
        if (mapping)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO WebAutomationTransferMappings(
                  OperationId,AccountId,Generation,ItemOrdinal,SourceRemoteItemId,
                  SourceCanonicalSortKey,DestinationRemoteItemId,DestinationCanonicalSortKey)
                VALUES($operation,$account,1,$ordinal,$source,$sourceSort,$destination,$destinationSort);
                """;
            command.Parameters.AddWithValue("$operation", graph.OperationId.ToString("D"));
            command.Parameters.AddWithValue("$account", graph.AccountId.ToString("D"));
            command.Parameters.AddWithValue("$ordinal", ordinal);
            command.Parameters.AddWithValue("$source", "orphan-source");
            command.Parameters.AddWithValue("$sourceSort",
                RemoteItemIdentitySet.GetCanonicalSortKey("orphan-source"));
            command.Parameters.AddWithValue("$destination", "orphan-destination");
            command.Parameters.AddWithValue("$destinationSort",
                RemoteItemIdentitySet.GetCanonicalSortKey("orphan-destination"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        if (accepted)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO WebAutomationAttemptAcceptedRemoteItems(
                  OperationId,AccountId,Generation,ItemOrdinal,RemoteItemId,CanonicalSortKey)
                VALUES($operation,$account,1,$ordinal,$item,$sort);
                """;
            command.Parameters.AddWithValue("$operation", graph.OperationId.ToString("D"));
            command.Parameters.AddWithValue("$account", graph.AccountId.ToString("D"));
            command.Parameters.AddWithValue("$ordinal", ordinal);
            command.Parameters.AddWithValue("$item", "orphan-accepted");
            command.Parameters.AddWithValue("$sort",
                RemoteItemIdentitySet.GetCanonicalSortKey("orphan-accepted"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
    }

    private static async Task<long> CountAcceptanceChildAsync(Graph graph, string table)
    {
        Assert.True(table is "WebAutomationTransferMappings" or
            "WebAutomationAttemptAcceptedRemoteItems");
        await using var connection = await graph.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(*) FROM {table}
            WHERE OperationId=$operation AND AccountId=$account AND Generation=1;
            """;
        command.Parameters.AddWithValue("$operation", graph.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$account", graph.AccountId.ToString("D"));
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task DeleteAcceptanceChildrenAsync(Graph graph)
    {
        await using var connection = await graph.Factory.OpenConnectionAsync();
        await using var transaction = connection.BeginTransaction();
        foreach (var table in new[]
        {
            "WebAutomationTransferMappings",
            "WebAutomationAttemptAcceptedRemoteItems",
        })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                DELETE FROM {table}
                WHERE OperationId=$operation AND AccountId=$account AND Generation=1;
                """;
            command.Parameters.AddWithValue("$operation", graph.OperationId.ToString("D"));
            command.Parameters.AddWithValue("$account", graph.AccountId.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed record Graph(
        SqliteWebAutomationAttemptStore Store,
        SqliteConnectionFactory Factory,
        Guid OperationId,
        Guid ShareOperationId,
        Guid StandaloneOperationId,
        Guid BatchId,
        Guid StandaloneBatchId,
        Guid AccountId,
        Guid OtherAccountId,
        DateTimeOffset Now,
        ExecutionProfileSnapshot TransferExecutionProfile);
}

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Abstractions;

public enum WebAutomationOwnerKind
{
    Transfer,
    TransferShare,
    StandaloneShare,
    RemoteSelection,
}

public enum WebAutomationAttemptState
{
    Preparing,
    BaselinePersisted,
    Submitting,
    ResultPending,
    UserHandoff,
    Succeeded,
    DefinitelyNotSubmitted,
    NeedsDecision,
}

public sealed record WebAutomationBaselineItem
{
    public WebAutomationBaselineItem(string exactId)
    {
        ExactId = exactId;
        CanonicalSortKey = RemoteItemIdentitySet.GetCanonicalSortKey(exactId).ToImmutableArray();
    }

    public string ExactId { get; }
    public ImmutableArray<byte> CanonicalSortKey { get; }

    public override string ToString() =>
        "WebAutomationBaselineItem { ExactId = [REDACTED], CanonicalSortKey = [REDACTED] }";
}

public sealed record WebAutomationBaseline
{
    private WebAutomationBaseline(ImmutableArray<WebAutomationBaselineItem> items, string fingerprint)
    {
        Items = items;
        Fingerprint = fingerprint;
    }

    public ImmutableArray<WebAutomationBaselineItem> Items { get; }
    public string Fingerprint { get; }

    public static WebAutomationBaseline Create(IEnumerable<string> exactIds)
    {
        ArgumentNullException.ThrowIfNull(exactIds);
        var snapshot = exactIds
            .Select(exactId => new WebAutomationBaselineItem(exactId))
            .OrderBy(item => item.ExactId, StringComparer.Ordinal)
            .ToImmutableArray();
        if (snapshot.Select(item => item.ExactId).Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
            throw new ArgumentException("Baseline item identities must be unique.", nameof(exactIds));

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "web-automation-baseline-v1");
        foreach (var item in snapshot) Append(hash, item.ExactId);
        return new(snapshot, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    public override string ToString() =>
        $"WebAutomationBaseline {{ Count = {Items.Length}, Fingerprint = [REDACTED], Items = [REDACTED] }}";

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(bytes.Length)));
        hash.AppendData(bytes);
    }
}

public sealed record WebAutomationAttemptRecord
{
    public WebAutomationAttemptRecord(
        Guid operationId,
        Guid accountId,
        int generation,
        WebAutomationOwnerKind ownerKind,
        WebAutomationAttemptState state,
        WebAutomationAttemptState? resumeStage,
        WebAutomationBaseline? baseline,
        string? observationFingerprint,
        bool isWaitingForUser,
        long revision,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        RemoteItemIdentitySet? acceptedRemoteItems = null,
        TransferSourceEvidence? sourceEvidence = null,
        TransferTargetPreparationEvidence? targetPreparationEvidence = null,
        TransferSubmissionEvidence? submissionEvidence = null,
        string? officialRequestId = null,
        TransferIdentityMapping? transferIdentityMapping = null)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation is required.", nameof(operationId));
        if (accountId == Guid.Empty) throw new ArgumentException("An account is required.", nameof(accountId));
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
        if (!Enum.IsDefined(ownerKind)) throw new ArgumentOutOfRangeException(nameof(ownerKind));
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        if (resumeStage is not null && !Enum.IsDefined(resumeStage.Value))
            throw new ArgumentOutOfRangeException(nameof(resumeStage));
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        if (updatedAt < createdAt) throw new ArgumentOutOfRangeException(nameof(updatedAt));
        ValidateFingerprint(observationFingerprint, nameof(observationFingerprint));
        if (state == WebAutomationAttemptState.UserHandoff)
        {
            if (resumeStage is null || !CanResumeStage(resumeStage.Value) || !isWaitingForUser)
                throw new ArgumentException("A user handoff requires its exact resumable prior stage.", nameof(resumeStage));
        }
        else if (resumeStage is not null)
        {
            throw new ArgumentException("Only a user handoff can retain a resume stage.", nameof(resumeStage));
        }
        if ((RequiresBaseline(state) || resumeStage is not null && resumeStage != WebAutomationAttemptState.Preparing) &&
            baseline is null)
        {
            throw new ArgumentException("This state requires an exact baseline.", nameof(baseline));
        }
        if (targetPreparationEvidence is not null && sourceEvidence is null)
            throw new ArgumentException("Target preparation requires exact source evidence.", nameof(targetPreparationEvidence));
        if (sourceEvidence is not null && targetPreparationEvidence is not null)
        {
            _ = new TransferPreparationEvidence(sourceEvidence, targetPreparationEvidence).Validate();
            if (baseline is not null && !string.Equals(
                    baseline.Fingerprint, targetPreparationEvidence.Baseline.Fingerprint, StringComparison.Ordinal))
                throw new ArgumentException("The legacy baseline must match target preparation evidence.", nameof(baseline));
        }
        if (submissionEvidence is not null)
        {
            if (sourceEvidence is null || targetPreparationEvidence is null ||
                !string.Equals(submissionEvidence.SourceFingerprint, sourceEvidence.Items.Fingerprint, StringComparison.Ordinal) ||
                !string.Equals(submissionEvidence.TargetExactId, targetPreparationEvidence.TargetExactId, StringComparison.Ordinal) ||
                !string.Equals(submissionEvidence.TargetBaselineFingerprint,
                    targetPreparationEvidence.Baseline.Fingerprint, StringComparison.Ordinal) ||
                !string.Equals(submissionEvidence.BaiduUserId, sourceEvidence.BaiduUserId, StringComparison.Ordinal) ||
                submissionEvidence.Capability != sourceEvidence.Capability)
                throw new ArgumentException("Submission evidence must bind the frozen preparation evidence.", nameof(submissionEvidence));
        }
        if (transferIdentityMapping is not null)
        {
            if (sourceEvidence is null || !transferIdentityMapping.Covers(sourceEvidence.Items) ||
                string.IsNullOrWhiteSpace(officialRequestId))
                throw new ArgumentException("Official transfer acceptance must cover the exact source set.", nameof(transferIdentityMapping));
            var mappedDestinations = new RemoteItemIdentitySet(transferIdentityMapping.Entries
                .Select(entry => new RemoteItemIdentity(entry.DestinationExactId, null)));
            if (acceptedRemoteItems is null || mappedDestinations != acceptedRemoteItems)
                throw new ArgumentException("Accepted remote items must match the transfer mapping.", nameof(acceptedRemoteItems));
        }

        OperationId = operationId;
        AccountId = accountId;
        Generation = generation;
        OwnerKind = ownerKind;
        State = state;
        ResumeStage = resumeStage;
        Baseline = baseline;
        ObservationFingerprint = observationFingerprint;
        IsWaitingForUser = isWaitingForUser;
        Revision = revision;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        AcceptedRemoteItems = acceptedRemoteItems;
        SourceEvidence = sourceEvidence;
        TargetPreparationEvidence = targetPreparationEvidence;
        SubmissionEvidence = submissionEvidence;
        OfficialRequestId = officialRequestId;
        TransferIdentityMapping = transferIdentityMapping;
    }

    public Guid OperationId { get; }
    public Guid AccountId { get; }
    public int Generation { get; }
    public WebAutomationOwnerKind OwnerKind { get; }
    public WebAutomationAttemptState State { get; }
    public WebAutomationAttemptState? ResumeStage { get; }
    public WebAutomationBaseline? Baseline { get; }
    public ImmutableArray<WebAutomationBaselineItem> BaselineRemoteItems => Baseline?.Items ?? [];
    public string? ObservationFingerprint { get; }
    public bool IsWaitingForUser { get; }
    public long Revision { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; }
    public RemoteItemIdentitySet? AcceptedRemoteItems { get; }
    public TransferSourceEvidence? SourceEvidence { get; }
    public TransferTargetPreparationEvidence? TargetPreparationEvidence { get; }
    public TransferSubmissionEvidence? SubmissionEvidence { get; }
    public string? OfficialRequestId { get; }
    public TransferIdentityMapping? TransferIdentityMapping { get; }

    public static WebAutomationAttemptRecord Create(
        Guid operationId,
        Guid accountId,
        int generation,
        WebAutomationOwnerKind ownerKind,
        DateTimeOffset createdAt) =>
        new(operationId, accountId, generation, ownerKind, WebAutomationAttemptState.Preparing,
            null, null, null, false, 0, createdAt, createdAt);

    public override string ToString() =>
        $"WebAutomationAttemptRecord {{ OperationId = {OperationId}, AccountId = {AccountId}, Generation = {Generation}, OwnerKind = {OwnerKind}, State = {State}, ResumeStage = {ResumeStage}, Baseline = [REDACTED], AcceptedRemoteItems = [REDACTED], Observation = [REDACTED], IsWaitingForUser = {IsWaitingForUser}, Revision = {Revision} }}";

    public static void ValidateFingerprint(string? value, string parameterName)
    {
        if (value is null) return;
        if (value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("A sanitized lowercase SHA-256 fingerprint is required.", parameterName);
    }

    private static bool RequiresBaseline(WebAutomationAttemptState state) => state is
        WebAutomationAttemptState.BaselinePersisted or WebAutomationAttemptState.Submitting or
        WebAutomationAttemptState.ResultPending or WebAutomationAttemptState.Succeeded;

    public static bool CanResumeStage(WebAutomationAttemptState state) => state is
        WebAutomationAttemptState.Preparing or WebAutomationAttemptState.BaselinePersisted or
        WebAutomationAttemptState.Submitting or WebAutomationAttemptState.ResultPending;
}

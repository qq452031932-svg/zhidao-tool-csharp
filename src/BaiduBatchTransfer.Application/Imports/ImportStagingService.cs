using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Core.Services;

namespace BaiduBatchTransfer.Application.Imports;

public delegate Task<IUnitOfWork> ImportUnitOfWorkFactory(CancellationToken cancellationToken);

public sealed record ImportRequest(
    Guid SessionId,
    string SourceType,
    string SourceName,
    TransferMode TransferMode = TransferMode.Distribution,
    Guid? AppendToBatchJobId = null)
{
    public override string ToString() =>
        $"ImportRequest {{ SessionId = {SessionId}, SourceType = {SourceType}, SourceName = [REDACTED], TransferMode = {TransferMode}, AppendToBatchJobId = {AppendToBatchJobId} }}";
}

public sealed class ImportStagingService
{
    private readonly ImportUnitOfWorkFactory unitOfWorkFactory;
    private readonly IShareLinkResolver resolver;
    private readonly int chunkSize;
    private readonly SemaphoreSlim resolutionGate;
    private readonly BaiduShareNormalizer normalizer = new();
    private readonly BaiduPathNormalizer pathNormalizer = new();
    private readonly ImportConflictService conflicts = new();

    public ImportStagingService(
        ImportUnitOfWorkFactory unitOfWorkFactory,
        IShareLinkResolver resolver,
        int chunkSize = 5_000,
        int maxResolutionConcurrency = 4)
    {
        this.unitOfWorkFactory = unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        if (maxResolutionConcurrency <= 0) throw new ArgumentOutOfRangeException(nameof(maxResolutionConcurrency));
        this.chunkSize = chunkSize;
        resolutionGate = new SemaphoreSlim(maxResolutionConcurrency, maxResolutionConcurrency);
    }

    public async Task StageAsync(
        ImportRequest request,
        IAsyncEnumerable<ImportSourceRow> rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rows);
        if (request.SessionId == Guid.Empty) throw new ArgumentException("A session id is required.", nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceName);

        var now = DateTimeOffset.UtcNow;
        var progress = new ProgressState(new ImportSessionRecord(
            request.SessionId,
            request.SourceType,
            request.SourceName,
            "Parsing",
            0, 0, 0, 0, 0,
            null, null, request.AppendToBatchJobId,
            now, now));

        await using (var unit = await unitOfWorkFactory(cancellationToken).ConfigureAwait(false))
        {
            await unit.Imports.AddSessionAsync(progress.Session, cancellationToken).ConfigureAwait(false);
            await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        await ProcessWithDraftRecoveryAsync(progress, request.TransferMode, rows, skipThroughSourceRow: 0, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ResumeAsync(
        Guid sessionId,
        IAsyncEnumerable<ImportSourceRow> rows,
        TransferMode transferMode = TransferMode.Distribution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ImportSessionRecord session;
        int lastSourceRow;
        await using (var unit = await unitOfWorkFactory(cancellationToken).ConfigureAwait(false))
        {
            session = await unit.Imports.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The import session does not exist.");
            if (session.Status is not ("Draft" or "Parsing"))
            {
                throw new InvalidOperationException("Only an unfinished import draft can be resumed.");
            }

            lastSourceRow = await unit.Imports.GetLastSourceRowNumberAsync(sessionId, cancellationToken).ConfigureAwait(false);
            session = session with { Status = "Parsing", UpdatedAt = DateTimeOffset.UtcNow };
            await unit.Imports.UpdateSessionAsync(session, cancellationToken).ConfigureAwait(false);
            await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        await ProcessWithDraftRecoveryAsync(new ProgressState(session), transferMode, rows, lastSourceRow, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RetryNeedsResolutionAsync(
        Guid sessionId,
        TransferMode transferMode = TransferMode.Distribution,
        CancellationToken cancellationToken = default)
    {
        var afterSourceRowNumber = 0;
        while (true)
        {
            IReadOnlyList<StagedImportRowRecord> page;
            await using (var readUnit = await unitOfWorkFactory(cancellationToken).ConfigureAwait(false))
            {
                var snapshotSession = await readUnit.Imports.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The import session does not exist.");
                if (snapshotSession.Status != "Draft")
                {
                    throw new InvalidOperationException("Only a draft import can retry unresolved links.");
                }

                page = await readUnit.Imports.GetRowsAfterSourceRowAsync(
                    sessionId,
                    "NeedsResolution",
                    afterSourceRowNumber,
                    Math.Min(chunkSize, 1_000),
                    cancellationToken).ConfigureAwait(false);
            }

            if (page.Count == 0)
            {
                return;
            }

            afterSourceRowNumber = page[^1].SourceRowNumber;

            var resolved = await Task.WhenAll(page.Select(row => ReresolveAsync(row, sessionId, cancellationToken)))
                .ConfigureAwait(false);

            await using var writeUnit = await unitOfWorkFactory(cancellationToken).ConfigureAwait(false);
            var session = await writeUnit.Imports.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The import session does not exist.");
            if (session.Status != "Draft")
            {
                throw new InvalidOperationException("Only a draft import can retry unresolved links.");
            }

            var transitions = new List<RowTransition>();
            var normalizedIds = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < resolved.Length; index++)
            {
                var snapshot = page[index];
                var current = await writeUnit.Imports.GetRowByIdAsync(snapshot.Id, cancellationToken).ConfigureAwait(false);
                if (current is null || current.Disposition != "NeedsResolution" || current.UpdatedAt != snapshot.UpdatedAt)
                {
                    continue;
                }

                var candidate = resolved[index];
                await writeUnit.Imports.UpdateRowsAsync([candidate], cancellationToken).ConfigureAwait(false);
                transitions.Add(new RowTransition(current, candidate));
                if (candidate.Disposition == "Ready" && candidate.NormalizedShareId is not null)
                {
                    normalizedIds.Add(candidate.NormalizedShareId);
                }
            }

            foreach (var normalizedId in normalizedIds)
            {
                await StabilizeNormalizedGroupAsync(
                    writeUnit.Imports,
                    sessionId,
                    normalizedId,
                    transferMode,
                    transitions,
                    cancellationToken).ConfigureAwait(false);
            }

            session = ApplyTransitionCounters(session, transitions) with { UpdatedAt = DateTimeOffset.UtcNow };
            await writeUnit.Imports.UpdateSessionAsync(session, cancellationToken).ConfigureAwait(false);
            await writeUnit.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StabilizeNormalizedGroupAsync(
        IImportRepository repository,
        Guid sessionId,
        string normalizedShareId,
        TransferMode transferMode,
        List<RowTransition> transitions,
        CancellationToken cancellationToken)
    {
        var primary = await repository.GetFirstByNormalizedShareIdAsync(
            sessionId,
            normalizedShareId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A normalized staging group has no primary row.");
        var readyPrimary = primary with
        {
            Disposition = "Ready",
            DuplicateOfStagedRowId = null,
            ValidationMessage = null,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        if (!HasSameClassification(primary, readyPrimary))
        {
            await repository.UpdateRowsAsync([readyPrimary], cancellationToken).ConfigureAwait(false);
            transitions.Add(new RowTransition(primary, readyPrimary));
        }
        primary = readyPrimary;

        var afterSourceRowNumber = primary.SourceRowNumber;
        while (true)
        {
            var page = await repository.GetRowsByNormalizedShareIdAfterSourceRowAsync(
                sessionId,
                normalizedShareId,
                afterSourceRowNumber,
                1_000,
                cancellationToken).ConfigureAwait(false);
            if (page.Count == 0) return;
            afterSourceRowNumber = page[^1].SourceRowNumber;
            foreach (var later in page)
            {
                var classification = conflicts.Classify(transferMode, primary, later);
                var desired = classification.Kind switch
                {
                    DuplicateKind.Duplicate => later with
                    {
                        Disposition = "Duplicate",
                        DuplicateOfStagedRowId = primary.Id,
                        ValidationMessage = null,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    },
                    DuplicateKind.Conflict => later with
                    {
                        Disposition = "Conflict",
                        DuplicateOfStagedRowId = null,
                        ValidationMessage = classification.Message,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    },
                    _ => later,
                };
                if (HasSameClassification(later, desired)) continue;
                await repository.UpdateRowsAsync([desired], cancellationToken).ConfigureAwait(false);
                transitions.Add(new RowTransition(later, desired));
            }
        }
    }

    private static bool HasSameClassification(StagedImportRowRecord left, StagedImportRowRecord right) =>
        left.Disposition == right.Disposition &&
        left.DuplicateOfStagedRowId == right.DuplicateOfStagedRowId &&
        string.Equals(left.ValidationMessage, right.ValidationMessage, StringComparison.Ordinal);

    private async Task<StagedImportRowRecord> ReresolveAsync(
        StagedImportRowRecord existing,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveAsync(ToSource(existing), sessionId, cancellationToken).ConfigureAwait(false);
        return resolved with { Id = existing.Id };
    }

    private async Task ProcessWithDraftRecoveryAsync(
        ProgressState progress,
        TransferMode transferMode,
        IAsyncEnumerable<ImportSourceRow> rows,
        int skipThroughSourceRow,
        CancellationToken cancellationToken)
    {
        try
        {
            var chunk = new List<ImportSourceRow>(chunkSize);
            await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (row.SourceRowNumber <= skipThroughSourceRow)
                {
                    continue;
                }

                chunk.Add(row);
                if (chunk.Count == chunkSize)
                {
                    progress.Session = await PersistChunkAsync(progress.Session, transferMode, chunk, cancellationToken)
                        .ConfigureAwait(false);
                    chunk.Clear();
                }
            }

            if (chunk.Count != 0)
            {
                progress.Session = await PersistChunkAsync(progress.Session, transferMode, chunk, cancellationToken)
                    .ConfigureAwait(false);
            }

            await PersistDraftAsync(progress.Session, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await PersistDraftAsync(progress.Session, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<ImportSessionRecord> PersistChunkAsync(
        ImportSessionRecord session,
        TransferMode transferMode,
        IReadOnlyList<ImportSourceRow> chunk,
        CancellationToken cancellationToken)
    {
        var candidates = await Task.WhenAll(chunk.Select(row => ResolveAsync(row, session.Id, cancellationToken)))
            .ConfigureAwait(false);
        await using var unit = await unitOfWorkFactory(cancellationToken).ConfigureAwait(false);
        var classified = new List<StagedImportRowRecord>(candidates.Length);
        var localFirst = new Dictionary<string, StagedImportRowRecord>(StringComparer.Ordinal);
        foreach (var candidate in candidates.OrderBy(row => row.SourceRowNumber))
        {
            cancellationToken.ThrowIfCancellationRequested();
            classified.Add(await ClassifyAsync(
                unit.Imports,
                candidate,
                session.Id,
                transferMode,
                cancellationToken,
                localFirst).ConfigureAwait(false));
        }

        var now = DateTimeOffset.UtcNow;
        session = ApplyCounters(session, classified) with { UpdatedAt = now };
        await unit.Imports.AddRowsAsync(classified, cancellationToken).ConfigureAwait(false);
        await unit.Imports.UpdateSessionAsync(session, cancellationToken).ConfigureAwait(false);
        await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
        return session;
    }

    private async Task<StagedImportRowRecord> ResolveAsync(
        ImportSourceRow source,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var row = new StagedImportRowRecord(
            Guid.NewGuid(), sessionId, source.SourceRowNumber, source.ShareUrl,
            null, source.ExtractionCode, source.TargetPath, source.CreateFolderName,
            source.RequestedAccountName, source.MappedAccountId, source.Note,
            "NeedsResolution", null, null, null, null, null, now);
        if (source.ValidationError is not null || string.IsNullOrWhiteSpace(source.ShareUrl))
        {
            return row with { Disposition = "Invalid", ValidationMessage = source.ValidationError ?? "The share link is missing." };
        }

        if (source.TargetPath is not null)
        {
            try
            {
                _ = pathNormalizer.Normalize(source.TargetPath);
            }
            catch (ArgumentException)
            {
                return row with { Disposition = "Invalid", ValidationMessage = "The target path is invalid." };
            }
        }

        try
        {
            var normalized = normalizer.Normalize(source.ShareUrl);
            return row with
            {
                NormalizedShareId = normalized.ShareId,
                ExtractionCode = source.ExtractionCode ?? normalized.ExtractionCode,
                Disposition = "Ready",
            };
        }
        catch (ArgumentException)
        {
            if (!IsResolvableShortLink(source.ShareUrl))
            {
                return row with { Disposition = "Invalid", ValidationMessage = "The share link is not a supported Baidu link." };
            }
        }

        await resolutionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BaiduAdapterResult<ResolvedShareLink> result;
            try
            {
                result = await resolver.ResolveAsync(source.ShareUrl, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return row with
                {
                    Disposition = "NeedsResolution",
                    ValidationMessage = "The short link could not be resolved because the service is unavailable.",
                };
            }
            catch (Exception error) when (error is HttpRequestException or IOException or TimeoutException)
            {
                return row with
                {
                    Disposition = "NeedsResolution",
                    ValidationMessage = "The short link could not be resolved because the service is unavailable.",
                };
            }
            if (!result.IsSuccess || result.Value is null)
            {
                var disposition = result.ErrorKind == BaiduErrorKind.InvalidLink ? "Invalid" : "NeedsResolution";
                return row with
                {
                    Disposition = disposition,
                    ValidationMessage = result.SafeMessage ?? "The short link could not be resolved safely.",
                };
            }

            try
            {
                var normalized = normalizer.Normalize(result.Value.FinalUrl);
                return row with
                {
                    NormalizedShareId = normalized.ShareId,
                    ExtractionCode = source.ExtractionCode ?? normalized.ExtractionCode,
                    Disposition = "Ready",
                };
            }
            catch (ArgumentException)
            {
                return row with { Disposition = "Invalid", ValidationMessage = "The resolved share link is invalid." };
            }
        }
        finally
        {
            resolutionGate.Release();
        }
    }

    private async Task<StagedImportRowRecord> ClassifyAsync(
        IImportRepository repository,
        StagedImportRowRecord incoming,
        Guid sessionId,
        TransferMode transferMode,
        CancellationToken cancellationToken,
        Dictionary<string, StagedImportRowRecord>? localFirst = null)
    {
        if (incoming.Disposition != "Ready" || incoming.NormalizedShareId is null)
        {
            return incoming;
        }

        StagedImportRowRecord? first = null;
        if (localFirst is not null)
        {
            localFirst.TryGetValue(incoming.NormalizedShareId, out first);
        }

        first ??= await repository.GetFirstByNormalizedShareIdAsync(sessionId, incoming.NormalizedShareId, cancellationToken)
            .ConfigureAwait(false);
        if (first is null)
        {
            localFirst?.Add(incoming.NormalizedShareId, incoming);
            return incoming;
        }

        var classification = conflicts.Classify(transferMode, first, incoming);
        return classification.Kind switch
        {
            DuplicateKind.Duplicate => incoming with
            {
                Disposition = "Duplicate",
                DuplicateOfStagedRowId = first.Id,
            },
            DuplicateKind.Conflict => incoming with
            {
                Disposition = "Conflict",
                ValidationMessage = classification.Message,
            },
            _ => incoming,
        };
    }

    private async Task PersistDraftAsync(ImportSessionRecord session, CancellationToken cancellationToken)
    {
        await using var unit = await unitOfWorkFactory(cancellationToken).ConfigureAwait(false);
        await unit.Imports.UpdateSessionAsync(
            session with { Status = "Draft", UpdatedAt = DateTimeOffset.UtcNow },
            cancellationToken).ConfigureAwait(false);
        await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ImportSessionRecord ApplyCounters(ImportSessionRecord session, IEnumerable<StagedImportRowRecord> rows)
    {
        var materialized = rows as IReadOnlyCollection<StagedImportRowRecord> ?? rows.ToArray();
        return session with
        {
            TotalRows = checked(session.TotalRows + materialized.Count),
            ReadyRows = checked(session.ReadyRows + materialized.Count(row => row.Disposition == "Ready")),
            InvalidRows = checked(session.InvalidRows + materialized.Count(row => row.Disposition is "Invalid" or "Conflict")),
            DuplicateRows = checked(session.DuplicateRows + materialized.Count(row => row.Disposition == "Duplicate")),
            ExcludedRows = checked(session.ExcludedRows + materialized.Count(row => row.Disposition == "Excluded")),
        };
    }

    private static ImportSessionRecord ApplyTransitionCounters(
        ImportSessionRecord session,
        IEnumerable<RowTransition> transitions)
    {
        var ready = session.ReadyRows;
        var invalid = session.InvalidRows;
        var duplicate = session.DuplicateRows;
        var excluded = session.ExcludedRows;
        foreach (var transition in transitions)
        {
            ApplyDispositionDelta(transition.Before.Disposition, -1, ref ready, ref invalid, ref duplicate, ref excluded);
            ApplyDispositionDelta(transition.After.Disposition, 1, ref ready, ref invalid, ref duplicate, ref excluded);
        }

        return session with { ReadyRows = ready, InvalidRows = invalid, DuplicateRows = duplicate, ExcludedRows = excluded };
    }

    private static void ApplyDispositionDelta(
        string disposition,
        int delta,
        ref int ready,
        ref int invalid,
        ref int duplicate,
        ref int excluded)
    {
        switch (disposition)
        {
            case "Ready": ready = checked(ready + delta); break;
            case "Invalid":
            case "Conflict": invalid = checked(invalid + delta); break;
            case "Duplicate": duplicate = checked(duplicate + delta); break;
            case "Excluded": excluded = checked(excluded + delta); break;
        }
    }

    private static ImportSourceRow ToSource(StagedImportRowRecord row) => new(
        row.SourceRowNumber,
        row.ShareUrl,
        row.ExtractionCode,
        row.TargetPath,
        row.CreateFolderName,
        row.RequestedAccountName,
        row.MappedAccountId,
        row.Note);

    private static bool IsResolvableShortLink(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 ||
            !(string.Equals(uri.IdnHost, "pan.baidu.com", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(uri.IdnHost, "yun.baidu.com", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return uri.AbsolutePath.StartsWith("/scl/", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.StartsWith("/short/", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ProgressState(ImportSessionRecord session)
    {
        public ImportSessionRecord Session { get; set; } = session;
    }

    private sealed record RowTransition(StagedImportRowRecord Before, StagedImportRowRecord After);
}

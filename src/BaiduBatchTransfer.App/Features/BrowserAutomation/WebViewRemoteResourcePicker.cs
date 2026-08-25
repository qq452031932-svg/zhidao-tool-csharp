using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Features.BrowserAutomation;

public sealed class WebViewRemoteResourcePicker(
    IVisibleWebAutomationBroker broker,
    IAccountProfilePathPolicy profilePaths,
    IRemoteSelectionSessionStore sessions,
    RemoteResourcePickerFlow flow,
    TimeProvider timeProvider,
    string applicationBuildVersion) : IRemoteResourcePicker
{
    private static readonly Uri OwnDriveUri = new("https://pan.baidu.com/disk/main");

    public async Task<IReadOnlyList<OwnedRemoteSelection>> PickAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("An account is required.", nameof(accountId));
        var session = await sessions.CreateAsync(
            accountId, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        var browserMayHaveStarted = false;
        var completionPersisted = false;
        try
        {
            ValidateSession(session, accountId);
            var identity = new WebAutomationRequestIdentity(
                session.SessionId, session.WorkspaceDraftId, session.AccountId,
                session.Generation, WebAutomationOwnerKind.RemoteSelection);
            var profilePath = profilePaths.GetAccountProfilePath(accountId);
            profilePaths.EnsureSafeAccountPath(accountId, profilePath);
            var request = new VisibleWebAutomationRequest<RemoteResourcePickerOutcome>(
                identity,
                profilePath,
                Navigation(),
                async (visibleSession, _, token) => visibleSession is IRemoteResourcePickerSession pickerSession
                    ? await flow.ExecuteAsync(pickerSession, accountId, token).ConfigureAwait(false)
                    : new(RemoteResourcePickerDisposition.UnsafeObservation,
                        Array.Empty<OwnedRemoteSelection>(), "当前浏览会话不能提供可验证的选择证据。"));
            VisibleWebAutomationResult<RemoteResourcePickerOutcome> result;
            try
            {
                browserMayHaveStarted = true;
                result = await broker.EnqueueAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (WebAutomationAuthorizationException)
            {
                await CompleteOrThrowAsync(session, RemoteSelectionSessionState.Cancelled)
                    .ConfigureAwait(false);
                completionPersisted = true;
                throw;
            }

            var outcome = result.Disposition == VisibleWebAutomationDisposition.ActionCompleted
                ? result.Value
                : null;
            var terminal = outcome?.Disposition switch
            {
                RemoteResourcePickerDisposition.Confirmed => RemoteSelectionSessionState.Confirmed,
                RemoteResourcePickerDisposition.Cancelled => RemoteSelectionSessionState.Cancelled,
                RemoteResourcePickerDisposition.UnsafeObservation => RemoteSelectionSessionState.NeedsDecision,
                _ => RemoteSelectionSessionState.Cancelled,
            };
            await CompleteOrThrowAsync(session, terminal).ConfigureAwait(false);
            completionPersisted = true;
            return terminal == RemoteSelectionSessionState.Confirmed
                ? outcome!.Selections
                : Array.Empty<OwnedRemoteSelection>();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!completionPersisted)
                await CompleteBestEffortAsync(session, RemoteSelectionSessionState.Cancelled)
                    .ConfigureAwait(false);
            throw;
        }
        catch
        {
            if (!completionPersisted)
                await CompleteBestEffortAsync(
                        session,
                        browserMayHaveStarted
                            ? RemoteSelectionSessionState.NeedsDecision
                            : RemoteSelectionSessionState.Cancelled)
                    .ConfigureAwait(false);
            throw;
        }
    }

    private BaiduNavigationAuthorizationRequest Navigation() => new(
        BaiduAutomationAction.OpenOfficialUri,
        OwnDriveUri,
        BaiduSelectorCatalog.ProtocolVersion,
        BaiduSelectorCatalog.SelectorVersion,
        applicationBuildVersion,
        new BaiduAutomationAcceptance(
            false,
            BaiduSelectorCatalog.ProtocolVersion,
            BaiduSelectorCatalog.SelectorVersion,
            applicationBuildVersion),
        BaiduProtocolValidationStatus.NotValidated);

    private async Task CompleteOrThrowAsync(
        RemoteSelectionSession session,
        RemoteSelectionSessionState terminal)
    {
        if (await sessions.TryCompleteAsync(
                session.SessionId, session.WorkspaceDraftId, session.AccountId,
                session.Generation, session.Revision, terminal,
                timeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(false))
            return;
        var persisted = await sessions.GetAsync(
            session.SessionId, session.WorkspaceDraftId, session.AccountId,
            session.Generation, CancellationToken.None).ConfigureAwait(false);
        if (persisted?.State == terminal) return;
        throw new InvalidOperationException("Resource selection session completion lost its exact ownership.");
    }

    private async Task CompleteBestEffortAsync(
        RemoteSelectionSession session,
        RemoteSelectionSessionState terminal)
    {
        try
        {
            await sessions.TryCompleteAsync(
                session.SessionId, session.WorkspaceDraftId, session.AccountId,
                session.Generation, session.Revision, terminal,
                timeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The original failure remains authoritative; startup recovery fences any Active session.
        }
    }

    private static void ValidateSession(RemoteSelectionSession session, Guid accountId)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.AccountId != accountId || session.Generation < 1 ||
            session.State != RemoteSelectionSessionState.Active)
            throw new InvalidOperationException("The resource selection session is not bound to the requested account.");
    }
}

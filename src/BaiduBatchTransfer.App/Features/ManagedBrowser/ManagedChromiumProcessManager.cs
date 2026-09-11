using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Infrastructure.Security;
using System.IO;
using System.Text.Json;
using System.Net.Http;
using System.Net.WebSockets;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

internal sealed record ManagedBrowserTargetSnapshot(Guid BrowserId, string TargetId, Uri PageUri);

internal interface IManagedBrowserTargetProbe
{
    Task<ManagedBrowserTargetSnapshot?> ReadAsync(DevToolsActivePort port, string? exactTargetId, CancellationToken cancellationToken,
        Action<string>? targetDiscovered = null);
}

internal sealed class ChromiumTargetProbe : IManagedBrowserTargetProbe
{
    private readonly IChromiumTargetTransport transport;
    internal ChromiumTargetProbe(IChromiumTargetTransport transport) { this.transport = transport; }
    public ChromiumTargetProbe() : this(new ChromiumTargetTransport()) { }
    public async Task<ManagedBrowserTargetSnapshot?> ReadAsync(DevToolsActivePort port, string? exactTargetId,
        CancellationToken cancellationToken, Action<string>? targetDiscovered = null)
    {
        try
        {
            using var version = await transport.VersionAsync(port, cancellationToken).ConfigureAwait(false);
            if (!version.RootElement.TryGetProperty("webSocketDebuggerUrl", out var browserEndpoint) ||
                browserEndpoint.ValueKind != JsonValueKind.String || browserEndpoint.GetString() != port.WebSocketEndpoint.AbsoluteUri)
                throw new IOException("The browser GUID does not match the pinned port file.");
            if (exactTargetId is null)
            {
                using var list = await transport.CommandAsync(port, "Target.getTargets", null, cancellationToken).ConfigureAwait(false);
                if (list is null || !list.RootElement.TryGetProperty("result", out var result) ||
                    !result.TryGetProperty("targetInfos", out var infos) || infos.ValueKind != JsonValueKind.Array || infos.GetArrayLength() > 128)
                    return null;
                var pages = infos.EnumerateArray().Where(IsPage).Take(2).ToArray();
                if (pages.Length != 1 || !TryTargetId(pages[0], out exactTargetId)) return null;
                targetDiscovered?.Invoke(exactTargetId!);
            }
            using var current = await transport.CommandAsync(port, "Target.getTargetInfo", exactTargetId, cancellationToken).ConfigureAwait(false);
            if (current is null || !current.RootElement.TryGetProperty("result", out var currentResult) ||
                !currentResult.TryGetProperty("targetInfo", out var info) || !IsPage(info) ||
                !TryTargetId(info, out var observedId) || observedId != exactTargetId ||
                !info.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var pageUri)) return null;
            return new(port.BrowserId, exactTargetId!, pageUri);
        }
        catch (HttpRequestException) { return null; }
        catch (WebSocketException) { return null; }
        catch (JsonException) { return null; }
    }
    private static bool IsPage(JsonElement info) => info.ValueKind == JsonValueKind.Object &&
        info.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "page";
    private static bool TryTargetId(JsonElement info, out string? id)
    {
        id = null;
        if (!info.TryGetProperty("targetId", out var value) || value.ValueKind != JsonValueKind.String) return false;
        id = value.GetString();
        return id is { Length: > 0 and <= 256 } && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }
}

internal interface IChromiumTargetTransport
{
    Task<JsonDocument> VersionAsync(DevToolsActivePort port, CancellationToken token);
    Task<JsonDocument?> CommandAsync(DevToolsActivePort port, string method, string? targetId, CancellationToken token);
}

internal sealed class ChromiumTargetTransport : IChromiumTargetTransport
{
    private const int MaximumResponseBytes = 65536;
    public async Task<JsonDocument> VersionAsync(DevToolsActivePort port, CancellationToken token)
    {
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await http.GetAsync(new Uri(port.Endpoint, "json/version"), HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new IOException("The browser response exceeds its bound.");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var bytes = new byte[MaximumResponseBytes + 1];
        var total = 0;
        while (total < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(total), token).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        if (total > MaximumResponseBytes) throw new IOException("The browser response exceeds its bound.");
        return JsonDocument.Parse(bytes.AsMemory(0, total), new JsonDocumentOptions { MaxDepth = 16 });
    }
    public async Task<JsonDocument?> CommandAsync(DevToolsActivePort port, string method, string? targetId, CancellationToken token)
    {
        // This transport is deliberately not a generic CDP/script gateway.
        if (method is not ("Target.getTargets" or "Target.getTargetInfo") ||
            (method == "Target.getTargetInfo" && string.IsNullOrEmpty(targetId)))
            throw new ArgumentException("Unsupported browser proof command.", nameof(method));
        using var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        await socket.ConnectAsync(port.WebSocketEndpoint, token).ConfigureAwait(false);
        var request = method == "Target.getTargets"
            ? JsonSerializer.SerializeToUtf8Bytes(new { id = 1, method, @params = new { } })
            : JsonSerializer.SerializeToUtf8Bytes(new { id = 1, method, @params = new { targetId } });
        await socket.SendAsync(request.AsMemory(), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
        for (var message = 0; message < 32; message++)
        {
            var bytes = new byte[MaximumResponseBytes + 1];
            var total = 0;
            ValueWebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(bytes.AsMemory(total), token).ConfigureAwait(false);
                if (received.MessageType != WebSocketMessageType.Text) return null;
                total += received.Count;
                if (total > MaximumResponseBytes) throw new IOException("The browser proof response exceeds its bound.");
            } while (!received.EndOfMessage);
            var document = JsonDocument.Parse(bytes.AsMemory(0, total), new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.TryGetProperty("id", out var id) && id.TryGetInt32(out var number) && number == 1)
            {
                if (!document.RootElement.TryGetProperty("error", out _)) return document;
                document.Dispose();
                return null;
            }
            document.Dispose();
        }
        throw new IOException("The browser sent too many unrelated proof messages.");
    }
}

public enum ManagedChromiumLaunchStatus { Ready, WaitingForPage }
public enum ManagedChromiumLaunchStage
{
    Entered, ProfilePinned, ExecutablePinned, ProcessLaunched, RootVerified,
    PortRead, TargetBound
}
public sealed record ManagedChromiumLaunchResult(ManagedChromiumLaunchStatus Status, ManagedChromiumSessionHandle? Session);

public sealed class ManagedChromiumSessionHandle
{
    internal ManagedChromiumSessionHandle(int rootProcessId, BrowserInstallationIdentity installation,
        ManagedBrowserProfileDescriptor profile, DevToolsActivePort port, string targetId)
    { RootProcessId = rootProcessId; Installation = installation; Profile = profile; BrowserId = port.BrowserId; TargetId = targetId; Endpoint = port.Endpoint; }
    public int RootProcessId { get; }
    public BrowserInstallationIdentity Installation { get; }
    public ManagedBrowserProfileDescriptor Profile { get; }
    public Guid BrowserId { get; }
    public string TargetId { get; }
    public Uri Endpoint { get; }
    public override string ToString() => "ManagedChromiumSessionHandle { Identity = [REDACTED] }";
}

public sealed partial class ManagedChromiumProcessManager : IAsyncDisposable, IDisposable
{
    private readonly Func<BrowserInstallationIdentity, CancellationToken, Task<IBrowserExecutableLaunchLease>> pinExecutable;
    private readonly Func<ManagedBrowserProfileDescriptor, CancellationToken, Task<IManagedBrowserProfileLease>> pinProfile;
    private readonly IManagedChromiumProcessFactory processes;
    private readonly IWindowsTcpOwnerTable listeners;
    private readonly IManagedBrowserTargetProbe targets;
    private readonly TimeSpan startupTimeout;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly object gate = new();
    private readonly Dictionary<Guid, OwnedBrowser> owned = [];
    private readonly CancellationTokenSource stopping = new();
    private bool disposed;
    private Task? shutdown;

    internal ManagedChromiumProcessManager(
        Func<BrowserInstallationIdentity, CancellationToken, Task<IBrowserExecutableLaunchLease>> pinExecutable,
        Func<ManagedBrowserProfileDescriptor, CancellationToken, Task<IManagedBrowserProfileLease>> pinProfile,
        IManagedChromiumProcessFactory processes, IWindowsTcpOwnerTable listeners, IManagedBrowserTargetProbe targets,
        TimeSpan? startupTimeout = null)
    {
        this.pinExecutable = pinExecutable ?? throw new ArgumentNullException(nameof(pinExecutable));
        this.pinProfile = pinProfile ?? throw new ArgumentNullException(nameof(pinProfile));
        this.processes = processes ?? throw new ArgumentNullException(nameof(processes));
        this.listeners = listeners ?? throw new ArgumentNullException(nameof(listeners));
        this.targets = targets ?? throw new ArgumentNullException(nameof(targets));
        this.startupTimeout = startupTimeout ?? TimeSpan.FromSeconds(10);
        if (this.startupTimeout <= TimeSpan.Zero || this.startupTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(startupTimeout));
    }

    public ManagedChromiumProcessManager(FileSystemManagedBrowserProfilePolicy profiles)
        : this(new BrowserExecutableVerifier().PinForLaunchAsync, profiles.PinExistingAsync,
            new WindowsJobObject(), new WindowsTcpOwnerTable(), new ChromiumTargetProbe()) { }

    public async Task<ManagedChromiumLaunchResult> LaunchAsync(BrowserInstallationIdentity installation,
        ManagedBrowserProfileDescriptor profile, CancellationToken cancellationToken = default,
        Action<ManagedChromiumLaunchStage>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(profile);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        var token = linked.Token;
        progress?.Invoke(ManagedChromiumLaunchStage.Entered);
        await serial.WaitAsync(token).ConfigureAwait(false);
        try
        {
            OwnedBrowser? entry;
            lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); owned.TryGetValue(profile.AccountId, out entry); }
            if (installation.BrowserFamily != profile.Family) throw new IOException("Browser/profile family mismatch.");
            if (entry is not null)
            {
                if (!SameInstallation(entry.Executable.Identity, installation) || !SameProfile(entry.Profile.Profile, profile))
                    throw new IOException("This account already owns a different browser/profile tuple.");
                if (entry.Process.IsAlive)
                {
                    await ReadPortAsync(entry, token).ConfigureAwait(false);
                    progress?.Invoke(ManagedChromiumLaunchStage.PortRead);
                    var rebound = await BindAsync(entry, token).ConfigureAwait(false);
                    progress?.Invoke(ManagedChromiumLaunchStage.TargetBound);
                    return rebound;
                }
                if (!Remove(entry)) throw new IOException("The previous owned browser job has not fully exited.");
                entry = null; // New pins are not owned by the removed browser, even if acquisition is cancelled.
            }

            IManagedBrowserProfileLease? profileLease = null;
            IBrowserExecutableLaunchLease? executable = null;
            IManagedChromiumProcess? process = null;
            try
            {
                profileLease = await pinProfile(profile, token).ConfigureAwait(false);
                progress?.Invoke(ManagedChromiumLaunchStage.ProfilePinned);
                executable = await pinExecutable(installation, token).ConfigureAwait(false);
                progress?.Invoke(ManagedChromiumLaunchStage.ExecutablePinned);
                if (!SameProfile(profile, profileLease.Profile) || !SameInstallation(installation, executable.Identity) ||
                    !profileLease.Validate() || !executable.Validate()) throw new IOException("The launch identity changed.");
                token.ThrowIfCancellationRequested();
                var launchFailed = false;
                try { process = processes.Launch(executable, profileLease); }
                catch (OwnedChromiumLaunchException failure) { process = failure.Process; launchFailed = true; }
                progress?.Invoke(ManagedChromiumLaunchStage.ProcessLaunched);
                entry = new OwnedBrowser(executable, profileLease, process);
                executable = null; profileLease = null; process = null; // Exactly one owner, including a late shutdown race.
                lock (gate)
                {
                    owned.Add(profile.AccountId, entry);
                    ObjectDisposedException.ThrowIf(disposed, this);
                }
                if (launchFailed) throw new IOException("Browser launch failed; its owned job remains tracked until confirmed exit.");
                RequireRoot(entry);
                progress?.Invoke(ManagedChromiumLaunchStage.RootVerified);
                await ReadPortAsync(entry, token).ConfigureAwait(false);
                progress?.Invoke(ManagedChromiumLaunchStage.PortRead);
                var bound = await BindAsync(entry, token).ConfigureAwait(false);
                progress?.Invoke(ManagedChromiumLaunchStage.TargetBound);
                return bound;
            }
            catch (OperationCanceledException) when (entry is not null && !stopping.IsCancellationRequested)
            {
                // Ending the login wait is not an explicit browser shutdown. Keep the exact job and pins.
                throw;
            }
            catch (TimeoutException) when (entry is not null && !stopping.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                if (entry is not null) Remove(entry);
                process?.Terminate(); process?.Dispose(); executable?.Dispose(); profileLease?.Dispose();
                throw;
            }
        }
        finally { serial.Release(); }
    }

    private async Task ReadPortAsync(OwnedBrowser entry, CancellationToken token)
    {
        RequireRoot(entry);
        if (entry.Port is null)
            (entry.Port, entry.PortFile) = await new DevToolsActivePortReader().ReadAsync(
                entry.Profile, entry.Process, startupTimeout, token).ConfigureAwait(false);
    }

    public async Task<ManagedChromiumLaunchResult> ReconnectAsync(ManagedChromiumSessionHandle exactSession,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exactSession);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        await serial.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            OwnedBrowser entry;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!owned.TryGetValue(exactSession.Profile.AccountId, out entry!) || !ReferenceEquals(entry.Session, exactSession))
                    throw new IOException("Only this manager's exact active session can reconnect.");
            }
            return await BindAsync(entry, linked.Token).ConfigureAwait(false);
        }
        finally { serial.Release(); }
    }

    private async Task<ManagedChromiumLaunchResult> BindAsync(OwnedBrowser entry, CancellationToken token)
    {
        var owner = RequireOwnership(entry);
        ManagedBrowserTargetSnapshot? target;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(startupTimeout);
        var acceptingTarget = true;
        void SelectTarget(string id)
        {
            lock (entry.ProofGate)
            {
                if (!acceptingTarget || deadline.IsCancellationRequested || token.IsCancellationRequested) return;
                if (id.Length is 0 or > 256 || !id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
                    throw new IOException("Invalid browser target identity.");
                entry.TargetId ??= id;
            }
        }
        try { target = await targets.ReadAsync(entry.Port!, entry.TargetId, deadline.Token, SelectTarget)
            .WaitAsync(startupTimeout, token).ConfigureAwait(false); }
        catch (TimeoutException) { target = null; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { target = null; }
        finally
        {
            lock (entry.ProofGate) acceptingTarget = false;
            deadline.Cancel();
        }
        RequireOwnership(entry, owner);
        if (target is null) return new(ManagedChromiumLaunchStatus.WaitingForPage, null);
        if (target.BrowserId != entry.Port!.BrowserId) throw new IOException("The browser endpoint identity changed.");
        if (string.IsNullOrWhiteSpace(target.TargetId) || target.TargetId.Length > 256 || target.TargetId.Any(char.IsControl))
            throw new IOException("Invalid browser target identity.");
        entry.TargetId ??= target.TargetId;
        if (entry.TargetId != target.TargetId || ManagedBrowserRoutePolicy.Classify(target.PageUri) == ManagedBrowserRoute.Rejected)
            return new(ManagedChromiumLaunchStatus.WaitingForPage, null);
        entry.Session ??= new(entry.Process.RootProcessId, entry.Executable.Identity, entry.Profile.Profile, entry.Port, entry.TargetId);
        token.ThrowIfCancellationRequested();
        return new(ManagedChromiumLaunchStatus.Ready, entry.Session);
    }

    private void RequireRoot(OwnedBrowser entry)
    {
        if (!entry.Process.IsAlive || !entry.Profile.Validate() || !entry.Executable.Validate() ||
            !entry.Process.OwnsProcess(entry.Process.RootProcessId) ||
            !entry.Process.VerifyExecutable(entry.Process.RootProcessId, entry.Executable))
            throw new IOException("The app-owned browser process could not be proved.");
    }

    private int RequireOwnership(OwnedBrowser entry, int? expectedOwner = null)
    {
        RequireRoot(entry);
        if (entry.Port is null || entry.PortFile is null || !entry.PortFile.Validate())
            throw new IOException("The pinned browser endpoint is no longer valid.");
        int owner;
        try { owner = WindowsTcpOwnerTable.RequireUniqueLoopbackOwner(listeners.ReadListeners(), entry.Port.Port); }
        catch (InvalidDataException) { throw new IOException("The browser listener is missing or ambiguous."); }
        if ((expectedOwner is not null && owner != expectedOwner) ||
            (entry.ListenerProcessId is not null && owner != entry.ListenerProcessId) || !entry.Process.OwnsProcess(owner) ||
            !entry.Process.VerifyExecutable(owner, entry.Executable) || !entry.PortFile.Validate() || !entry.Process.IsAlive)
            throw new IOException("The browser listener is not owned by this app's job.");
        entry.ListenerProcessId ??= owner;
        return owner;
    }

    public async Task<bool> StopAsync(ManagedChromiumSessionHandle exactSession, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exactSession);
        await serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            OwnedBrowser? entry;
            lock (gate)
                if (!owned.TryGetValue(exactSession.Profile.AccountId, out entry) || !ReferenceEquals(entry.Session, exactSession)) return false;
            try { await entry.Process.CloseGracefullyAsync(TimeSpan.FromSeconds(2), cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { }
            return await Task.Run(() => Remove(entry), CancellationToken.None).ConfigureAwait(false);
        }
        finally { serial.Release(); }
    }

    // This classifies only this manager's held tuples, never arbitrary external processes.
    internal async Task<ManagedProfileOwnership> ClassifyOwnedProfileAsync(ManagedBrowserProfileDescriptor exactProfile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exactProfile);
        await serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!owned.TryGetValue(exactProfile.AccountId, out var entry)) return ManagedProfileOwnership.None;
                if (SameProfile(entry.Profile.Profile, exactProfile)) return ManagedProfileOwnership.Matching;
                return entry.Profile.Profile.Family != exactProfile.Family
                    ? ManagedProfileOwnership.DifferentFamily : ManagedProfileOwnership.Conflict;
            }
        }
        finally { serial.Release(); }
    }

    /// <summary>Stops only the currently owned exact profile tuple, including pre-target startup.</summary>
    public async Task<ManagedBrowserStopStatus> StopOwnedProfileAsync(ManagedBrowserProfileDescriptor exactProfile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exactProfile);
        await serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            OwnedBrowser? entry;
            lock (gate)
            {
                if (!owned.TryGetValue(exactProfile.AccountId, out entry)) return ManagedBrowserStopStatus.NotRunning;
                if (!SameProfile(entry.Profile.Profile, exactProfile)) return ManagedBrowserStopStatus.Blocked;
            }
            try
            {
                await entry.Process.CloseGracefullyAsync(TimeSpan.FromSeconds(2), cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException) { }
            catch (IOException) { return ManagedBrowserStopStatus.Blocked; }
            return await Task.Run(() => Remove(entry), CancellationToken.None).ConfigureAwait(false)
                ? ManagedBrowserStopStatus.Stopped : ManagedBrowserStopStatus.Blocked;
        }
        finally { serial.Release(); }
    }

    private bool Remove(OwnedBrowser entry)
    {
        if (!entry.TryDispose()) return false;
        lock (gate)
            if (owned.TryGetValue(entry.Profile.Profile.AccountId, out var current) && ReferenceEquals(entry, current))
                owned.Remove(entry.Profile.Profile.AccountId);
        return true;
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (shutdown is null || shutdown.IsFaulted || shutdown.IsCanceled || (shutdown.IsCompleted && owned.Count != 0)) shutdown = ShutdownAsync();
            return new ValueTask(shutdown);
        }
    }
    private async Task ShutdownAsync()
    {
        stopping.Cancel();
        if (!await serial.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false))
            throw new IOException("A browser launch is still in flight; shutdown cannot confirm its ownership cleanup.");
        OwnedBrowser[] entries;
        try { lock (gate) entries = owned.Values.ToArray(); }
        finally { serial.Release(); }
        try
        {
            await Task.WhenAll(entries.Select(async entry =>
            {
                try { await entry.Process.CloseGracefullyAsync(TimeSpan.FromSeconds(2), CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch (Exception) { /* Held job disposal below is the bounded fallback. */ }
            })).ConfigureAwait(false);
        }
        finally { await Task.Run(Dispose).ConfigureAwait(false); }
        lock (gate)
            if (owned.Count != 0) throw new IOException("An app-owned browser job has not confirmed exit; profile pins are retained.");
    }
    public void Dispose()
    {
        OwnedBrowser[] entries;
        lock (gate)
        {
            disposed = true;
            entries = owned.Values.ToArray();
        }
        stopping.Cancel();
        foreach (var entry in entries)
        {
            try { Remove(entry); }
            catch (Exception) { /* Retain this entry and its pins for a later explicit shutdown retry. */ }
        }
    }

    private static bool SameProfile(ManagedBrowserProfileDescriptor left, ManagedBrowserProfileDescriptor right) =>
        left.AccountId == right.AccountId && left.Family == right.Family && left.ProfileId == right.ProfileId &&
        left.OwnershipId == right.OwnershipId && string.Equals(left.CanonicalSourcePath, right.CanonicalSourcePath, StringComparison.OrdinalIgnoreCase);
    private static bool SameInstallation(BrowserInstallationIdentity left, BrowserInstallationIdentity right) =>
        left.InstallationId == right.InstallationId && left.BrowserFamily == right.BrowserFamily &&
        left.FileIdentity == right.FileIdentity && left.Publisher == right.Publisher && left.ProductName == right.ProductName &&
        string.Equals(left.ExecutablePath, right.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.InstallationDirectory, right.InstallationDirectory, StringComparison.OrdinalIgnoreCase);

    private sealed class OwnedBrowser(IBrowserExecutableLaunchLease executable, IManagedBrowserProfileLease profile,
        IManagedChromiumProcess process)
    {
        private readonly object disposalGate = new();
        private bool disposed, terminationRequested;
        public IBrowserExecutableLaunchLease Executable { get; } = executable;
        public IManagedBrowserProfileLease Profile { get; } = profile;
        public IManagedChromiumProcess Process { get; } = process;
        public DevToolsActivePort? Port;
        public IManagedBrowserPortFileLease? PortFile;
        public string? TargetId;
        public int? ListenerProcessId;
        public ManagedChromiumSessionHandle? Session;
        public object ProofGate { get; } = new();
        public bool TryDispose()
        {
            lock (disposalGate)
            {
                if (disposed) return true;
                if (!terminationRequested)
                {
                    try { Process.Terminate(); }
                    catch (IOException) { return false; } // Preserve the entry and both pins for an explicit retry.
                    terminationRequested = true;
                }
                if (!Process.WaitForExit(TimeSpan.FromSeconds(2))) return false;
                disposed = true;
                Process.Dispose(); PortFile?.Dispose(); Profile.Dispose(); Executable.Dispose();
                return true;
            }
        }
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using Velopack.Locators;

namespace BaiduBatchTransfer.App.Updates;

internal static class SignedUpdateGuard
{
    internal const string ModeArgument = "--superpan-ota-guard";
    private static readonly TimeSpan ParentExitTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UpdaterExitTimeout = TimeSpan.FromMinutes(10);

    internal static bool Start()
    {
        if (!OperatingSystem.IsWindows() ||
            string.IsNullOrWhiteSpace(Environment.ProcessPath) ||
            !Path.IsPathFullyQualified(Environment.ProcessPath)) return false;
        using var handshake = CreateHandshakeServerForTesting();
        var nonce = RandomNumberGenerator.GetBytes(32);
        using var current = Process.GetCurrentProcess();
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath,
            WorkingDirectory = Path.GetDirectoryName(Environment.ProcessPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add(ModeArgument);
        startInfo.ArgumentList.Add(current.Id.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(handshake.Name);
        startInfo.ArgumentList.Add(Convert.ToHexString(nonce));
        try
        {
            using var guard = Process.Start(startInfo);
            if (guard is null) return false;
            var ready = WaitForGuardReadyForTesting(handshake.Server, nonce, HandshakeTimeout)
                .GetAwaiter().GetResult();
            if (!ready && !guard.HasExited) guard.Kill(entireProcessTree: true);
            return ready;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or
                                          IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return false;
        }
    }

    internal static bool TryRun(string[] args, ApplicationBuildInfo buildInfo, out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(buildInfo);
        if (args.Length == 0 || !string.Equals(args[0], ModeArgument, StringComparison.Ordinal))
        {
            exitCode = 0;
            return false;
        }
        exitCode = RunGuard(args, buildInfo) ? 0 : 2;
        return true;
    }

    private static bool RunGuard(string[] args, ApplicationBuildInfo buildInfo)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 5 ||
            !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parentPid) ||
            parentPid <= 0 || parentPid == Environment.ProcessId ||
            !long.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var parentStartTicks) ||
            parentStartTicks <= 0 || !IsSafePipeName(args[3]) ||
            !TryParseNonce(args[4], out var nonce) ||
            buildInfo.HttpsFeedUrl is not { } feedIdentity ||
            !TryOpenVerifiedParentForTesting(parentPid, parentStartTicks, out var parent)) return false;

        using var verifiedParent = parent!;
        {
            if (!SignalReady(args[3], nonce) ||
                !verifiedParent.WaitForExit(checked((int)ParentExitTimeout.TotalMilliseconds))) return false;
        }

        var succeeded = false;
        try
        {
            var locator = VelopackLocator.Current;
            var packagesRoot = locator.PackagesDir;
            if (string.IsNullOrWhiteSpace(packagesRoot)) return false;
            using var client = VelopackUpdateClient.Create(buildInfo);
            if (!client.IsInstalled) return false;
            var gate = new SignedFeedPackageGate(
                buildInfo.SignedPendingReceiptPath,
                new SignedFeedStateStore(
                    buildInfo.SignedFeedStatePath,
                    feedIdentity,
                    client.CurrentVersion ?? buildInfo.ProductVersion),
                packagesRoot);
            succeeded = ValidateAndLaunchAfterParentExitForTesting(
                static () => { },
                () => client.UpdatePendingRestart,
                gate,
                packagePath => LaunchNativeUpdater(
                    locator.UpdateExePath,
                    packagePath,
                    locator.RootAppDir,
                    packagesRoot));
            return succeeded;
        }
        finally
        {
            if (!succeeded) SignedUpdateGuardFailureMarker.TryWrite(buildInfo.SignedGuardFailureMarkerPath);
        }
    }

    internal static bool TryOpenVerifiedParentForTesting(
        int parentPid,
        long expectedStartTicks,
        out Process? parent)
    {
        parent = null;
        try
        {
            var candidate = Process.GetProcessById(parentPid);
            if (candidate.StartTime.ToUniversalTime().Ticks != expectedStartTicks || candidate.HasExited)
            {
                candidate.Dispose();
                return false;
            }
            parent = candidate;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    internal static SignedUpdateHandshakeServer CreateHandshakeServerForTesting()
    {
        var name = $"SuperPan.OtaGuard.{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}";
        var server = new NamedPipeServerStream(
            name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
        return new SignedUpdateHandshakeServer(name, server);
    }

    internal static async Task<bool> WaitForGuardReadyForTesting(
        NamedPipeServerStream server,
        byte[] expectedNonce,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(expectedNonce);
        if (expectedNonce.Length != 32 || timeout <= TimeSpan.Zero) return false;
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await server.WaitForConnectionAsync(cancellation.Token).ConfigureAwait(false);
            var received = new byte[32];
            var offset = 0;
            while (offset < received.Length)
            {
                var read = await server.ReadAsync(received.AsMemory(offset), cancellation.Token)
                    .ConfigureAwait(false);
                if (read == 0) return false;
                offset += read;
            }
            return CryptographicOperations.FixedTimeEquals(received, expectedNonce);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
            return false;
        }
    }

    private static bool SignalReady(string pipeName, byte[] nonce)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            pipe.Connect(checked((int)HandshakeTimeout.TotalMilliseconds));
            pipe.Write(nonce);
            pipe.Flush();
            return true;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or
                                          UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    internal static bool ValidateAndLaunchAfterParentExitForTesting(
        Action waitForParentExit,
        Func<VelopackClientAsset?> readPending,
        SignedFeedPackageGate gate,
        Func<string, bool> invokeUpdater)
    {
        ArgumentNullException.ThrowIfNull(waitForParentExit);
        ArgumentNullException.ThrowIfNull(readPending);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(invokeUpdater);
        waitForParentExit();
        var pending = readPending();
        if (pending is null) return false;
        using var authorization = gate.TryAcquireLaunchAuthorization(pending);
        return authorization is not null && invokeUpdater(authorization.PackagePath);
    }

    private static bool LaunchNativeUpdater(
        string? updateExePath,
        string packagePath,
        string? appRoot,
        string packagesRoot)
    {
        if (string.IsNullOrWhiteSpace(updateExePath) || string.IsNullOrWhiteSpace(appRoot)) return false;
        try
        {
            var startInfo = CreateNativeUpdaterStartInfoForTesting(
                updateExePath, packagePath, appRoot, packagesRoot);
            using var updater = Process.Start(startInfo);
            return updater is not null && WaitForUpdaterExitForTesting(updater, UpdaterExitTimeout);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or
                                          IOException or UnauthorizedAccessException or
                                          InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    internal static bool WaitForUpdaterExitForTesting(Process updater, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(updater);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (updater.WaitForExit(checked((int)Math.Min(timeout.TotalMilliseconds, int.MaxValue))))
            return updater.ExitCode == 0;
        try
        {
            updater.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or
                                          NotSupportedException)
        {
            // Fail closed below; the verified package lease remains held until this process exits.
        }
        updater.WaitForExit();
        return false;
    }

    internal static ProcessStartInfo CreateNativeUpdaterStartInfoForTesting(
        string updateExePath,
        string packagePath,
        string appRoot,
        string packagesRoot)
    {
        var updater = ValidateDirectChild(updateExePath, appRoot, "Velopack updater");
        var package = ValidateDirectChild(packagePath, packagesRoot, "signed package");
        ValidatePathAncestors(appRoot);
        ValidatePathAncestors(packagesRoot);
        var info = new ProcessStartInfo
        {
            FileName = updater,
            WorkingDirectory = Path.GetDirectoryName(updater)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        info.ArgumentList.Add("apply");
        info.ArgumentList.Add("--package");
        info.ArgumentList.Add(package);
        info.ArgumentList.Add("--rootDir");
        info.ArgumentList.Add(Path.GetFullPath(appRoot));
        info.ArgumentList.Add("--packageDir");
        info.ArgumentList.Add(Path.GetFullPath(packagesRoot));
        return info;
    }

    private static string ValidateDirectChild(string path, string root, string description)
    {
        if (!Path.IsPathFullyQualified(path) || !Path.IsPathFullyQualified(root))
            throw new InvalidDataException($"The {description} path is not absolute.");
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!string.Equals(Path.GetDirectoryName(fullPath), fullRoot, PathComparison) ||
            new FileInfo(fullPath).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException($"The {description} path is not trusted.");
        return fullPath;
    }

    private static void ValidatePathAncestors(string path)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("An updater path contains a reparse point.");
            current = current.Parent;
        }
    }

    private static bool IsSafePipeName(string value) =>
        value.StartsWith("SuperPan.OtaGuard.", StringComparison.Ordinal) && value.Length <= 80 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '.');

    private static bool TryParseNonce(string value, out byte[] nonce)
    {
        nonce = [];
        if (value.Length != 64 || !value.All(Uri.IsHexDigit)) return false;
        try { nonce = Convert.FromHexString(value); return nonce.Length == 32; }
        catch (FormatException) { return false; }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}

internal sealed class SignedUpdateHandshakeServer(string name, NamedPipeServerStream server)
    : IDisposable, IAsyncDisposable
{
    internal string Name { get; } = name;
    internal NamedPipeServerStream Server { get; } = server;
    public void Dispose() => Server.Dispose();
    public ValueTask DisposeAsync() => Server.DisposeAsync();
}

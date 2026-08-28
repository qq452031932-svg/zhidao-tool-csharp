using BaiduBatchTransfer.Infrastructure.Security;
using Microsoft.Win32.SafeHandles;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

internal interface IManagedChromiumProcess : IDisposable
{
    int RootProcessId { get; }
    DateTimeOffset StartedAtUtc { get; }
    bool IsAlive { get; }
    bool OwnsProcess(int processId);
    bool VerifyExecutable(int processId, IBrowserExecutableLaunchLease executable);
    Task CloseGracefullyAsync(TimeSpan timeout, CancellationToken cancellationToken);
    void Terminate();
    bool WaitForExit(TimeSpan timeout);
}

internal interface IManagedChromiumProcessFactory
{
    IManagedChromiumProcess Launch(IBrowserExecutableLaunchLease executable, IManagedBrowserProfileLease profile);
}

internal sealed class OwnedChromiumLaunchException(IManagedChromiumProcess process)
    : IOException("Browser launch failed and its owned process has not confirmed exit.")
{
    public IManagedChromiumProcess Process { get; } = process;
}

internal sealed class WindowsJobObject : IManagedChromiumProcessFactory
{
    internal WindowsJobObject(IWindowsChromiumNativeApi native) { this.native = native; }
    public WindowsJobObject() : this(new WindowsChromiumNativeApi()) { }
    private readonly IWindowsChromiumNativeApi native;
    public IManagedChromiumProcess Launch(IBrowserExecutableLaunchLease executable, IManagedBrowserProfileLease profile)
    {
        if (!executable.Validate() || !profile.Validate() || executable.Identity.BrowserFamily != profile.Profile.Family)
            throw new IOException("The pinned browser/profile identity changed before launch.");
        var arguments = new[] { "--user-data-dir=" + profile.Profile.CanonicalSourcePath,
            "--remote-debugging-port=0", "--no-first-run", "--no-default-browser-check", "https://pan.baidu.com/" };
        var job = native.CreateJob();
        SuspendedChromiumProcess? root = null;
        try
        {
            if (!executable.Validate() || !profile.Validate()) throw new IOException("Launch identity changed.");
            root = native.CreateSuspended(executable.Identity.ExecutablePath, arguments);
            if (!native.Assign(job, root.Process)) throw new IOException("The browser could not be assigned to its app-owned job.");
            if (!native.IsAlive(root.Process) || !native.IsInJob(job, root.Process) ||
                !executable.MatchesExecutablePath(native.ReadImagePath(root.Process)) || !profile.Validate())
                throw new IOException("The suspended browser identity changed.");
            if (!native.Resume(root.Thread)) throw new IOException("The owned browser could not resume.");
            root.Thread.Dispose();
            return new OwnedJob(native, job, root);
        }
        catch
        {
            // This root was created suspended by this call. It never ran if assignment failed.
            if (root is not null)
            {
                var failed = new OwnedJob(native, job, root, terminateRoot: true);
                failed.Terminate();
                if (!failed.WaitForExit(TimeSpan.FromSeconds(2)))
                    throw new OwnedChromiumLaunchException(failed); // Transfer retained handles to the manager's cleanup path.
                failed.Dispose();
            }
            else job.Dispose();
            throw;
        }
    }

    public static string EncodeArgumentList(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var result = new StringBuilder();
        foreach (var argument in arguments)
        {
            if (argument is null || argument.Contains('\0')) throw new ArgumentException("Invalid native argument.", nameof(arguments));
            if (result.Length != 0) result.Append(' ');
            result.Append('"');
            var slashes = 0;
            foreach (var character in argument)
            {
                if (character == '\\') { slashes++; continue; }
                result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                result.Append(character);
                slashes = 0;
            }
            result.Append('\\', slashes * 2).Append('"');
        }
        if (result.Length > 32766) throw new ArgumentException("Native command line is too long.", nameof(arguments));
        return result.ToString();
    }

    private sealed class OwnedJob(IWindowsChromiumNativeApi native, SafeFileHandle job, SuspendedChromiumProcess root, bool terminateRoot = false)
        : IManagedChromiumProcess
    {
        private int terminated;
        public int RootProcessId => root.ProcessId;
        public DateTimeOffset StartedAtUtc => root.StartedAtUtc;
        public bool IsAlive => !root.Process.IsClosed && !job.IsClosed && native.IsAlive(root.Process);
        public bool OwnsProcess(int processId)
        {
            if (!IsAlive || processId <= 0) return false;
            using var process = native.OpenProcess(processId);
            return !process.IsInvalid && native.IsAlive(process) && native.IsInJob(job, process);
        }
        public bool VerifyExecutable(int processId, IBrowserExecutableLaunchLease executable)
        {
            if (!IsAlive || processId <= 0) return false;
            using var process = native.OpenProcess(processId);
            if (process.IsInvalid || !native.IsAlive(process) || !native.IsInJob(job, process)) return false;
            return executable.MatchesExecutablePath(native.ReadImagePath(process)) &&
                native.IsAlive(process) && native.IsInJob(job, process) && IsAlive;
        }
        public async Task CloseGracefullyAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(10)) throw new ArgumentOutOfRangeException(nameof(timeout));
            if (job.IsClosed) return;
            native.CloseWindows(job);
            var elapsed = Stopwatch.StartNew();
            while (IsAlive && elapsed.Elapsed < timeout)
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        }
        public void Terminate()
        {
            if (Interlocked.Exchange(ref terminated, 1) != 0) return;
            if (terminateRoot && !root.Process.IsClosed) native.TerminateProcess(root.Process);
            if (!job.IsClosed) native.TerminateJob(job);
        }
        public bool WaitForExit(TimeSpan timeout)
        {
            var elapsed = Stopwatch.StartNew();
            do
            {
                if (!native.IsAlive(root.Process) && native.IsJobEmpty(job)) return true;
                if (elapsed.Elapsed >= timeout) return false;
                Thread.Sleep(20);
            } while (true);
        }
        public void Dispose()
        {
            try { Terminate(); }
            finally { root.Thread.Dispose(); root.Process.Dispose(); job.Dispose(); }
        }
    }
}

internal sealed record SuspendedChromiumProcess(SafeProcessHandle Process, SafeWaitHandle Thread, int ProcessId,
    DateTimeOffset StartedAtUtc);

internal interface IWindowsChromiumNativeApi
{
    SafeFileHandle CreateJob();
    SuspendedChromiumProcess CreateSuspended(string executable, IReadOnlyList<string> arguments);
    bool Assign(SafeFileHandle job, SafeProcessHandle process);
    bool Resume(SafeWaitHandle thread);
    bool IsAlive(SafeProcessHandle process);
    bool IsInJob(SafeFileHandle job, SafeProcessHandle process);
    bool IsJobEmpty(SafeFileHandle job);
    string ReadImagePath(SafeProcessHandle process);
    SafeProcessHandle OpenProcess(int processId);
    void CloseWindows(SafeFileHandle job);
    void TerminateJob(SafeFileHandle job);
    void TerminateProcess(SafeProcessHandle process);
}

internal sealed class WindowsChromiumNativeApi : IWindowsChromiumNativeApi
{
    public SafeFileHandle CreateJob()
    {
        var job = Native.CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) { job.Dispose(); throw new IOException("Cannot create the managed browser job."); }
        var information = new ExtendedLimitInformation { Basic = new BasicLimitInformation { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE; no breakaway.
        if (!Native.SetInformationJobObject(job, 9, ref information, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        { job.Dispose(); throw new IOException("Cannot configure the managed browser job."); }
        return job;
    }
    public SuspendedChromiumProcess CreateSuspended(string executable, IReadOnlyList<string> arguments)
    {
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        var command = new StringBuilder(WindowsJobObject.EncodeArgumentList(new[] { executable }.Concat(arguments).ToArray()));
        var started = DateTimeOffset.UtcNow;
        // lpApplicationName is explicit; handles are not inherited; no shell and no hidden-window flags.
        if (!Native.CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, false, 0x00000004,
            IntPtr.Zero, Path.GetDirectoryName(executable), ref startup, out var information))
            throw new IOException("The managed browser process could not be created.");
        return new(new SafeProcessHandle(information.Process, true), new SafeWaitHandle(information.Thread, true),
            checked((int)information.ProcessId), started);
    }
    public bool Assign(SafeFileHandle job, SafeProcessHandle process) => Native.AssignProcessToJobObject(job, process);
    public bool Resume(SafeWaitHandle thread) => Native.ResumeThread(thread) == 1;
    public bool IsAlive(SafeProcessHandle process) => !process.IsInvalid && !process.IsClosed && Native.WaitForSingleObject(process, 0) == 258;
    public bool IsInJob(SafeFileHandle job, SafeProcessHandle process) =>
        !job.IsClosed && Native.IsProcessInJob(process, job, out var belongs) && belongs;
    public bool IsJobEmpty(SafeFileHandle job) => !job.IsClosed &&
        Native.QueryInformationJobObject(job, 1, out var accounting, (uint)Marshal.SizeOf<BasicAccountingInformation>(), IntPtr.Zero) &&
        accounting.ActiveProcesses == 0;
    public string ReadImagePath(SafeProcessHandle process)
    {
        var buffer = new StringBuilder(32768);
        var size = buffer.Capacity;
        if (!Native.QueryFullProcessImageNameW(process, 0, buffer, ref size)) throw new IOException("The process image cannot be verified.");
        return buffer.ToString();
    }
    public SafeProcessHandle OpenProcess(int processId) => Native.OpenProcess(0x00100000 | 0x1000, false, processId);
    public void CloseWindows(SafeFileHandle job)
    {
        Native.EnumWindows((window, _) =>
        {
            Native.GetWindowThreadProcessId(window, out var pid);
            if (pid is 0 or > int.MaxValue) return true;
            using var process = OpenProcess((int)pid);
            if (process.IsInvalid || !IsAlive(process) || !IsInJob(job, process)) return true;
            Native.GetWindowThreadProcessId(window, out var currentPid);
            if (currentPid == pid && IsAlive(process)) Native.PostMessageW(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
    }
    public void TerminateJob(SafeFileHandle job) { if (!job.IsClosed && !job.IsInvalid) Native.TerminateJobObject(job, 1); }
    public void TerminateProcess(SafeProcessHandle process) { if (!process.IsClosed && !process.IsInvalid) Native.TerminateProcess(process, 1); }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2;
        public IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccountingInformation
    {
        public long TotalUserTime, TotalKernelTime, PeriodUserTime, PeriodKernelTime;
        public uint TotalPageFaults, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }
    private static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimitInformation information, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass,
            out BasicAccountingInformation information, uint length, IntPtr returnedLength);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags,
            IntPtr environment, string? currentDirectory, ref StartupInfo startup, out ProcessInformation information);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern uint ResumeThread(SafeWaitHandle thread);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsProcessInJob(SafeProcessHandle process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref int length);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TerminateJobObject(SafeFileHandle job, uint code);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TerminateProcess(SafeProcessHandle process, uint code);
        public delegate bool WindowCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    }
}

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BundlePath,
    [string]$PublisherCertificatePath = (Join-Path $PSScriptRoot '..\eng\ota\SuperPan.OtaPublisher.cer'),
    [string]$ExpectedVersion,
    [string]$ExpectedPublisherSpkiSha256 = 'f905188d79bb427c1a6eacc4bc9741d68a407228f643f6a50bfd791211da6cff',
    [string]$OutputReportPath,
    [switch]$RequireTimestamp
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'ProductIdentity.ps1')
$productIdentity = Get-ProductIdentity -Path (Join-Path $repoRoot 'eng\ProductIdentity.props')
$productDisplayName = [string]$productIdentity.ProductDisplayName
$productDescription = [string]$productIdentity.ProductDescription
$productMainAssemblyName = [string]$productIdentity.ProductMainAssemblyName
$productMainExecutableName = [string]$productIdentity.ProductMainExecutableName
$productMainDllName = [string]$productIdentity.ProductMainDllName
$productMainStem = [IO.Path]::GetFileNameWithoutExtension($productMainExecutableName)
$productLegacyAssemblyName = [string]$productIdentity.ProductLegacyAssemblyName
$productLegacyExecutableName = [string]$productIdentity.ProductLegacyExecutableName
$productIconPath = [IO.Path]::GetFullPath((Join-Path $repoRoot (
    'src\BaiduBatchTransfer.App\Assets\Branding\' + $productMainStem + '.ico')))
$packageId = [string]$productIdentity.ProductPackageId
if ($packageId -cne 'SuperPan.BaiduBatchTransfer' -or
    $productLegacyExecutableName -cne 'BaiduBatchTransfer.App.exe') {
    throw 'Product identity changed a stable OTA compatibility identifier.'
}
if (-not (Test-Path -LiteralPath $productIconPath -PathType Leaf) -or
    (([IO.File]::GetAttributes($productIconPath) -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
    throw 'Canonical product icon is missing or unsafe.'
}
$channel = 'win-x64-stable'
$manifestName = 'upload-manifest.sha256'
$signatureName = 'upload-manifest.sha256.sig'
$releaseFeedName = "releases.$channel.json"
$assetCatalogName = "assets.$channel.json"
$releasesName = "RELEASES-$channel"
$utf8Strict = [Text.UTF8Encoding]::new($false, $true)
$signatureEvidence = [Collections.Generic.List[object]]::new()

if ($PSVersionTable.PSEdition -eq 'Desktop') {
    Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1') -ErrorAction Stop
    Add-Type -AssemblyName System.Security
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
} else {
    Add-Type -AssemblyName System.Security.Cryptography.Pkcs
}

function ConvertTo-LowerHex([byte[]]$Bytes) {
    return ([BitConverter]::ToString($Bytes)).Replace('-', '').ToLowerInvariant()
}

function Test-FixedBytesEqual([byte[]]$Left, [byte[]]$Right) {
    if ($Left.Length -ne $Right.Length) { return $false }
    $difference = 0
    for ($index = 0; $index -lt $Left.Length; $index++) {
        $difference = $difference -bor ($Left[$index] -bxor $Right[$index])
    }
    return $difference -eq 0
}

function Get-LowerHash([Security.Cryptography.HashAlgorithm]$Algorithm, [IO.Stream]$Stream) {
    try { return ConvertTo-LowerHex $Algorithm.ComputeHash($Stream) }
    finally { $Algorithm.Dispose(); $Stream.Dispose() }
}

function Get-FileSha256([string]$Path) {
    return Get-LowerHash ([Security.Cryptography.SHA256]::Create()) ([IO.File]::OpenRead($Path))
}

function Get-BytesSha256([byte[]]$Bytes) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return ConvertTo-LowerHex $algorithm.ComputeHash($Bytes) }
    finally { $algorithm.Dispose() }
}

function New-DerLength([int]$Length) {
    if ($Length -lt 0) { throw 'DER length is invalid.' }
    if ($Length -lt 128) { return ,([byte[]]@([byte]$Length)) }
    $bytes = [Collections.Generic.List[byte]]::new()
    while ($Length -gt 0) { $bytes.Insert(0, [byte]($Length -band 0xff)); $Length = $Length -shr 8 }
    return ,([byte[]](@([byte](0x80 -bor $bytes.Count)) + $bytes.ToArray()))
}

function New-DerValue([byte]$Tag, [byte[]]$Content) {
    return ,([byte[]](@($Tag) + (New-DerLength $Content.Length) + $Content))
}

function Read-DerValue([byte[]]$Bytes, [ref]$Offset, [byte]$ExpectedTag, [string]$Description) {
    $start = [int]$Offset.Value
    if ($start -ge $Bytes.Length -or $Bytes[$start] -ne $ExpectedTag) { throw "$Description has an unexpected ASN.1 tag." }
    $start++
    if ($start -ge $Bytes.Length) { throw "$Description has a truncated ASN.1 length." }
    $firstLength = $Bytes[$start]; $start++
    if ($firstLength -eq 0x80) { throw "$Description uses an indefinite ASN.1 length." }
    if (($firstLength -band 0x80) -eq 0) { $length = [int]$firstLength }
    else {
        $count = $firstLength -band 0x7f
        if ($count -lt 1 -or $count -gt 4 -or $start + $count -gt $Bytes.Length -or $Bytes[$start] -eq 0) {
            throw "$Description has an invalid ASN.1 length."
        }
        $length = 0
        for ($index = 0; $index -lt $count; $index++) { $length = ($length -shl 8) -bor $Bytes[$start + $index] }
        $start += $count
        if ($length -lt 128) { throw "$Description has a non-canonical ASN.1 length." }
    }
    if ($length -lt 0 -or $start + $length -gt $Bytes.Length) { throw "$Description is truncated." }
    $content = [byte[]]::new($length)
    if ($length -gt 0) { [Array]::Copy($Bytes, $start, $content, 0, $length) }
    $Offset.Value = $start + $length
    return ,$content
}

function Assert-NoDerRemainder([byte[]]$Bytes, [int]$Offset, [string]$Description) {
    if ($Offset -ne $Bytes.Length) { throw "$Description has trailing ASN.1 data." }
}

function Get-AuthenticodeDigest([byte[]]$Content) {
    $offset = 0
    $root = Read-DerValue $Content ([ref]$offset) 0x30 'Authenticode content'
    Assert-NoDerRemainder $Content $offset 'Authenticode content'
    $rootOffset = 0
    $imageData = Read-DerValue $root ([ref]$rootOffset) 0x30 'Authenticode image data'
    $imageOffset = 0
    $imageOid = Read-DerValue $imageData ([ref]$imageOffset) 0x06 'Authenticode image data OID'
    $spcPeImageDataOid = '1.3.6.1.4.1.311.2.1.15'
    if ((ConvertTo-LowerHex $imageOid) -cne '2b06010401823702010f') {
        throw "Executable Authenticode content is not bound to PE-image data ($spcPeImageDataOid)."
    }
    $digestInfo = Read-DerValue $root ([ref]$rootOffset) 0x30 'Authenticode digest info'
    Assert-NoDerRemainder $root $rootOffset 'Authenticode content'
    $digestOffset = 0
    $algorithm = Read-DerValue $digestInfo ([ref]$digestOffset) 0x30 'Authenticode digest algorithm'
    $algorithmOffset = 0
    $algorithmOid = Read-DerValue $algorithm ([ref]$algorithmOffset) 0x06 'Authenticode digest algorithm OID'
    if ($algorithmOffset -lt $algorithm.Length) {
        $nullValue = Read-DerValue $algorithm ([ref]$algorithmOffset) 0x05 'Authenticode digest algorithm parameters'
        if ($nullValue.Length -ne 0) { throw 'Authenticode digest algorithm parameters are invalid.' }
    }
    Assert-NoDerRemainder $algorithm $algorithmOffset 'Authenticode digest algorithm'
    $digest = Read-DerValue $digestInfo ([ref]$digestOffset) 0x04 'Authenticode image digest'
    Assert-NoDerRemainder $digestInfo $digestOffset 'Authenticode digest info'
    if ((ConvertTo-LowerHex $algorithmOid) -cne '608648016503040201' -or $digest.Length -ne 32) {
        throw 'Executable Authenticode image digest metadata is unsupported.'
    }
    return ,$digest
}

function Test-CanonicalVersion([string]$Value) {
    if ($Value -cnotmatch '^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$') { return $false }
    foreach ($part in $Value.Split('.')) { if ([int]$part -gt 65534) { return $false } }
    return $true
}

function Compare-Version([string]$Left, [string]$Right) {
    $a = @($Left.Split('.') | ForEach-Object { [int]$_ })
    $b = @($Right.Split('.') | ForEach-Object { [int]$_ })
    for ($i = 0; $i -lt 3; $i++) {
        if ($a[$i] -lt $b[$i]) { return -1 }
        if ($a[$i] -gt $b[$i]) { return 1 }
    }
    return 0
}

function Test-FlatAsciiName([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name) -or $Name.Length -gt 255 -or
        $Name -cne [IO.Path]::GetFileName($Name) -or $Name.Contains('/') -or $Name.Contains('\') -or
        $Name.EndsWith(' ') -or $Name.EndsWith('.') -or
        $Name.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) { return $false }
    foreach ($character in $Name.ToCharArray()) {
        if ([int]$character -lt 0x21 -or [int]$character -gt 0x7e) { return $false }
    }
    $stem = $Name.Split('.')[0].ToUpperInvariant()
    return $stem -notin @('CON','PRN','AUX','NUL','COM1','COM2','COM3','COM4','COM5','COM6','COM7','COM8','COM9','LPT1','LPT2','LPT3','LPT4','LPT5','LPT6','LPT7','LPT8','LPT9')
}

function Test-AllowedName([string]$Name) {
    if ($Name -cin @($manifestName, $signatureName, $releaseFeedName, $assetCatalogName, $releasesName,
            "$packageId-Setup.exe", "$packageId-Portable.zip")) { return $true }
    return $Name -cmatch ('^' + [Regex]::Escape($packageId) + '-(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})-(full|delta)\.nupkg$')
}

function Read-LimitedStream([IO.Stream]$Stream, [long]$Limit, [string]$Description) {
    $buffer = [byte[]]::new(65536)
    $memory = [IO.MemoryStream]::new()
    try {
        while (($read = $Stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            if ($memory.Length + $read -gt $Limit) { throw "$Description exceeds the allowed size." }
            $memory.Write($buffer, 0, $read)
        }
        return $memory.ToArray()
    }
    finally { $memory.Dispose(); $Stream.Dispose() }
}

function Get-CertificateSpki([Security.Cryptography.X509Certificates.X509Certificate2]$Certificate) {
    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($Certificate)
    if ($null -eq $rsa -or $rsa.KeySize -ne 3072) { throw 'Publisher certificate must use RSA-3072.' }
    try { return Get-RsaCertificateSpki $Certificate }
    finally { $rsa.Dispose() }
}

function Copy-FileBounded([string]$Source, [string]$Destination, [long]$Limit, [string]$Description) {
    $sourceInfo = [IO.FileInfo]::new($Source)
    if (-not $sourceInfo.Exists -or ($sourceInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $sourceInfo.Length -lt 1 -or $sourceInfo.Length -gt $Limit) { throw "$Description has an invalid size or file type." }
    $input = [IO.File]::Open($sourceInfo.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $output = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $buffer = [byte[]]::new(65536)
    [long]$total = 0
    try {
        while (($read = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
            if ($total -gt $Limit - $read) { throw "$Description exceeds the allowed size." }
            $output.Write($buffer, 0, $read)
            $total += $read
        }
        if ($total -ne $sourceInfo.Length) { throw "$Description changed while it was being copied." }
    }
    finally { $output.Dispose(); $input.Dispose() }
}

function Initialize-OtaJobRunner {
    if ('OtaJobRunner' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public sealed class OtaContainedResult {
    public int ExitCode;
    public string Failure;
    public bool SafeToCleanup;
}

public static class OtaJobRunner {
    const uint CREATE_SUSPENDED = 0x00000004;
    const uint CREATE_NO_WINDOW = 0x08000000;
    const uint STARTF_USESTDHANDLES = 0x00000100;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    const uint JOB_OBJECT_LIMIT_JOB_MEMORY = 0x00000200;
    const uint JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x00000008;
    const uint HANDLE_FLAG_INHERIT = 0x00000001;
    const uint WAIT_OBJECT_0 = 0;
    const uint STILL_ACTIVE = 259;

    [StructLayout(LayoutKind.Sequential)] struct STARTUPINFO {
        public uint cb; public string lpReserved; public string lpDesktop; public string lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION {
        public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId;
    }
    [StructLayout(LayoutKind.Sequential)] struct JOBOBJECT_BASIC_LIMIT_INFORMATION {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit;
        public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] struct IO_COUNTERS {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [StructLayout(LayoutKind.Sequential)] struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential)] struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern IntPtr CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern bool QueryInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length, out uint returnedLength);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern bool CreateProcess(string applicationName, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment,
        string currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError=true)] static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError=true)] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr GetStdHandle(int id);

    static string Quote(string value) {
        if (value == null) throw new ArgumentNullException("value");
        StringBuilder b = new StringBuilder(); b.Append('"'); int slashes = 0;
        foreach (char c in value) {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { b.Append('\\', slashes * 2 + 1); b.Append('"'); }
            else { if (slashes != 0) b.Append('\\', slashes); b.Append(c); }
            slashes = 0;
        }
        if (slashes != 0) b.Append('\\', slashes * 2); b.Append('"'); return b.ToString();
    }
    static uint ActiveProcesses(IntPtr job) {
        int size = Marshal.SizeOf(typeof(JOBOBJECT_BASIC_ACCOUNTING_INFORMATION));
        IntPtr memory = Marshal.AllocHGlobal(size);
        try {
            uint returned;
            if (!QueryInformationJobObject(job, 1, memory, (uint)size, out returned)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return ((JOBOBJECT_BASIC_ACCOUNTING_INFORMATION)Marshal.PtrToStructure(memory, typeof(JOBOBJECT_BASIC_ACCOUNTING_INFORMATION))).ActiveProcesses;
        } finally { Marshal.FreeHGlobal(memory); }
    }
    static bool WaitForEmpty(IntPtr job, IntPtr process, int milliseconds) {
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds) {
            bool mainDone = process == IntPtr.Zero || WaitForSingleObject(process, 0) == WAIT_OBJECT_0;
            if (mainDone && ActiveProcesses(job) == 0) return true;
            System.Threading.Thread.Sleep(50);
        }
        return false;
    }

    public static OtaContainedResult Run(string executable, string[] arguments, string workingDirectory,
        string stdoutPath, string stderrPath, int timeoutMilliseconds, long maximumLogBytes,
        string monitoredOutputPath, long maximumMonitoredOutputBytes, long jobMemoryLimitBytes,
        uint activeProcessLimit) {
        if (jobMemoryLimitBytes < 1 || jobMemoryLimitBytes > 1073741824L) throw new ArgumentOutOfRangeException("jobMemoryLimitBytes");
        if (activeProcessLimit < 1 || activeProcessLimit > 8) throw new ArgumentOutOfRangeException("activeProcessLimit");
        OtaContainedResult result = new OtaContainedResult { ExitCode = -1, SafeToCleanup = false };
        IntPtr job = IntPtr.Zero; PROCESS_INFORMATION pi = new PROCESS_INFORMATION();
        FileStream stdout = null, stderr = null; bool assigned = false;
        try {
            job = CreateJobObject(IntPtr.Zero, null); if (job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE |
                JOB_OBJECT_LIMIT_JOB_MEMORY | JOB_OBJECT_LIMIT_ACTIVE_PROCESS;
            limits.BasicLimitInformation.ActiveProcessLimit = activeProcessLimit;
            limits.JobMemoryLimit = new UIntPtr((ulong)jobMemoryLimitBytes);
            int limitSize = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr limitMemory = Marshal.AllocHGlobal(limitSize);
            try { Marshal.StructureToPtr(limits, limitMemory, false); if (!SetInformationJobObject(job, 9, limitMemory, (uint)limitSize)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
            finally { Marshal.FreeHGlobal(limitMemory); }
            stdout = new FileStream(stdoutPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            stderr = new FileStream(stderrPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            if (!SetHandleInformation(stdout.SafeFileHandle.DangerousGetHandle(), HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT) ||
                !SetHandleInformation(stderr.SafeFileHandle.DangerousGetHandle(), HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT)) throw new Win32Exception(Marshal.GetLastWin32Error());
            StringBuilder commandLine = new StringBuilder(Quote(executable));
            foreach (string argument in arguments) { commandLine.Append(' ').Append(Quote(argument)); }
            STARTUPINFO startup = new STARTUPINFO(); startup.cb = (uint)Marshal.SizeOf(typeof(STARTUPINFO));
            startup.dwFlags = STARTF_USESTDHANDLES; startup.hStdInput = GetStdHandle(-10);
            startup.hStdOutput = stdout.SafeFileHandle.DangerousGetHandle(); startup.hStdError = stderr.SafeFileHandle.DangerousGetHandle();
            if (!CreateProcess(executable, commandLine, IntPtr.Zero, IntPtr.Zero, true, CREATE_SUSPENDED | CREATE_NO_WINDOW,
                IntPtr.Zero, workingDirectory, ref startup, out pi)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!AssignProcessToJobObject(job, pi.hProcess)) { TerminateProcess(pi.hProcess, 0xEE01); WaitForSingleObject(pi.hProcess, 5000); throw new Win32Exception(Marshal.GetLastWin32Error()); }
            assigned = true;
            if (ResumeThread(pi.hThread) == UInt32.MaxValue) { TerminateJobObject(job, 0xEE02); throw new Win32Exception(Marshal.GetLastWin32Error()); }
            CloseHandle(pi.hThread); pi.hThread = IntPtr.Zero;
            Stopwatch timer = Stopwatch.StartNew();
            while (true) {
                bool mainDone = WaitForSingleObject(pi.hProcess, 100) == WAIT_OBJECT_0;
                long outLength = stdout.Length, errorLength = stderr.Length;
                if (outLength > maximumLogBytes || errorLength > maximumLogBytes || outLength > maximumLogBytes - errorLength)
                    result.Failure = "Contained process exceeded the output limit.";
                else if (monitoredOutputPath != null && File.Exists(monitoredOutputPath) &&
                    new FileInfo(monitoredOutputPath).Length > maximumMonitoredOutputBytes)
                    result.Failure = "Contained process output file exceeded the allowed size.";
                else if (timer.ElapsedMilliseconds > timeoutMilliseconds)
                    result.Failure = "Contained process timed out.";
                if (result.Failure != null) { TerminateJobObject(job, 0xEE03); break; }
                if (mainDone) {
                    if (ActiveProcesses(job) == 0) break;
                    if (timer.ElapsedMilliseconds > timeoutMilliseconds) { result.Failure = "Contained process left active child processes."; TerminateJobObject(job, 0xEE04); break; }
                }
            }
            result.SafeToCleanup = WaitForEmpty(job, pi.hProcess, 5000);
            uint exitCode;
            if (WaitForSingleObject(pi.hProcess, 0) == WAIT_OBJECT_0 && GetExitCodeProcess(pi.hProcess, out exitCode)) result.ExitCode = unchecked((int)exitCode);
            if (result.Failure == null && result.ExitCode != 0)
                result.Failure = "Contained reconstruction process failed; configured job resource limits remained enforced.";
            return result;
        } catch (Exception error) {
            if (assigned && job != IntPtr.Zero) TerminateJobObject(job, 0xEE05);
            else if (pi.hProcess != IntPtr.Zero) TerminateProcess(pi.hProcess, 0xEE06);
            try { if (job != IntPtr.Zero) result.SafeToCleanup = WaitForEmpty(job, pi.hProcess, 5000); }
            catch { result.SafeToCleanup = false; }
            result.Failure = "Contained process setup failed: " + error.Message;
            return result;
        } finally {
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
            if (job != IntPtr.Zero) CloseHandle(job);
            if (stdout != null) { SetHandleInformation(stdout.SafeFileHandle.DangerousGetHandle(), HANDLE_FLAG_INHERIT, 0); stdout.Dispose(); }
            if (stderr != null) { SetHandleInformation(stderr.SafeFileHandle.DangerousGetHandle(), HANDLE_FLAG_INHERIT, 0); stderr.Dispose(); }
        }
    }
}
'@
}

function Invoke-ContainedProcess(
    [string]$ExecutablePath, [string[]]$Arguments, [string]$WorkingDirectory,
    [string]$StandardOutputPath, [string]$StandardErrorPath,
    [int]$TimeoutMilliseconds, [long]$MaximumLogBytes,
    [string]$MonitoredOutputPath, [long]$MaximumMonitoredOutputBytes,
    [long]$JobMemoryLimitBytes, [uint32]$ActiveProcessLimit) {
    Initialize-OtaJobRunner
    return [OtaJobRunner]::Run($ExecutablePath, $Arguments, $WorkingDirectory, $StandardOutputPath,
        $StandardErrorPath, $TimeoutMilliseconds, $MaximumLogBytes, $MonitoredOutputPath, $MaximumMonitoredOutputBytes,
        $JobMemoryLimitBytes, $ActiveProcessLimit)
}

function Assert-DeltaReconstructsFull(
    [string]$BasePackagePath,
    [string]$DeltaPackagePath,
    [string]$CurrentFullPackagePath,
    [string]$ToolManifestPath) {
    $resolvedManifest = [IO.Path]::GetFullPath($ToolManifestPath)
    if (-not [IO.File]::Exists($resolvedManifest) -or [IO.Path]::GetFileName($resolvedManifest) -cne 'dotnet-tools.json' -or
        [IO.Path]::GetFileName([IO.Path]::GetDirectoryName($resolvedManifest)) -cne '.config') {
        throw 'Locked vpk tool manifest is unavailable.'
    }
    try { $toolManifest = [IO.File]::ReadAllText($resolvedManifest, [Text.UTF8Encoding]::new($false, $true)) | ConvertFrom-Json }
    catch { throw 'Locked vpk tool manifest is invalid.' }
    $toolProperties = @($toolManifest.tools.PSObject.Properties)
    if ($toolProperties.Count -ne 1 -or $toolProperties[0].Name -cne 'vpk' -or
        [string]$toolManifest.tools.vpk.version -cne '1.2.0' -or
        @($toolManifest.tools.vpk.commands).Count -ne 1 -or [string]$toolManifest.tools.vpk.commands[0] -cne 'vpk') {
        throw 'Locked vpk tool manifest must pin exactly vpk 1.2.0.'
    }
    $repoRoot = [IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($resolvedManifest))
    $dotnetCommands = @(Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue)
    if ($dotnetCommands.Count -lt 1) { throw 'dotnet is unavailable for locked vpk Delta reconstruction.' }
    $dotnetPath = [IO.Path]::GetFullPath([string]$dotnetCommands[0].Source)
    $dotnetInfo = [IO.FileInfo]::new($dotnetPath)
    if (-not $dotnetInfo.Exists -or ($dotnetInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'dotnet executable path is unsafe for locked vpk Delta reconstruction.'
    }

    $reconstructionRoot = Join-Path ([IO.Path]::GetTempPath()) ("superpan-delta-reconstruct-" + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($reconstructionRoot) | Out-Null
    $safeToDelete = $true
    $primaryError = $null
    $cleanupError = $null
    try {
        $baseCopy = Join-Path $reconstructionRoot 'base.nupkg'
        $deltaCopy = Join-Path $reconstructionRoot 'delta.nupkg'
        $currentCopy = Join-Path $reconstructionRoot 'current.nupkg'
        $reconstructed = Join-Path $reconstructionRoot 'reconstructed.nupkg'
        Copy-FileBounded $BasePackagePath $baseCopy 2GB 'Delta base Full package'
        Copy-FileBounded $DeltaPackagePath $deltaCopy 2GB 'Current Delta package'
        Copy-FileBounded $CurrentFullPackagePath $currentCopy 2GB 'Current Full package'

        # dotnet tool run resolves the already-restored local tool from WorkingDirectory's
        # .config manifest; it has no restore operation and performs no implicit download.
        $arguments = @('tool','run','vpk','--','delta','patch',
            '--base',$baseCopy,'--patch',$deltaCopy,'--output',$reconstructed)
        $stdoutPath = Join-Path $reconstructionRoot 'vpk.stdout.log'
        $stderrPath = Join-Path $reconstructionRoot 'vpk.stderr.log'
        $execution = Invoke-ContainedProcess $dotnetPath $arguments $repoRoot $stdoutPath $stderrPath `
            45000 1048576 $reconstructed 2GB 1GB 8
        $safeToDelete = $execution.SafeToCleanup
        if (-not $safeToDelete -and [string]::IsNullOrEmpty([string]$execution.Failure)) {
            throw "Locked vpk process containment did not become empty. Temporary files retained at: $reconstructionRoot"
        }
        if (-not [string]::IsNullOrEmpty([string]$execution.Failure)) {
            $diagnostic = ''
            if ([IO.File]::Exists($stderrPath) -and ([IO.FileInfo]::new($stderrPath)).Length -le 1048576) {
                $diagnosticBytes = Read-LimitedStream ([IO.File]::OpenRead($stderrPath)) 1048576 'Locked vpk error log'
                $diagnostic = ([Text.UTF8Encoding]::new($false, $true)).GetString($diagnosticBytes).Trim()
                if ($diagnostic.Length -gt 2048) { $diagnostic = $diagnostic.Substring(0, 2048) }
            }
            $retentionNote = if ($safeToDelete) { '' } else { " Temporary files retained at: $reconstructionRoot" }
            throw "Locked vpk could not apply the current Delta package. $($execution.Failure) $diagnostic$retentionNote"
        }
        $reconstructedInfo = [IO.FileInfo]::new($reconstructed)
        $currentInfo = [IO.FileInfo]::new($currentCopy)
        if (-not $reconstructedInfo.Exists -or $reconstructedInfo.Length -lt 1 -or $reconstructedInfo.Length -gt 2GB -or
            $reconstructedInfo.Length -ne $currentInfo.Length -or (Get-FileSha256 $reconstructed) -cne (Get-FileSha256 $currentCopy)) {
            throw 'Delta reconstruction differs from the current Full package.'
        }
    }
    catch { $primaryError = $_.Exception }
    finally {
        if ($safeToDelete -and (Test-Path -LiteralPath $reconstructionRoot)) {
            for ($cleanupAttempt = 0; $cleanupAttempt -lt 8; $cleanupAttempt++) {
                try { Remove-Item -LiteralPath $reconstructionRoot -Recurse -Force -ErrorAction Stop; break }
                catch {
                    if ($cleanupAttempt -eq 7) { $cleanupError = $_.Exception; break }
                    Start-Sleep -Milliseconds (50 * ($cleanupAttempt + 1))
                }
            }
        }
    }
    if ($null -ne $primaryError) {
        if ($null -ne $cleanupError) {
            throw "$($primaryError.Message) Delta reconstruction cleanup also failed; temporary files retained at: $reconstructionRoot ($($cleanupError.Message))"
        }
        throw $primaryError
    }
    if ($null -ne $cleanupError) {
        throw "Delta reconstruction cleanup failed; temporary files retained at: $reconstructionRoot ($($cleanupError.Message))"
    }
}

function Get-AnyCertificateSpki([Security.Cryptography.X509Certificates.X509Certificate2]$Certificate) {
    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($Certificate)
    if ($null -eq $rsa) { throw 'Executable signer certificate must use RSA.' }
    try { return Get-RsaCertificateSpki $Certificate }
    finally { $rsa.Dispose() }
}

function Get-RsaCertificateSpki([Security.Cryptography.X509Certificates.X509Certificate2]$Certificate) {
    if ($Certificate.PublicKey.Oid.Value -cne '1.2.840.113549.1.1.1') { throw 'Executable signer certificate must use RSA.' }
    $algorithmIdentifier = [byte[]](0x30,0x0d,0x06,0x09,0x2a,0x86,0x48,0x86,0xf7,0x0d,0x01,0x01,0x01,0x05,0x00)
    $bitString = New-DerValue 0x03 ([byte[]](@(0) + $Certificate.PublicKey.EncodedKeyValue.RawData))
    return Get-BytesSha256 (New-DerValue 0x30 ([byte[]]($algorithmIdentifier + $bitString)))
}

function Add-AuthenticodeHashRange(
    [Security.Cryptography.HashAlgorithm]$Hash,
    [IO.FileStream]$Stream,
    [long]$Offset,
    [long]$Count) {
    if ($Count -lt 0 -or $Offset -lt 0 -or $Offset -gt $Stream.Length - $Count) { throw 'Authenticode image range is invalid.' }
    $Stream.Position = $Offset
    $buffer = [byte[]]::new(1048576)
    while ($Count -gt 0) {
        $read = $Stream.Read($buffer, 0, [int][Math]::Min($buffer.Length, $Count))
        if ($read -le 0) { throw 'Authenticode image ended unexpectedly.' }
        [void]$Hash.TransformBlock($buffer, 0, $read, $buffer, 0)
        $Count -= $read
    }
}

function Get-AuthenticodeTimestampUtc([Security.Cryptography.Pkcs.SignerInfo]$SignerInfo) {
    $accepted = [Collections.Generic.List[object]]::new()
    foreach ($counterSigner in $SignerInfo.CounterSignerInfos) {
        $counterSigner.CheckSignature($true)
        $signingTimes = [Collections.Generic.List[DateTime]]::new()
        foreach ($attribute in $counterSigner.SignedAttributes) {
            if ($attribute.Oid.Value -ceq '1.2.840.113549.1.9.5' -and $attribute.Values.Count -eq 1) {
                $signingTimes.Add(([Security.Cryptography.Pkcs.Pkcs9SigningTime]::new($attribute.Values[0].RawData)).SigningTime.ToUniversalTime())
            }
        }
        if ($signingTimes.Count -ne 1 -or $null -eq $counterSigner.Certificate) {
            throw 'Authenticode countersignature timestamp is ambiguous.'
        }
        $accepted.Add([pscustomobject]@{ Utc=$signingTimes[0]; Certificate=$counterSigner.Certificate; Kind='legacy' })
    }
    foreach ($attribute in $SignerInfo.UnsignedAttributes) {
        if ($attribute.Oid.Value -cne '1.3.6.1.4.1.311.3.3.1') { continue }
        foreach ($value in $attribute.Values) {
            $timestampCms = [Security.Cryptography.Pkcs.SignedCms]::new()
            $timestampCms.Decode($value.RawData)
            $timestampCms.CheckSignature($true)
            if ($timestampCms.ContentInfo.ContentType.Value -cne '1.2.840.113549.1.9.16.1.4' -or
                $timestampCms.SignerInfos.Count -ne 1 -or $null -eq $timestampCms.SignerInfos[0].Certificate) {
                throw 'Authenticode RFC3161 timestamp token is ambiguous or has the wrong content type.'
            }
            $timestampOffset = 0
            $sequence = Read-DerValue $timestampCms.ContentInfo.Content ([ref]$timestampOffset) 0x30 'RFC3161 timestamp'
            Assert-NoDerRemainder $timestampCms.ContentInfo.Content $timestampOffset 'RFC3161 timestamp'
            $sequenceOffset = 0
            [void](Read-DerValue $sequence ([ref]$sequenceOffset) 0x02 'RFC3161 version')
            [void](Read-DerValue $sequence ([ref]$sequenceOffset) 0x06 'RFC3161 policy')
            $messageImprint = Read-DerValue $sequence ([ref]$sequenceOffset) 0x30 'RFC3161 message imprint'
            $imprintOffset = 0
            $imprintAlgorithm = Read-DerValue $messageImprint ([ref]$imprintOffset) 0x30 'RFC3161 imprint algorithm'
            $algorithmOffset = 0
            $imprintOid = Read-DerValue $imprintAlgorithm ([ref]$algorithmOffset) 0x06 'RFC3161 imprint algorithm OID'
            if ($algorithmOffset -lt $imprintAlgorithm.Length) { [void](Read-DerValue $imprintAlgorithm ([ref]$algorithmOffset) 0x05 'RFC3161 algorithm parameters') }
            Assert-NoDerRemainder $imprintAlgorithm $algorithmOffset 'RFC3161 imprint algorithm'
            $expectedImprint = Read-DerValue $messageImprint ([ref]$imprintOffset) 0x04 'RFC3161 message imprint'
            Assert-NoDerRemainder $messageImprint $imprintOffset 'RFC3161 message imprint'
            if ((ConvertTo-LowerHex $imprintOid) -cne '608648016503040201' -or $expectedImprint.Length -ne 32) {
                throw 'Authenticode timestamp uses an unsupported message imprint.'
            }
            $imprintHash = [Security.Cryptography.SHA256]::Create()
            try { $actualImprint = $imprintHash.ComputeHash($SignerInfo.GetSignature()) }
            finally { $imprintHash.Dispose() }
            if (-not (Test-FixedBytesEqual $actualImprint $expectedImprint)) {
                throw 'Authenticode timestamp is not bound to the executable signature.'
            }
            [void](Read-DerValue $sequence ([ref]$sequenceOffset) 0x02 'RFC3161 serial number')
            $timestampBytes = Read-DerValue $sequence ([ref]$sequenceOffset) 0x18 'RFC3161 time'
            $timestampText = [Text.Encoding]::ASCII.GetString($timestampBytes)
            if ($timestampText -cnotmatch '^\d{14}(?:\.\d{1,7})?Z$') { throw 'Authenticode RFC3161 timestamp time is invalid.' }
            $fractionDigits = if ($timestampText.Contains('.')) { $timestampText.Length - 16 } else { 0 }
            $timestampFormat = if ($fractionDigits -eq 0) {
                "yyyyMMddHHmmss'Z'"
            } else {
                "yyyyMMddHHmmss." + ''.PadLeft($fractionDigits, 'f') + "'Z'"
            }
            $timestampUtc = [DateTime]::ParseExact($timestampText, $timestampFormat, [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal)
            $accepted.Add([pscustomobject]@{ Utc=$timestampUtc; Certificate=$timestampCms.SignerInfos[0].Certificate; Kind='rfc3161' })
        }
    }
    if ($accepted.Count -gt 1) { throw 'Authenticode must contain exactly one accepted timestamp token.' }
    if ($accepted.Count -eq 0) { return $null }
    return $accepted[0]
}

function Get-VerifiedAuthenticode([string]$Path) {
    $envelope = Get-PeEnvelope $Path
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $reader = [IO.BinaryReader]::new($stream, [Text.Encoding]::ASCII, $true)
    try {
        $stream.Position = $envelope.CertificateOffset + 8
        $pkcs7Bytes = $reader.ReadBytes([int]($envelope.CertificateLength - 8))
        if ($pkcs7Bytes.Length -ne $envelope.CertificateLength - 8) { throw 'Executable Authenticode certificate entry is truncated.' }

        $cms = [Security.Cryptography.Pkcs.SignedCms]::new()
        $cms.Decode($pkcs7Bytes)
        $cms.CheckSignature($true)
        if ($cms.ContentInfo.ContentType.Value -cne '1.3.6.1.4.1.311.2.1.4') {
            throw 'Executable CMS content is not SPC_INDIRECT_DATA_OBJID Authenticode content.'
        }
        if ($cms.SignerInfos.Count -ne 1 -or $null -eq $cms.SignerInfos[0].Certificate) { throw 'Executable Authenticode signer is ambiguous.' }
        if ($cms.SignerInfos[0].DigestAlgorithm.Value -cne '2.16.840.1.101.3.4.2.1') {
            throw 'Executable Authenticode SignerInfo digest algorithm must be SHA-256.'
        }

        $expectedDigest = Get-AuthenticodeDigest $cms.ContentInfo.Content
        $hash = [Security.Cryptography.SHA256]::Create()
        try {
            Add-AuthenticodeHashRange $hash $stream 0 $envelope.ChecksumOffset
            Add-AuthenticodeHashRange $hash $stream ($envelope.ChecksumOffset + 4) ($envelope.SecurityDirectoryOffset - ($envelope.ChecksumOffset + 4))
            Add-AuthenticodeHashRange $hash $stream ($envelope.SecurityDirectoryOffset + 8) ($envelope.CertificateOffset - ($envelope.SecurityDirectoryOffset + 8))
            if ($envelope.CertificateOffset + $envelope.CertificateSize -lt $stream.Length) {
                Add-AuthenticodeHashRange $hash $stream ($envelope.CertificateOffset + $envelope.CertificateSize) ($stream.Length - ($envelope.CertificateOffset + $envelope.CertificateSize))
            }
            [void]$hash.TransformFinalBlock([byte[]]::new(0), 0, 0)
            $actualDigest = $hash.Hash
        }
        finally { $hash.Dispose() }
        if (-not (Test-FixedBytesEqual $actualDigest $expectedDigest)) {
            throw 'Executable Authenticode image digest does not match the signed digest.'
        }
        return [pscustomobject]@{
            Cms=$cms
            Signer=$cms.SignerInfos[0]
            Machine=$envelope.Machine
            ImageDigest=(ConvertTo-LowerHex $actualDigest)
        }
    }
    finally { $reader.Dispose(); $stream.Dispose() }
}

function Assert-AuthenticatedExecutableIdentity(
    [string]$Path,
    [string]$ArtifactRole,
    [string]$ExpectedMainInternalName,
    [string]$ExpectedLegacyInternalName,
    [string]$ExpectedProductName,
    [string]$ExpectedDescription) {
    if ($ArtifactRole -notin @('main-application','historical-main-application','legacy-compatibility-launcher')) { return }
    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    $expectedInternalName = if ($ArtifactRole -ceq 'main-application') { $ExpectedMainInternalName } else { $ExpectedLegacyInternalName }
    if ($versionInfo.OriginalFilename -cne $expectedInternalName -or $versionInfo.InternalName -cne $expectedInternalName) {
        $roleDescription = if ($ArtifactRole -ceq 'main-application') { 'main application' } elseif ($ArtifactRole -ceq 'legacy-compatibility-launcher') { 'compatibility launcher' } else { 'historical main application' }
        throw "Executable $roleDescription identity does not match the authenticated product contract."
    }
    if ($ArtifactRole -ne 'historical-main-application' -and
        ($versionInfo.ProductName -cne $ExpectedProductName -or $versionInfo.FileDescription -cne $ExpectedDescription)) {
        throw 'Executable product branding does not match the authenticated product contract.'
    }
}

function Assert-DistinctCurrentApplicationImages([object[]]$RoleImages) {
    $mainImages = @($RoleImages | Where-Object { [string]$_.ArtifactRole -ceq 'main-application' })
    if ($mainImages.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$mainImages[0].ImageDigest)) {
        throw 'Current package must contain exactly one authenticated main application image.'
    }
    $mainDigest = [string]$mainImages[0].ImageDigest
    foreach ($roleImage in $RoleImages) {
        if ([string]$roleImage.ArtifactRole -cne 'main-application' -and
            [string]$roleImage.ImageDigest -ceq $mainDigest) {
            throw 'Current main application shares its authenticated image with another executable role.'
        }
    }
}

function Assert-Authenticode(
    [string]$Path,
    [string]$ExpectedSpki,
    [bool]$TimestampRequired,
    [string]$EvidencePath,
    [int]$ExpectedMachine = 0,
    [ValidateSet('main-application','historical-main-application','legacy-compatibility-launcher','velopack-bootstrapper')]
    [string]$ArtifactRole = 'velopack-bootstrapper') {
    try { $verified = Get-VerifiedAuthenticode $Path }
    catch { throw "Executable is not validly Authenticode signed: $([IO.Path]::GetFileName($Path)) ($($_.Exception.Message))" }
    $actualSpki = Get-CertificateSpki $verified.Signer.Certificate
    if ($actualSpki -cne $ExpectedSpki) {
        throw "Executable signer SPKI does not match the enrolled publisher: $([IO.Path]::GetFileName($Path))"
    }
    if ($ExpectedMachine -ne 0 -and $verified.Machine -ne $ExpectedMachine) {
        throw "Executable PE architecture must be x64 (0x8664): $EvidencePath"
    }
    $codeSigningOid = '1.3.6.1.5.5.7.3.3'
    $hasCodeSigningEku = @($verified.Signer.Certificate.Extensions | Where-Object {
            $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] -and
            @($_.EnhancedKeyUsages | Where-Object { $_.Value -ceq $codeSigningOid }).Count -eq 1
        }).Count -eq 1
    if (-not $hasCodeSigningEku) {
        throw "Executable signer certificate is missing the Code Signing EKU: $EvidencePath"
    }
    Assert-AuthenticatedExecutableIdentity $Path $ArtifactRole "$productMainAssemblyName.dll" `
        "$productLegacyAssemblyName.dll" $productDisplayName $productDescription
    $authenticodeStatus = Get-AuthenticodeSignature -LiteralPath $Path
    $timestampEvidence = Get-AuthenticodeTimestampUtc $verified.Signer
    $timestampUtc = if ($null -eq $timestampEvidence) { $null } else { [DateTime]$timestampEvidence.Utc }
    if ($TimestampRequired -and $null -eq $timestampEvidence) {
        throw "Executable is missing the required Authenticode timestamp: $([IO.Path]::GetFileName($Path))"
    }
    $validationTime = if ($null -eq $timestampUtc) { [DateTime]::UtcNow } else { $timestampUtc }
    if ($validationTime -lt $verified.Signer.Certificate.NotBefore.ToUniversalTime() -or
        $validationTime -gt $verified.Signer.Certificate.NotAfter.ToUniversalTime()) {
        throw "Executable publisher certificate was not valid at its effective signing time: $EvidencePath"
    }
    if ($null -ne $timestampUtc) {
        if ($timestampUtc -gt [DateTime]::UtcNow.AddMinutes(5)) { throw "Executable timestamp is unreasonably in the future: $EvidencePath" }
        $tsaCertificate = $timestampEvidence.Certificate
        if ($null -ne $authenticodeStatus.TimeStamperCertificate -and
            $authenticodeStatus.TimeStamperCertificate.Thumbprint -cne $tsaCertificate.Thumbprint) {
            throw "Executable platform timestamp signer differs from the accepted timestamp token: $EvidencePath"
        }
        $timestampingOid = '1.3.6.1.5.5.7.3.8'
        $hasTimestampingEku = @($tsaCertificate.Extensions | Where-Object {
                $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] -and
                @($_.EnhancedKeyUsages | Where-Object { $_.Value -ceq $timestampingOid }).Count -eq 1
            }).Count -eq 1
        if (-not $hasTimestampingEku -or $timestampUtc -lt $tsaCertificate.NotBefore.ToUniversalTime() -or
            $timestampUtc -gt $tsaCertificate.NotAfter.ToUniversalTime()) { throw "Executable timestamp authority is invalid: $EvidencePath" }
        $tsaChain = [Security.Cryptography.X509Certificates.X509Chain]::new()
        try {
            $tsaChain.ChainPolicy.RevocationMode = [Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
            $tsaChain.ChainPolicy.VerificationTime = $timestampUtc
            $tsaChain.ChainPolicy.VerificationFlags = [Security.Cryptography.X509Certificates.X509VerificationFlags]::NoFlag
            if (-not $tsaChain.Build($tsaCertificate)) { throw "Executable timestamp authority chain is not trusted: $EvidencePath" }
        }
        finally { $tsaChain.Dispose() }
    }
    $signatureEvidence.Add([ordered]@{
        file = $EvidencePath
        policy = 'publisher-authenticode-spki'
        artifactRole = $ArtifactRole
        signerSpkiSha256 = $actualSpki
        peMachine = ('0x{0:x4}' -f $verified.Machine)
        imageDigestVerified = $true
        platformStatus = [string]$authenticodeStatus.Status
        timestampPresent = ($null -ne $timestampUtc)
        timestampUtc = if ($null -eq $timestampUtc) { $null } else { $timestampUtc.ToString('O') }
    })
    $result = [pscustomobject]@{ ArtifactRole=$ArtifactRole; ImageDigest=[string]$verified.ImageDigest }
    if ($null -ne $authenticodeStatus.TimeStamperCertificate) { $authenticodeStatus.TimeStamperCertificate.Dispose() }
    if ($null -ne $authenticodeStatus.SignerCertificate) { $authenticodeStatus.SignerCertificate.Dispose() }
    if ($null -ne $timestampEvidence -and $null -ne $timestampEvidence.Certificate) { $timestampEvidence.Certificate.Dispose() }
    if ($null -ne $verified.Signer.Certificate) { $verified.Signer.Certificate.Dispose() }
    $authenticodeStatus = $null
    $verified = $null
    return $result
}

function Get-PeEnvelope([string]$Path) {
    $fileLength = ([IO.FileInfo]::new($Path)).Length
    if ($fileLength -gt 536870912) { throw 'Executable exceeds the allowed size.' }
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $reader = [IO.BinaryReader]::new($stream, [Text.Encoding]::ASCII, $true)
    try {
        if ($fileLength -lt 256 -or $reader.ReadUInt16() -ne 0x5A4D) { throw 'Executable is not a PE image.' }
        $stream.Position = 0x3c
        $peOffset = [long]$reader.ReadUInt32()
        if ($peOffset -lt 0x40 -or $peOffset -gt $fileLength - 256) { throw 'Executable PE header is invalid.' }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) { throw 'Executable PE signature is invalid.' }
        $machine = $reader.ReadUInt16()
        $optionalOffset = $peOffset + 24
        $stream.Position = $optionalOffset
        $magic = $reader.ReadUInt16()
        $dataDirectoryOffset = if ($magic -eq 0x20b) { $optionalOffset + 112 } elseif ($magic -eq 0x10b) { $optionalOffset + 96 } else { throw 'Executable optional header is invalid.' }
        $checksumOffset = $optionalOffset + 64
        $securityDirectoryOffset = $dataDirectoryOffset + 32
        if ($securityDirectoryOffset -gt $fileLength - 8) { throw 'Executable certificate table is outside the PE image.' }
        $stream.Position = $securityDirectoryOffset
        $certificateOffset = [long]$reader.ReadUInt32()
        $certificateSize = [long]$reader.ReadUInt32()
        if ($certificateSize -gt 16777216) { throw 'Executable certificate table exceeds the allowed size.' }
        if ($certificateOffset -le $securityDirectoryOffset + 8 -or $certificateSize -lt 8 -or
            $certificateOffset -gt $fileLength - $certificateSize) { throw 'Executable has no valid Authenticode certificate table.' }
        $stream.Position = $certificateOffset
        $certificateLength = [long]$reader.ReadUInt32()
        $revision = $reader.ReadUInt16()
        $certificateType = $reader.ReadUInt16()
        if ($certificateLength -gt 16777216) { throw 'Executable Authenticode certificate blob exceeds the allowed size.' }
        if ($certificateLength -lt 9 -or $certificateLength -gt $certificateSize -or
            $certificateLength -gt [int]::MaxValue -or $revision -notin @(0x0100,0x0200) -or $certificateType -ne 2) {
            throw 'Executable Authenticode certificate entry is invalid.'
        }
        return [pscustomobject]@{
            Machine=$machine; ChecksumOffset=$checksumOffset; SecurityDirectoryOffset=$securityDirectoryOffset
            CertificateOffset=$certificateOffset; CertificateSize=$certificateSize; CertificateLength=$certificateLength
        }
    }
    finally { $reader.Dispose(); $stream.Dispose() }
}

function Test-ByteSequence([byte[]]$Bytes, [byte[]]$Needle) {
    if ($Needle.Length -eq 0 -or $Bytes.Length -lt $Needle.Length) { return $false }
    for ($offset = 0; $offset -le $Bytes.Length - $Needle.Length; $offset++) {
        $matches = $true
        for ($index = 0; $index -lt $Needle.Length; $index++) {
            if ($Bytes[$offset + $index] -ne $Needle[$index]) { $matches = $false; break }
        }
        if ($matches) { return $true }
    }
    return $false
}

function Assert-FileContainsText([string]$Path, [string]$Value, [string]$ErrorMessage, [long]$MaximumLength = 67108864) {
    $fileLength = ([IO.FileInfo]::new($Path)).Length
    if ($fileLength -gt $MaximumLength) { throw 'Launcher exceeds the allowed size.' }
    $needles = @([Text.Encoding]::UTF8.GetBytes($Value), [Text.Encoding]::Unicode.GetBytes($Value))
    $overlapLength = (@($needles | ForEach-Object { $_.Length }) | Measure-Object -Maximum).Maximum - 1
    $carry = [byte[]]::new(0)
    $buffer = [byte[]]::new(65536)
    $stream = [IO.File]::OpenRead($Path)
    try {
        while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $window = [byte[]]::new($carry.Length + $read)
            if ($carry.Length -gt 0) { [Array]::Copy($carry, 0, $window, 0, $carry.Length) }
            [Array]::Copy($buffer, 0, $window, $carry.Length, $read)
            foreach ($needle in $needles) { if (Test-ByteSequence $window $needle) { return } }
            $keep = [Math]::Min($overlapLength, $window.Length)
            $carry = [byte[]]::new($keep)
            if ($keep -gt 0) { [Array]::Copy($window, $window.Length - $keep, $carry, 0, $keep) }
        }
    }
    finally { $stream.Dispose() }
    throw $ErrorMessage
}

function Assert-DeltaPatchContract(
    [object[]]$Patches,
    [Collections.IDictionary]$CurrentFullTargets,
    [string]$DeltaVersion,
    [string]$CurrentVersion,
    [string]$RequiredLegacyBasePath) {
    if ($DeltaVersion -cne $CurrentVersion) { throw 'Historical Delta packages are not allowed in a production upload bundle.' }
    $seenBases = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $hasLegacyPatch = $false
    foreach ($patch in $Patches) {
        $basePath = [string]$patch.BasePath
        if (-not $seenBases.Add($basePath)) { throw "Delta target has both .diff and .zsdiff payloads: $basePath" }
        if ([long]$patch.Length -lt 1) { throw "Delta package patch payload is empty: $basePath$($patch.Extension)" }
        if ([long]$patch.Length -gt 536870912) { throw "Delta package patch payload exceeds the allowed size: $basePath$($patch.Extension)" }
        if (-not $CurrentFullTargets.ContainsKey($basePath)) { throw "Delta package patch has no matching target in the current Full package: $basePath" }
        $target = $CurrentFullTargets[$basePath]
        $expectedShasum = "$(([string]$target.Sha1).ToUpperInvariant()) $([IO.Path]::GetFileName($basePath)).shasum $([long]$target.Length)"
        if ([string]$patch.Shasum -cne $expectedShasum) {
            throw "Delta package shasum does not identify the current Full target: $basePath"
        }
        if ($basePath -ceq $RequiredLegacyBasePath) { $hasLegacyPatch = $true }
    }
    if (-not $hasLegacyPatch) { throw 'Delta package does not replace the legacy executable with the compatibility launcher.' }
}

function Assert-ThirdPartyAuthenticode([string]$Path, [string]$ExpectedSpki, [string]$EvidencePath, [string]$ExpectedHash) {
    try { $verified = Get-VerifiedAuthenticode $Path }
    catch { throw "Third-party executable is not validly Authenticode signed: $EvidencePath ($($_.Exception.Message))" }
    $actualSpki = Get-AnyCertificateSpki $verified.Signer.Certificate
    if ($actualSpki -cne $ExpectedSpki) { throw "Third-party executable signer SPKI is not trusted: $EvidencePath" }
    $actualHash = Get-FileSha256 $Path
    if ($actualHash -cne $ExpectedHash) { throw "Third-party executable hash differs from its authenticated helper manifest: $EvidencePath" }
    $codeSigningOid = '1.3.6.1.5.5.7.3.3'
    $hasCodeSigningEku = @($verified.Signer.Certificate.Extensions | Where-Object {
            $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] -and
            @($_.EnhancedKeyUsages | Where-Object { $_.Value -ceq $codeSigningOid }).Count -eq 1
        }).Count -eq 1
    if (-not $hasCodeSigningEku) { throw "Third-party executable signer certificate is missing the Code Signing EKU: $EvidencePath" }
    $authenticodeStatus = Get-AuthenticodeSignature -LiteralPath $Path
    $timestampEvidence = Get-AuthenticodeTimestampUtc $verified.Signer
    $timestampUtc = if ($null -eq $timestampEvidence) { $null } else { [DateTime]$timestampEvidence.Utc }
    $validationTime = if ($null -eq $timestampUtc) { [DateTime]::UtcNow } else { $timestampUtc }
    if ($validationTime -lt $verified.Signer.Certificate.NotBefore.ToUniversalTime() -or
        $validationTime -gt $verified.Signer.Certificate.NotAfter.ToUniversalTime()) {
        throw "Third-party executable signer certificate was not valid at its effective signing time: $EvidencePath"
    }
    if ($null -ne $timestampEvidence) {
        if ($timestampUtc -gt [DateTime]::UtcNow.AddMinutes(5)) { throw "Third-party executable timestamp is unreasonably in the future: $EvidencePath" }
        $tsaCertificate = $timestampEvidence.Certificate
        if ($null -ne $authenticodeStatus.TimeStamperCertificate -and
            $authenticodeStatus.TimeStamperCertificate.Thumbprint -cne $tsaCertificate.Thumbprint) {
            throw "Third-party executable platform timestamp signer differs from the accepted timestamp token: $EvidencePath"
        }
        $timestampingOid = '1.3.6.1.5.5.7.3.8'
        $hasTimestampingEku = @($tsaCertificate.Extensions | Where-Object {
                $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] -and
                @($_.EnhancedKeyUsages | Where-Object { $_.Value -ceq $timestampingOid }).Count -eq 1
            }).Count -eq 1
        if (-not $hasTimestampingEku -or $timestampUtc -lt $tsaCertificate.NotBefore.ToUniversalTime() -or
            $timestampUtc -gt $tsaCertificate.NotAfter.ToUniversalTime()) { throw "Third-party executable timestamp authority is invalid: $EvidencePath" }
        $tsaChain = [Security.Cryptography.X509Certificates.X509Chain]::new()
        try {
            $tsaChain.ChainPolicy.RevocationMode = [Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
            $tsaChain.ChainPolicy.VerificationTime = $timestampUtc
            $tsaChain.ChainPolicy.VerificationFlags = [Security.Cryptography.X509Certificates.X509VerificationFlags]::NoFlag
            if (-not $tsaChain.Build($tsaCertificate)) { throw "Third-party executable timestamp authority chain is not trusted: $EvidencePath" }
        }
        finally { $tsaChain.Dispose() }
    }
    $signatureEvidence.Add([ordered]@{
        file = $EvidencePath
        policy = 'python-psf-authenticode-and-helper-manifest'
        signerSpkiSha256 = $actualSpki
        sha256 = $actualHash
        imageDigestVerified = $true
        platformStatus = [string]$authenticodeStatus.Status
        timestampPresent = ($null -ne $timestampEvidence)
        timestampUtc = if ($null -eq $timestampUtc) { $null } else { $timestampUtc.ToString('O') }
    })
    if ($null -ne $authenticodeStatus.TimeStamperCertificate) { $authenticodeStatus.TimeStamperCertificate.Dispose() }
    if ($null -ne $authenticodeStatus.SignerCertificate) { $authenticodeStatus.SignerCertificate.Dispose() }
    if ($null -ne $timestampEvidence -and $null -ne $timestampEvidence.Certificate) { $timestampEvidence.Certificate.Dispose() }
    if ($null -ne $verified.Signer.Certificate) { $verified.Signer.Certificate.Dispose() }
    $authenticodeStatus = $null
    $timestampEvidence = $null
    $verified = $null
}

$resolvedBundle = [IO.Path]::GetFullPath($BundlePath)
$resolvedCertificate = [IO.Path]::GetFullPath($PublisherCertificatePath)
if (-not (Test-Path -LiteralPath $resolvedBundle)) { throw 'BundlePath does not exist.' }
if (-not (Test-Path -LiteralPath $resolvedCertificate -PathType Leaf)) { throw 'Publisher certificate does not exist.' }
if ($ExpectedPublisherSpkiSha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Expected publisher SPKI must be lowercase SHA-256.' }
if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion) -and -not (Test-CanonicalVersion $ExpectedVersion)) {
    throw 'ExpectedVersion must be a canonical major.minor.patch value.'
}

$publisherCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($resolvedCertificate)
$archive = $null
$temporaryRoot = $null
try {
    $certificateSpki = Get-CertificateSpki $publisherCertificate
    if ($certificateSpki -cne $ExpectedPublisherSpkiSha256) { throw 'Publisher certificate SPKI pin does not match.' }

    $entries = [Collections.Generic.List[object]]::new()
    if (Test-Path -LiteralPath $resolvedBundle -PathType Container) {
        foreach ($path in [IO.Directory]::EnumerateFileSystemEntries($resolvedBundle, '*', [IO.SearchOption]::TopDirectoryOnly)) {
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Upload bundle contains a directory entry.' }
            if (([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Upload bundle contains a reparse-point file.' }
            $entries.Add([pscustomobject]@{ Name = [IO.Path]::GetFileName($path); Length = ([IO.FileInfo]$path).Length; Path = $path; ZipEntry = $null })
        }
    }
    else {
        if ([IO.Path]::GetExtension($resolvedBundle) -cne '.zip') { throw 'BundlePath must be a directory or ZIP file.' }
        if ([IO.FileInfo]::new($resolvedBundle).Length -gt 2GB) { throw 'Upload ZIP exceeds the 2 GiB compressed limit.' }
        $archive = [IO.Compression.ZipFile]::OpenRead($resolvedBundle)
        foreach ($entry in $archive.Entries) {
            if ([string]::IsNullOrEmpty($entry.Name) -or $entry.FullName -cne $entry.Name) { throw 'Upload ZIP contains a directory or nested entry.' }
            $unixType = (($entry.ExternalAttributes -shr 16) -band 0xF000)
            if ($unixType -eq 0xA000 -or ($entry.ExternalAttributes -band [int][IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Upload ZIP contains a symbolic-link or reparse-point entry.'
            }
            $entries.Add([pscustomobject]@{ Name = $entry.Name; Length = $entry.Length; Path = $null; ZipEntry = $entry })
        }
    }
    if ($entries.Count -lt 7 -or $entries.Count -gt 256) { throw 'Upload bundle has an invalid file count.' }
    $expandedBytes = [long]0
    $compressedBytes = [long]0
    foreach ($entry in $entries) {
        if ($entry.Length -lt 0 -or $entry.Length -gt 2GB) { throw "Upload asset exceeds the 2 GiB per-file limit: $($entry.Name)" }
        if ($entry.Length -gt ([long]::MaxValue - $expandedBytes)) { throw 'Upload bundle expanded size overflowed.' }
        $expandedBytes += [long]$entry.Length
        if ($expandedBytes -gt 8GB) { throw 'Upload bundle exceeds the 8 GiB expanded limit.' }
        if ($null -ne $entry.ZipEntry) {
            $compressed = [long]$entry.ZipEntry.CompressedLength
            if ($compressed -gt ([long]::MaxValue - $compressedBytes)) { throw 'Upload bundle compressed size overflowed.' }
            $compressedBytes += $compressed
            if ($entry.Length -gt 0 -and ($compressed -le 0 -or ([double]$entry.Length / [double]$compressed) -gt 100.0)) {
                throw "Upload asset exceeds the 100:1 compression ratio limit: $($entry.Name)"
            }
        }
    }
    if ($compressedBytes -gt 0 -and ([double]$expandedBytes / [double]$compressedBytes) -gt 100.0) {
        throw 'Upload bundle exceeds the 100:1 aggregate compression ratio limit.'
    }

    $names = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $entries) {
        if (-not (Test-FlatAsciiName $entry.Name)) { throw "Upload bundle contains an unknown or unsafe file: $($entry.Name)" }
        if ($names.ContainsKey($entry.Name)) { throw "Upload bundle contains a duplicate or case-colliding file: $($entry.Name)" }
        if (-not (Test-AllowedName $entry.Name)) { throw "Upload bundle contains an unknown or unsafe file: $($entry.Name)" }
        $names.Add($entry.Name, $entry.Name)
    }
    foreach ($required in @($manifestName, $signatureName, $releaseFeedName, $assetCatalogName, $releasesName,
            "$packageId-Setup.exe", "$packageId-Portable.zip")) {
        if (-not $names.ContainsKey($required) -or $names[$required] -cne $required) { throw "Upload bundle is missing required file: $required" }
    }

    function Read-EntryBytes([string]$Name, [long]$Limit) {
        $entry = @($entries | Where-Object { $_.Name -ceq $Name })[0]
        if ($null -ne $entry.Path) {
            if ($entry.Length -gt $Limit) { throw "$Name exceeds the allowed size." }
            return [IO.File]::ReadAllBytes($entry.Path)
        }
        return Read-LimitedStream $entry.ZipEntry.Open() $Limit $Name
    }

    function Get-EntrySha256([object]$Entry) {
        if ($null -ne $Entry.Path) { return Get-FileSha256 $Entry.Path }
        return Get-LowerHash ([Security.Cryptography.SHA256]::Create()) $Entry.ZipEntry.Open()
    }

    function Extract-Entry([string]$Name, [string]$Destination) {
        $entry = @($entries | Where-Object { $_.Name -ceq $Name })[0]
        if ($null -ne $entry.Path) { [IO.File]::Copy($entry.Path, $Destination, $false); return }
        $input = $entry.ZipEntry.Open()
        $output = [IO.File]::Create($Destination)
        try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
    }

    function Get-WindowsCanonicalInnerPath([string]$Name) {
        $reservedDeviceNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($reservedName in @(
                'CON','PRN','AUX','NUL','CLOCK$',
                'COM1','COM2','COM3','COM4','COM5','COM6','COM7','COM8','COM9','COM¹','COM²','COM³',
                'LPT1','LPT2','LPT3','LPT4','LPT5','LPT6','LPT7','LPT8','LPT9','LPT¹','LPT²','LPT³')) {
            [void]$reservedDeviceNames.Add($reservedName)
        }
        if ([string]::IsNullOrWhiteSpace($Name) -or $Name.Length -gt 1024 -or
            $Name.StartsWith('/') -or $Name.StartsWith('\') -or $Name.Contains('\') -or
            $Name.Contains([char]0) -or $Name -match '^[A-Za-z]:') { return $null }
        $parts = @($Name.Split('/'))
        if ($parts.Count -eq 0) { return $null }
        $canonicalParts = [Collections.Generic.List[string]]::new()
        foreach ($part in $parts) {
            if ($part -in @('', '.', '..') -or $part.Length -gt 255 -or $part.EndsWith(' ') -or $part.EndsWith('.') -or
                $part.Contains(':') -or $part.IndexOfAny([char[]]@('<','>','"','|','?','*')) -ge 0) { return $null }
            foreach ($character in $part.ToCharArray()) { if ([int]$character -lt 0x20 -or [int]$character -eq 0x7f) { return $null } }
            $normalized = $part.Normalize([Text.NormalizationForm]::FormC)
            $stem = $normalized.Split('.')[0].ToUpperInvariant()
            if ($reservedDeviceNames.Contains($stem)) { return $null }
            $canonicalParts.Add($normalized)
        }
        $canonical = $canonicalParts -join '/'
        if ($canonical.Length -gt 240) { return $null }
        return $canonical
    }

    function Assert-InnerArchive(
        [string]$ArchivePath,
        [string]$Description,
        [string[]]$RequiredPublisherExecutables,
        [string[]]$AllowedThirdPartyExecutables = @(),
        [string]$ExpectedPackageVersion,
        [ValidateSet('Full','Delta','Portable')][string]$ExpectedPackageType,
        [string]$ExpectedLegacyLauncherSha1,
        [long]$ExpectedLegacyLauncherLength = -1,
        [Collections.IDictionary]$CurrentFullTargets) {
        $inner = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
        try {
            if ($inner.Entries.Count -eq 0 -or $inner.Entries.Count -gt 4096) { throw "$Description has an invalid entry count." }
            $innerNames = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
            $innerExpanded = [long]0
            $innerCompressed = [long]0
            foreach ($innerEntry in $inner.Entries) {
                $canonicalInnerPath = Get-WindowsCanonicalInnerPath $innerEntry.FullName
                if ([string]::IsNullOrEmpty($innerEntry.Name) -or $null -eq $canonicalInnerPath) { throw "$Description contains an unsafe Windows path: $($innerEntry.FullName)" }
                if ($innerNames.ContainsKey($canonicalInnerPath)) { throw "$Description contains duplicate, case-colliding, or Unicode-normalization-aliased paths." }
                $innerNames.Add($canonicalInnerPath, $innerEntry.FullName)
                $unixType = (($innerEntry.ExternalAttributes -shr 16) -band 0xF000)
                if ($unixType -eq 0xA000 -or ($innerEntry.ExternalAttributes -band [int][IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "$Description contains a symbolic-link or reparse-point entry."
                }
                if ($innerEntry.Length -lt 0 -or $innerEntry.Length -gt 2GB) { throw "$Description contains an oversized entry." }
                if ($innerEntry.Length -gt ([long]::MaxValue - $innerExpanded)) { throw "$Description expanded size overflowed." }
                $innerExpanded += $innerEntry.Length
                if ($innerExpanded -gt 8GB) { throw "$Description exceeds the expanded size limit." }
                $innerCompressed += $innerEntry.CompressedLength
                if ($innerEntry.Length -gt 0 -and ($innerEntry.CompressedLength -le 0 -or
                    ([double]$innerEntry.Length / [double]$innerEntry.CompressedLength) -gt 100.0)) {
                    throw "$Description contains an excessive compression ratio."
                }
            }
            if ($innerCompressed -gt 0 -and ([double]$innerExpanded / [double]$innerCompressed) -gt 100.0) {
                throw "$Description has an excessive aggregate compression ratio."
            }
            $isCurrentPackage = $ExpectedPackageVersion -ceq $currentVersion
            if ($ExpectedPackageType -ceq 'Portable') {
                foreach ($requiredPortablePath in @(
                    '.portable',
                    'current/sq.version',
                    "current/$productMainExecutableName",
                    "current/$productMainDllName",
                    "current/$productMainStem.deps.json",
                    "current/$productMainStem.runtimeconfig.json",
                    "current/$productLegacyExecutableName")) {
                    if (-not $innerNames.ContainsKey($requiredPortablePath) -or $innerNames[$requiredPortablePath] -cne $requiredPortablePath) {
                        throw "Portable archive is missing required Velopack content: $requiredPortablePath"
                    }
                }
                $portableMetadataEntry = $inner.GetEntry('current/sq.version')
                if ($null -eq $portableMetadataEntry -or $portableMetadataEntry.Length -le 0 -or $portableMetadataEntry.Length -gt 1048576) {
                    throw 'Portable archive has invalid Velopack metadata.'
                }
                $portableSettings = [Xml.XmlReaderSettings]::new()
                $portableSettings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
                $portableSettings.XmlResolver = $null
                $portableSettings.MaxCharactersInDocument = 1048576
                $portableStream = $portableMetadataEntry.Open()
                $portableReader = [Xml.XmlReader]::Create($portableStream, $portableSettings)
                try {
                    $portableXml = [Xml.XmlDocument]::new()
                    $portableXml.XmlResolver = $null
                    $portableXml.Load($portableReader)
                }
                catch { throw 'Portable archive Velopack metadata is invalid XML.' }
                finally { $portableReader.Dispose(); $portableStream.Dispose() }
                $portableNamespace = [Xml.XmlNamespaceManager]::new($portableXml.NameTable)
                $portableNamespace.AddNamespace('n', $portableXml.DocumentElement.NamespaceURI)
                $portableMetadata = $portableXml.SelectSingleNode('/n:package/n:metadata', $portableNamespace)
                if ($null -eq $portableMetadata -or
                    [string]$portableMetadata.SelectSingleNode('n:id', $portableNamespace).InnerText -cne $packageId -or
                    [string]$portableMetadata.SelectSingleNode('n:version', $portableNamespace).InnerText -cne $ExpectedPackageVersion -or
                    [string]$portableMetadata.SelectSingleNode('n:channel', $portableNamespace).InnerText -cne $channel -or
                    [string]$portableMetadata.SelectSingleNode('n:title', $portableNamespace).InnerText -cne $productDisplayName -or
                    [string]$portableMetadata.SelectSingleNode('n:description', $portableNamespace).InnerText -cne $productDescription -or
                    [string]$portableMetadata.SelectSingleNode('n:authors', $portableNamespace).InnerText -cne $packageId -or
                    [string]$portableMetadata.SelectSingleNode('n:mainExe', $portableNamespace).InnerText -cne $productMainExecutableName -or
                    [string]$portableMetadata.SelectSingleNode('n:os', $portableNamespace).InnerText -cne 'win' -or
                    [string]$portableMetadata.SelectSingleNode('n:rid', $portableNamespace).InnerText -cne 'win-x64' -or
                    [string]$portableMetadata.SelectSingleNode('n:machineArchitecture', $portableNamespace).InnerText -cne 'x64') {
                    throw 'Portable archive Velopack metadata identity, version, channel, executable, OS, RID, or architecture is invalid.'
                }
            }
            else {
                foreach ($requiredPackagePath in @('[Content_Types].xml','_rels/.rels',"$packageId.nuspec")) {
                    if (-not $innerNames.ContainsKey($requiredPackagePath) -or $innerNames[$requiredPackagePath] -cne $requiredPackagePath) {
                        throw "$Description is missing required Velopack package content: $requiredPackagePath"
                    }
                }
                $nuspecEntry = $inner.GetEntry("$packageId.nuspec")
                if ($null -eq $nuspecEntry -or $nuspecEntry.Length -le 0 -or $nuspecEntry.Length -gt 1048576) {
                    throw "$Description has an invalid Velopack package nuspec."
                }
                $settings = [Xml.XmlReaderSettings]::new()
                $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
                $settings.XmlResolver = $null
                $settings.MaxCharactersInDocument = 1048576
                $nuspecStream = $nuspecEntry.Open(); $xmlReader = [Xml.XmlReader]::Create($nuspecStream, $settings)
                try { $nuspec = [Xml.XmlDocument]::new(); $nuspec.XmlResolver = $null; $nuspec.Load($xmlReader) }
                catch { throw "$Description Velopack package nuspec is invalid XML." }
                finally { $xmlReader.Dispose(); $nuspecStream.Dispose() }
                $namespace = [Xml.XmlNamespaceManager]::new($nuspec.NameTable)
                $namespace.AddNamespace('n', $nuspec.DocumentElement.NamespaceURI)
                $metadata = $nuspec.SelectSingleNode('/n:package/n:metadata', $namespace)
                $expectedPackageMainExe = if ($isCurrentPackage) { $productMainExecutableName } else { $productLegacyExecutableName }
                if ($null -eq $metadata -or
                    [string]$metadata.SelectSingleNode('n:id', $namespace).InnerText -cne $packageId -or
                    [string]$metadata.SelectSingleNode('n:version', $namespace).InnerText -cne $ExpectedPackageVersion -or
                    [string]$metadata.SelectSingleNode('n:channel', $namespace).InnerText -cne $channel -or
                    [string]$metadata.SelectSingleNode('n:mainExe', $namespace).InnerText -cne $expectedPackageMainExe -or
                    [string]$metadata.SelectSingleNode('n:os', $namespace).InnerText -cne 'win' -or
                    [string]$metadata.SelectSingleNode('n:rid', $namespace).InnerText -cne 'win-x64') {
                    throw "$Description Velopack package nuspec identity, branding, version, or type metadata is invalid."
                }
                if ($isCurrentPackage) {
                    if ([string]$metadata.SelectSingleNode('n:title', $namespace).InnerText -cne $productDisplayName -or
                        [string]$metadata.SelectSingleNode('n:description', $namespace).InnerText -cne $productDescription -or
                        [string]$metadata.SelectSingleNode('n:authors', $namespace).InnerText -cne $packageId -or
                        [string]$metadata.SelectSingleNode('n:machineArchitecture', $namespace).InnerText -cne 'x64') {
                        throw "$Description Velopack package nuspec branding or architecture metadata is invalid."
                    }
                    $iconEntry = $inner.GetEntry('setup.ico')
                    if ($null -eq $iconEntry -or $iconEntry.Length -lt 22 -or $iconEntry.Length -gt 16777216) {
                        throw "$Description is missing the canonical package icon: setup.ico"
                    }
                    $iconBytes = Read-LimitedStream $iconEntry.Open() 16777216 "$Description setup.ico"
                    if ($iconBytes.Length -lt 4 -or $iconBytes[0] -ne 0 -or $iconBytes[1] -ne 0 -or
                        $iconBytes[2] -ne 1 -or $iconBytes[3] -ne 0) {
                        throw "$Description canonical package icon is invalid: setup.ico"
                    }
                    if ((Get-BytesSha256 $iconBytes) -cne (Get-FileSha256 $productIconPath)) {
                        throw "$Description canonical package icon hash differs: setup.ico"
                    }
                }
                if ($ExpectedPackageType -ceq 'Full') {
                    $requiredFullPaths = if ($isCurrentPackage) { @(
                            "lib/app/$productMainExecutableName",
                            "lib/app/$productMainDllName",
                            "lib/app/$productMainStem.deps.json",
                            "lib/app/$productMainStem.runtimeconfig.json",
                            "lib/app/$productLegacyExecutableName")
                    } else { @(
                            "lib/app/$productLegacyExecutableName",
                            'lib/app/BaiduBatchTransfer.App.dll',
                            'lib/app/BaiduBatchTransfer.App.deps.json',
                            'lib/app/BaiduBatchTransfer.App.runtimeconfig.json') }
                    foreach ($requiredFullPath in $requiredFullPaths) {
                        if (-not $innerNames.ContainsKey($requiredFullPath) -or $innerNames[$requiredFullPath] -cne $requiredFullPath) {
                            throw "$Description is missing required Full package content: $requiredFullPath"
                        }
                    }
                }
                else {
                    if ($ExpectedPackageVersion -cne $currentVersion) {
                        throw 'Historical Delta packages are not allowed in a production upload bundle.'
                    }
                    $legacyPatchBase = "lib/app/$productLegacyExecutableName"
                    $hasLegacyPatch = $innerNames.ContainsKey("$legacyPatchBase.diff") -or $innerNames.ContainsKey("$legacyPatchBase.zsdiff")
                    $patches = @($inner.Entries | Where-Object {
                        $_.FullName.StartsWith('lib/app/', [StringComparison]::Ordinal) -and
                        ($_.FullName.EndsWith('.diff', [StringComparison]::Ordinal) -or $_.FullName.EndsWith('.zsdiff', [StringComparison]::Ordinal))
                    })
                    if (-not $hasLegacyPatch -or -not $innerNames.ContainsKey("$legacyPatchBase.shasum")) {
                        throw 'Delta package has no meaningful patch payload. Delta package does not replace the legacy executable with the compatibility launcher.'
                    }
                    foreach ($requiredRenamedPath in @(
                            "lib/app/$productMainExecutableName",
                            "lib/app/$productMainDllName",
                            "lib/app/$productMainStem.deps.json",
                            "lib/app/$productMainStem.runtimeconfig.json")) {
                        if (-not $innerNames.ContainsKey($requiredRenamedPath) -or $innerNames[$requiredRenamedPath] -cne $requiredRenamedPath) {
                            throw "Delta package does not add the renamed main application: $requiredRenamedPath"
                        }
                    }
                    if ($patches.Count -eq 0) {
                        throw 'Delta package has no meaningful patch payload.'
                    }
                    $patchContracts = [Collections.Generic.List[object]]::new()
                    foreach ($patch in $patches) {
                        $basePath = if ($patch.FullName.EndsWith('.zsdiff', [StringComparison]::Ordinal)) {
                            $patch.FullName.Substring(0, $patch.FullName.Length - 7)
                        }
                        else { $patch.FullName.Substring(0, $patch.FullName.Length - 5) }
                        if (-not $innerNames.ContainsKey("$basePath.shasum")) {
                            throw "Delta package patch is missing its shasum: $($patch.FullName)"
                        }
                        $patchShasumEntry = $inner.GetEntry("$basePath.shasum")
                        if ($patchShasumEntry.Length -gt 512) { throw "Delta package shasum exceeds the allowed size: $($patchShasumEntry.FullName)" }
                        $patchShasumBytes = Read-LimitedStream $patchShasumEntry.Open() 512 "Delta package shasum"
                        $patchShasumStream = [IO.MemoryStream]::new($patchShasumBytes, $false)
                        $patchShasumReader = [IO.StreamReader]::new($patchShasumStream, $utf8Strict, $true, 128, $false)
                        try { $patchShasum = $patchShasumReader.ReadToEnd() }
                        finally { $patchShasumReader.Dispose() }
                        $expectedShasumLeaf = [Regex]::Escape(([IO.Path]::GetFileName($basePath)) + '.shasum')
                        if ($patchShasum -cnotmatch ('^[A-F0-9]{40} ' + $expectedShasumLeaf + ' [1-9][0-9]{0,18}$')) {
                            throw "Delta package shasum has invalid UTF-8 or format: $($patchShasumEntry.FullName)"
                        }
                        $patchContracts.Add([pscustomobject]@{
                            BasePath=$basePath
                            Extension=if ($patch.FullName.EndsWith('.zsdiff', [StringComparison]::Ordinal)) { '.zsdiff' } else { '.diff' }
                            Length=$patch.Length
                            Shasum=$patchShasum
                        })
                    }
                    if ($null -eq $CurrentFullTargets -or [string]::IsNullOrWhiteSpace($ExpectedLegacyLauncherSha1) -or $ExpectedLegacyLauncherLength -lt 1) {
                        throw 'Delta package cannot be bound to the current Full compatibility launcher.'
                    }
                    Assert-DeltaPatchContract $patchContracts.ToArray() $CurrentFullTargets $ExpectedPackageVersion `
                        $currentVersion $legacyPatchBase
                }
            }
            $executableIndex = 0
            $publisherRoleImages = [Collections.Generic.List[object]]::new()
            foreach ($requiredName in $RequiredPublisherExecutables) {
                if (-not $innerNames.ContainsKey($requiredName) -or $innerNames[$requiredName] -cne $requiredName) {
                    throw "$Description is missing required publisher executable: $requiredName"
                }
            }
            $expectedExecutables = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($name in $RequiredPublisherExecutables) { [void]$expectedExecutables.Add($name) }
            foreach ($name in $AllowedThirdPartyExecutables) { [void]$expectedExecutables.Add($name) }
            $actualExecutables = @($inner.Entries | Where-Object { $_.FullName.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase) })
            foreach ($executableEntry in $actualExecutables) {
                if (-not $expectedExecutables.Contains($executableEntry.FullName)) {
                    throw "$Description contains an unexpected executable: $($executableEntry.FullName)"
                }
            }
            # Every executable carried by an inner archive must be explicitly
            # classified; every publisher-owned executable is independently checked
            # against its Authenticode image digest and publisher SPKI. Velopack 1.2
            # creates its launcher/updater stubs after its timestamped application-signing
            # pass, so the timestamp requirement applies to the application executable;
            # the outer Setup executable is timestamped and checked separately below.
            foreach ($requiredName in $RequiredPublisherExecutables) {
                $requiredEntry = $inner.GetEntry($requiredName)
                if ($null -eq $requiredEntry -or $requiredEntry.Length -le 0) { throw "$Description has an empty required publisher executable." }
                $destination = Join-Path $temporaryRoot ("inner-$executableIndex-$([Guid]::NewGuid().ToString('N')).exe")
                $executableIndex++
                $input = $requiredEntry.Open(); $output = [IO.File]::Create($destination)
                try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
                $publisherTimestampRequired = $RequireTimestamp.IsPresent -and
                    $ExpectedPackageVersion -ceq $currentVersion -and $requiredName -in @(
                    "current/$productMainExecutableName",
                    "current/$productLegacyExecutableName",
                    "lib/app/$productMainExecutableName",
                    "lib/app/$productLegacyExecutableName")
                $expectedApplicationMachine = if ($requiredName -in @(
                        "current/$productMainExecutableName",
                        "current/$productLegacyExecutableName",
                        "lib/app/$productMainExecutableName",
                        "lib/app/$productLegacyExecutableName")) { 0x8664 } else { 0 }
                $artifactRole = if ($isCurrentPackage -and $requiredName -in @(
                        "current/$productMainExecutableName", "lib/app/$productMainExecutableName")) {
                    'main-application'
                } elseif ($isCurrentPackage -and $requiredName -in @(
                        "current/$productLegacyExecutableName", "lib/app/$productLegacyExecutableName")) {
                    'legacy-compatibility-launcher'
                } elseif (-not $isCurrentPackage -and $requiredName -ceq "lib/app/$productLegacyExecutableName") {
                    'historical-main-application'
                } else { 'velopack-bootstrapper' }
                $publisherRoleImage = Assert-Authenticode $destination $ExpectedPublisherSpkiSha256 $publisherTimestampRequired `
                    "$Description::$requiredName" $expectedApplicationMachine $artifactRole
                $publisherRoleImages.Add($publisherRoleImage)
                if ($ExpectedPackageType -ceq 'Portable' -and $requiredName -ceq $productMainExecutableName) {
                    Assert-FileContainsText $destination $productMainStem 'Root launcher does not reference the configured main executable.'
                }
            }
            if ($isCurrentPackage) { Assert-DistinctCurrentApplicationImages $publisherRoleImages.ToArray() }
            $psfSpki = '7a1e6298a8fc5ec53e6389ad8532cb869e36233caf30c5ee0a1108d09a2b9801'
            foreach ($thirdPartyName in $AllowedThirdPartyExecutables) {
                if (-not $innerNames.ContainsKey($thirdPartyName)) { continue }
                $manifestName = $thirdPartyName.Substring(0, $thirdPartyName.LastIndexOf('/') + 1) + 'helper-manifest.json'
                if (-not $innerNames.ContainsKey($manifestName)) { throw "$Description is missing the authenticated helper manifest for $thirdPartyName" }
                $manifestEntry = $inner.GetEntry($manifestName)
                $manifestReader = [IO.StreamReader]::new($manifestEntry.Open(), [Text.UTF8Encoding]::new($false, $true), $true, 4096, $false)
                try { $helperManifest = $manifestReader.ReadToEnd() | ConvertFrom-Json }
                finally { $manifestReader.Dispose() }
                $leaf = [IO.Path]::GetFileName($thirdPartyName)
                $manifestFiles = @($helperManifest.files | Where-Object { [string]$_.path -ceq $leaf })
                if ($manifestFiles.Count -ne 1 -or [string]$manifestFiles[0].sha256 -cnotmatch '^[0-9a-f]{64}$') {
                    throw "$Description helper manifest does not uniquely authenticate $leaf"
                }
                $thirdPartyEntry = $inner.GetEntry($thirdPartyName)
                if ([long]$manifestFiles[0].length -ne $thirdPartyEntry.Length) { throw "$Description helper manifest length differs for $leaf" }
                $thirdPartyDestination = Join-Path $temporaryRoot ("third-party-$executableIndex-$([Guid]::NewGuid().ToString('N')).exe")
                $executableIndex++
                $input = $thirdPartyEntry.Open(); $output = [IO.File]::Create($thirdPartyDestination)
                try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
                Assert-ThirdPartyAuthenticode $thirdPartyDestination $psfSpki "$Description::$thirdPartyName" ([string]$manifestFiles[0].sha256)
            }
            # DLL/PYD entries are package data covered by the outer signed manifest and
            # their enclosing archive hash. They are not classified as Authenticode
            # executable assets; every actual .exe is handled by the signer policies above.
            foreach ($peEntry in @($inner.Entries | Where-Object {
                        $_.FullName.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase) -or
                        $_.FullName.EndsWith('.pyd', [StringComparison]::OrdinalIgnoreCase)
                    })) {
                $signatureEvidence.Add([ordered]@{
                    file = "$Description::$($peEntry.FullName)"
                    policy = 'manifest-hash-package-data'
                    assetClass = 'package-data-not-authenticode-executable'
                    signerSpkiSha256 = $null
                    sha256 = Get-LowerHash ([Security.Cryptography.SHA256]::Create()) $peEntry.Open()
                    imageDigestVerified = $false
                    timestampPresent = $false
                    timestampUtc = $null
                })
            }
        }
        finally { $inner.Dispose() }
    }

    $manifestBytes = Read-EntryBytes $manifestName 81788
    $signatureBytes = Read-EntryBytes $signatureName 384
    if ($signatureBytes.Length -ne 384) { throw 'Manifest signature must be one raw RSA-3072 signature.' }
    if ($manifestBytes.Length -eq 0 -or $manifestBytes[0] -eq 0xef) { throw 'Manifest must be non-empty UTF-8 without BOM.' }
    try { $manifestText = $utf8Strict.GetString($manifestBytes) }
    catch { throw 'Manifest is not strict UTF-8.' }
    if ($manifestText.Contains("`r") -or -not $manifestText.EndsWith("`n") -or $manifestText.EndsWith("`n`n")) { throw 'Manifest must use LF and exactly one final LF.' }
    $lines = @($manifestText.Substring(0, $manifestText.Length - 1).Split("`n"))
    if ($lines.Count -ne $entries.Count - 2) { throw 'Manifest does not cover the complete ZIP closure.' }
    $manifestEntries = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    $manifestNameCases = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    $previousName = $null
    foreach ($line in $lines) {
        if ($line -cnotmatch '^(?<hash>[0-9a-f]{64})  (?<name>[!-~]+)$') { throw 'Manifest contains a non-canonical line.' }
        $name = [string]$Matches.name
        if (-not (Test-FlatAsciiName $name) -or $name -cin @($manifestName, $signatureName) -or -not (Test-AllowedName $name)) { throw "Manifest contains an unsafe file name: $name" }
        if ($null -ne $previousName -and [StringComparer]::Ordinal.Compare($previousName, $name) -ge 0) { throw 'Manifest entries are not in ordinal UTF-8 filename order.' }
        if ($manifestEntries.ContainsKey($name)) { throw "Manifest contains a duplicate or case-colliding file: $name" }
        $manifestEntries.Add($name, [string]$Matches.hash)
        $manifestNameCases.Add($name, $name)
        $previousName = $name
    }
    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($publisherCertificate)
    try {
        if (-not $rsa.VerifyData($manifestBytes, $signatureBytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)) {
            throw 'Manifest signature verification failed.'
        }
    }
    finally { $rsa.Dispose() }

    $actualFiles = [ordered]@{}
    foreach ($entry in $entries) {
        if ($entry.Name -cin @($manifestName, $signatureName)) { continue }
        if (-not $manifestEntries.ContainsKey($entry.Name) -or $manifestNameCases[$entry.Name] -cne $entry.Name) { throw 'Manifest casing mismatch.' }
        $actualHash = Get-EntrySha256 $entry
        if ($actualHash -cne $manifestEntries[$entry.Name]) { throw "Manifest SHA-256 differs from file: $($entry.Name)" }
        $actualFiles[$entry.Name] = [pscustomobject]@{ hash = $actualHash; size = [long]$entry.Length }
    }

    try { $releaseFeed = $utf8Strict.GetString((Read-EntryBytes $releaseFeedName 1048576)) | ConvertFrom-Json }
    catch { throw 'Production release JSON is invalid.' }
    if ($null -eq $releaseFeed.PSObject.Properties['Assets']) { throw 'Production release JSON has no Assets array.' }
    $assets = @($releaseFeed.Assets)
    if ($assets.Count -eq 0 -or $assets.Count -gt 254) { throw 'Production release JSON has an invalid asset count.' }
    $releaseFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $fullFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $versions = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($asset in $assets) {
        foreach ($property in @('PackageId','Version','Type','FileName','SHA256','Size')) { if ($null -eq $asset.PSObject.Properties[$property]) { throw "Release asset is missing $property." } }
        $version = [string]$asset.Version
        $type = [string]$asset.Type
        $fileName = [string]$asset.FileName
        if ([string]$asset.PackageId -cne $packageId -or -not (Test-CanonicalVersion $version) -or $type -cnotin @('Full','Delta')) { throw 'Release asset identity, version, or type is invalid.' }
        $expectedFileName = "$packageId-$version-$($type.ToLowerInvariant()).nupkg"
        if ($fileName -cne $expectedFileName -or -not $actualFiles.Contains($fileName) -or -not $releaseFiles.Add($fileName)) { throw "Release asset reference is invalid: $fileName" }
        if (-not [string]::Equals([string]$asset.SHA256, [string]$actualFiles[$fileName].hash, [StringComparison]::OrdinalIgnoreCase) -or
            [long]$asset.Size -ne $actualFiles[$fileName].size) { throw "Release asset hash or size differs: $fileName" }
        if ($type -ceq 'Full') { [void]$fullFiles.Add($fileName) }
        [void]$versions.Add($version)
    }
    foreach ($fileName in @($actualFiles.Keys | Where-Object { $_ -clike '*.nupkg' })) { if (-not $releaseFiles.Contains($fileName)) { throw "Unreferenced package file: $fileName" } }
    $currentVersion = @($versions | Sort-Object { [version]$_ } -Descending)[0]
    if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion) -and $currentVersion -cne $ExpectedVersion) { throw 'Production release version does not match ExpectedVersion.' }
    $deltaAssets = @($assets | Where-Object { [string]$_.Type -ceq 'Delta' })
    if ($deltaAssets.Count -gt 0) {
        $deltaVersions = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($deltaAsset in $deltaAssets) {
            $deltaVersion = [string]$deltaAsset.Version
            if (-not $deltaVersions.Add($deltaVersion) -or -not $fullFiles.Contains("$packageId-$deltaVersion-full.nupkg")) {
                throw 'Each production Delta must uniquely match a Full package version.'
            }
            $priorFullVersions = @($assets | Where-Object {
                    [string]$_.Type -ceq 'Full' -and (Compare-Version ([string]$_.Version) $deltaVersion) -lt 0
                } | ForEach-Object { [string]$_.Version })
            if ($priorFullVersions.Count -eq 0) { throw 'Production Delta has no lower Full base version.' }
        }
        if (-not $deltaVersions.Contains($currentVersion)) { throw 'Current production Full package has no matching Delta.' }
    }

    try { $catalog = $utf8Strict.GetString((Read-EntryBytes $assetCatalogName 1048576)) | ConvertFrom-Json }
    catch { throw 'Production asset catalog is invalid.' }
    if ($catalog -isnot [array]) { throw 'Production asset catalog must be a JSON array.' }
    $expectedCatalog = [ordered]@{ Installer = "$packageId-Setup.exe"; Portable = "$packageId-Portable.zip"; Full = "$packageId-$currentVersion-full.nupkg" }
    if ($releaseFiles.Contains("$packageId-$currentVersion-delta.nupkg")) { $expectedCatalog.Delta = "$packageId-$currentVersion-delta.nupkg" }
    $seenTypes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($item in @($catalog)) {
        $properties = @($item.PSObject.Properties.Name)
        if ($properties.Count -ne 2 -or -not ($properties -ccontains 'Type') -or -not ($properties -ccontains 'RelativeFileName')) { throw 'Asset catalog properties are invalid.' }
        $type = [string]$item.Type; $name = [string]$item.RelativeFileName
        if (-not $expectedCatalog.Contains($type) -or $name -cne $expectedCatalog[$type] -or -not $seenTypes.Add($type) -or -not $actualFiles.Contains($name)) { throw "Asset catalog reference is invalid: $name" }
    }
    if ($seenTypes.Count -ne $expectedCatalog.Count) { throw 'Asset catalog is incomplete.' }

    try { $releaseLines = @($utf8Strict.GetString((Read-EntryBytes $releasesName 1048576)).TrimEnd("`n").Split("`n")) }
    catch { throw 'RELEASES index is invalid.' }
    if ($releaseLines.Count -ne $fullFiles.Count) { throw 'RELEASES does not cover every Full package exactly once.' }
    $indexed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($line in $releaseLines) {
        if ($line -cnotmatch '^(?<sha1>[0-9A-F]{40}) (?<name>[^\s]+) (?<size>[0-9]+)$') { throw 'RELEASES contains a non-canonical line.' }
        $name = [string]$Matches.name
        if (-not $fullFiles.Contains($name) -or -not $indexed.Add($name) -or [long]$Matches.size -ne $actualFiles[$name].size) { throw "RELEASES reference is invalid: $name" }
        $entry = @($entries | Where-Object { $_.Name -ceq $name })[0]
        if ($null -ne $entry.Path) { $sha1 = Get-LowerHash ([Security.Cryptography.SHA1]::Create()) ([IO.File]::OpenRead($entry.Path)) }
        else { $sha1 = Get-LowerHash ([Security.Cryptography.SHA1]::Create()) $entry.ZipEntry.Open() }
        if ($sha1.ToUpperInvariant() -cne [string]$Matches.sha1) { throw "RELEASES SHA-1 differs: $name" }
    }

    $temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("superpan-ota-verify-" + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    $setupName = "$packageId-Setup.exe"
    $setupPath = Join-Path $temporaryRoot $setupName
    Extract-Entry $setupName $setupPath
    [void](Assert-Authenticode $setupPath $ExpectedPublisherSpkiSha256 $RequireTimestamp.IsPresent $setupName)

    $portableName = "$packageId-Portable.zip"
    $portablePath = Join-Path $temporaryRoot $portableName
    Extract-Entry $portableName $portablePath
    Assert-InnerArchive $portablePath 'Portable archive' @(
        $productMainExecutableName,
        'Update.exe',
        "current/$productMainExecutableName",
        "current/$productLegacyExecutableName") @(
        'current/managed-browser-helper/python.exe',
        'current/managed-browser-helper/pythonw.exe') $currentVersion 'Portable'

    $currentLegacyLauncherSha1 = $null
    [long]$currentLegacyLauncherLength = -1
    $currentFullTargets = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    $validatedPackagePaths = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    $packageIndex = 0
    $assetsToValidate = @($assets | Sort-Object @{
            Expression = {
                if ([string]$_.Type -ceq 'Full' -and [string]$_.Version -ceq $currentVersion) { 0 }
                elseif ([string]$_.Type -ceq 'Full') { 1 }
                else { 2 }
            }
        }, @{ Expression = { [string]$_.Version } })
    foreach ($asset in $assetsToValidate) {
        $packageName = [string]$asset.FileName
        $packagePath = Join-Path $temporaryRoot ("package-$packageIndex.nupkg")
        $packageIndex++
        Extract-Entry $packageName $packagePath
        $validatedPackagePaths.Add($packageName, $packagePath)
        $requiredPackageExecutables = if ([string]$asset.Type -ceq 'Full' -and [string]$asset.Version -ceq $currentVersion) {
            @("lib/app/$($productMainStem)_ExecutionStub.exe", "lib/app/$productMainExecutableName", "lib/app/$productLegacyExecutableName", 'lib/app/Squirrel.exe')
        }
        elseif ([string]$asset.Type -ceq 'Full') {
            @('lib/app/BaiduBatchTransfer.App_ExecutionStub.exe', "lib/app/$productLegacyExecutableName", 'lib/app/Squirrel.exe')
        }
        elseif ([string]$asset.Type -ceq 'Delta') {
            @("lib/app/$($productMainStem)_ExecutionStub.exe", "lib/app/$productMainExecutableName")
        }
        else { @() }
        $allowedPackageExecutables = if ([string]$asset.Type -ceq 'Full') {
            @('lib/app/managed-browser-helper/python.exe', 'lib/app/managed-browser-helper/pythonw.exe')
        }
        else { @() }
        Assert-InnerArchive $packagePath "Package $packageName" $requiredPackageExecutables $allowedPackageExecutables `
            ([string]$asset.Version) ([string]$asset.Type) $currentLegacyLauncherSha1 $currentLegacyLauncherLength $currentFullTargets
        if ([string]$asset.Type -ceq 'Full' -and [string]$asset.Version -ceq $currentVersion) {
            $currentFull = [IO.Compression.ZipFile]::OpenRead($packagePath)
            try {
                $legacyEntry = $currentFull.GetEntry("lib/app/$productLegacyExecutableName")
                if ($null -eq $legacyEntry -or $legacyEntry.Length -le 0) {
                    throw 'Current Full package compatibility launcher identity is unavailable.'
                }
                $currentLegacyLauncherLength = $legacyEntry.Length
                $currentLegacyLauncherSha1 = Get-LowerHash ([Security.Cryptography.SHA1]::Create()) $legacyEntry.Open()
                foreach ($targetEntry in $currentFull.Entries) {
                    if ([string]::IsNullOrEmpty($targetEntry.Name)) { continue }
                    if ($currentFullTargets.ContainsKey($targetEntry.FullName)) { throw 'Current Full package has duplicate Delta target paths.' }
                    $currentFullTargets.Add($targetEntry.FullName, [pscustomobject]@{
                        Sha1=Get-LowerHash ([Security.Cryptography.SHA1]::Create()) $targetEntry.Open()
                        Length=$targetEntry.Length
                    })
                }
            }
            finally { $currentFull.Dispose() }
        }
    }

    $currentDeltaAssets = @($assets | Where-Object {
            [string]$_.Type -ceq 'Delta' -and [string]$_.Version -ceq $currentVersion
        })
    if ($currentDeltaAssets.Count -gt 0) {
        if ($currentDeltaAssets.Count -ne 1) { throw 'Current production Delta asset is ambiguous.' }
        $lowerFullAssets = @($assets | Where-Object {
                [string]$_.Type -ceq 'Full' -and (Compare-Version ([string]$_.Version) $currentVersion) -lt 0
            } | Sort-Object { [version]$_.Version } -Descending)
        if ($lowerFullAssets.Count -eq 0) { throw 'Current production Delta has no lower Full base package.' }
        $baseVersion = [string]$lowerFullAssets[0].Version
        $baseAssets = @($lowerFullAssets | Where-Object { [string]$_.Version -ceq $baseVersion })
        if ($baseAssets.Count -ne 1) { throw 'Current production Delta Full base package is ambiguous.' }
        $currentFullAssets = @($assets | Where-Object {
                [string]$_.Type -ceq 'Full' -and [string]$_.Version -ceq $currentVersion
            })
        if ($currentFullAssets.Count -ne 1) { throw 'Current production Full package is ambiguous.' }
        $sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
        $toolManifestPath = Join-Path $sourceRoot '.config\dotnet-tools.json'
        Assert-DeltaReconstructsFull `
            $validatedPackagePaths[[string]$baseAssets[0].FileName] `
            $validatedPackagePaths[[string]$currentDeltaAssets[0].FileName] `
            $validatedPackagePaths[[string]$currentFullAssets[0].FileName] `
            $toolManifestPath
    }

    $report = [ordered]@{
        schemaVersion = 1
        verified = $true
        packageId = $packageId
        channel = $channel
        version = $currentVersion
        manifestSha256 = Get-BytesSha256 $manifestBytes
        publisherSpkiSha256 = $certificateSpki
        authenticodeTimestampRequired = $RequireTimestamp.IsPresent
        executableSignatures = @($signatureEvidence)
        fileCount = $entries.Count
        bundleSha256 = if (Test-Path -LiteralPath $resolvedBundle -PathType Leaf) { Get-FileSha256 $resolvedBundle } else { $null }
        files = @($actualFiles.GetEnumerator() | Sort-Object Key | ForEach-Object { [ordered]@{ name = $_.Key; size = $_.Value.size; sha256 = $_.Value.hash } })
    }
    $json = $report | ConvertTo-Json -Depth 6
    if (-not [string]::IsNullOrWhiteSpace($OutputReportPath)) {
        $reportPath = [IO.Path]::GetFullPath($OutputReportPath)
        $parent = [IO.Path]::GetDirectoryName($reportPath)
        if (-not [string]::IsNullOrWhiteSpace($parent)) { [IO.Directory]::CreateDirectory($parent) | Out-Null }
        [IO.File]::WriteAllText($reportPath, $json + "`n", [Text.UTF8Encoding]::new($false))
    }
    Write-Output $json
}
finally {
    if ($null -ne $archive) { $archive.Dispose() }
    $publisherCertificate.Dispose()
    if ($null -ne $temporaryRoot -and (Test-Path -LiteralPath $temporaryRoot)) {
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            try { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction Stop; break }
            catch {
                if ($attempt -eq 19) { throw }
                [GC]::Collect(); [GC]::WaitForPendingFinalizers()
                Start-Sleep -Milliseconds 100
            }
        }
    }
}

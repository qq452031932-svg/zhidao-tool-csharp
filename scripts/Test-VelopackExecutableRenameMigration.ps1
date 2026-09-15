[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OldAssets,
    [Parameter(Mandatory = $true)][string]$NewAssets,
    [Parameter(Mandatory = $true)][string]$EvidenceDirectory,
    [Parameter(Mandatory = $true)][ValidateSet('Setup', 'Portable')][string]$AssetKind,
    [Parameter(Mandatory = $true)][ValidateSet('Delta', 'Full')][string]$Path,
    [ValidateSet('None', 'DownloadInterrupted', 'ApplicationFailed')][string]$FailureInjection = 'None',
    [switch]$IUnderstandThisIsAnIsolatedWindowsProfile
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-SafeProfileTopology {
    param([string]$EvidencePath,[bool]$IsolationConfirmed)
    if(-not$IsolationConfirmed){throw 'The explicit isolation confirmation switch is required.'}
    $profile=[IO.Path]::GetFullPath([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)).TrimEnd('\')
    $local=[IO.Path]::GetFullPath([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)).TrimEnd('\')
    $expectedLocal=Join-Path $profile 'AppData\Local'
    if(-not[string]::Equals($local,[IO.Path]::GetFullPath($expectedLocal).TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)){throw 'LocalAppData is not the fixed child of the isolated USERPROFILE.'}
    $targets=@($profile,$local,(Join-Path $profile '.velopack-rename-migration-isolation'),(Join-Path $local 'SuperPan.BaiduBatchTransfer'),(Join-Path $local 'BaiduBatchTransfer'),(Join-Path $local 'SuperPan\Licensing\v1'),[IO.Path]::GetFullPath($EvidencePath))
    foreach($target in $targets){
        $full=[IO.Path]::GetFullPath($target)
        if(-not[string]::Equals($full,$profile,[StringComparison]::OrdinalIgnoreCase)-and-not$full.StartsWith($profile+'\',[StringComparison]::OrdinalIgnoreCase)){throw "Safety target escapes isolated USERPROFILE: $full"}
        $root=[IO.Path]::GetPathRoot($full);$cursor=$root
        foreach($component in $full.Substring($root.Length).Split(@('\'),[StringSplitOptions]::RemoveEmptyEntries)){
            $cursor=Join-Path $cursor $component
            if(Test-Path -LiteralPath $cursor){$item=Get-Item -LiteralPath $cursor -Force;if(($item.Attributes-band[IO.FileAttributes]::ReparsePoint)-ne 0){throw "Profile topology contains a reparse point: $cursor"}}
        }
    }
    $evidenceParent=Split-Path -Parent ([IO.Path]::GetFullPath($EvidencePath))
    while(-not(Test-Path -LiteralPath $evidenceParent -PathType Container)){$next=Split-Path -Parent $evidenceParent;if($next-ceq$evidenceParent){throw 'EvidenceDirectory has no existing safe parent.'};$evidenceParent=$next}
    return [ordered]@{Profile=$profile;LocalAppData=$local;EvidenceExistingParent=$evidenceParent}
}

$script:IsolationDirectoryName = '.velopack-rename-migration-isolation'
$script:InitialSafeTopology=Assert-SafeProfileTopology -EvidencePath $EvidenceDirectory -IsolationConfirmed ([bool]$IUnderstandThisIsAnIsolatedWindowsProfile)
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Security
Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
public static class MigrationNativeCommandLine {
    [DllImport("shell32.dll", SetLastError=true)] private static extern IntPtr CommandLineToArgvW(string commandLine, out int argc);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr value);
    public static string[] Split(string commandLine) {
        int count; var pointer=CommandLineToArgvW(commandLine, out count);
        if(pointer==IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try { var result=new string[count]; for(int i=0;i<count;i++) result[i]=Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer,i*IntPtr.Size)); return result; }
        finally { LocalFree(pointer); }
    }
}
public static class MigrationFileIdentity {
    [StructLayout(LayoutKind.Sequential)] private struct BY_HANDLE_FILE_INFORMATION {
        public uint FileAttributes; public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime; public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber; public uint FileSizeHigh; public uint FileSizeLow; public uint NumberOfLinks;
        public uint FileIndexHigh; public uint FileIndexLow;
    }
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool GetFileInformationByHandle(IntPtr handle, out BY_HANDLE_FILE_INFORMATION info);
    public static string Get(FileStream stream) {
        BY_HANDLE_FILE_INFORMATION info;
        if(!GetFileInformationByHandle(stream.SafeFileHandle.DangerousGetHandle(), out info))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return info.VolumeSerialNumber.ToString("x8") + ":" + info.FileIndexHigh.ToString("x8") + info.FileIndexLow.ToString("x8");
    }
}
public sealed class MigrationContainedResult {
    public int ExitCode; public int ProcessId; public string StartedUtc; public string CompletedUtc;
    public string StandardOutput; public string StandardError; public string Failure; public bool SafeToCleanup;
    public int RestartPid; public string RestartPath; public string RestartStartedUtc; public string RestartFileIdentity; public IntPtr JobHandle;
}
public static class MigrationContainedRunner {
    // Threat boundary: the gate requires an isolated standard-user profile with no
    // other untrusted process under the same token. QueryFullProcessImageName plus
    // immediate file-id capture/PowerShell read lock is fail-closed within that boundary;
    // it does not claim to defeat a malicious same-user process racing mapped images.
    const uint CREATE_SUSPENDED=4, CREATE_NO_WINDOW=0x08000000, STARTF_USESTDHANDLES=0x100;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE=0x2000, JOB_OBJECT_LIMIT_JOB_MEMORY=0x200, JOB_OBJECT_LIMIT_ACTIVE_PROCESS=8;
    const uint HANDLE_FLAG_INHERIT=1, WAIT_OBJECT_0=0;
    const int JobObjectBasicProcessIdList=3;
    [StructLayout(LayoutKind.Sequential)] struct STARTUPINFO { public uint cb; public string r1,desktop,title; public uint x,y,xs,ys,xc,yc,fill,flags; public short show,reserved; public IntPtr r2,input,output,error; }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr process,thread; public uint processId,threadId; }
    [StructLayout(LayoutKind.Sequential)] struct BASIC_LIMIT { public long processTime,jobTime; public uint flags; public UIntPtr min,max; public uint active; public UIntPtr affinity; public uint priority,scheduling; }
    [StructLayout(LayoutKind.Sequential)] struct IO_COUNTERS { public ulong a,b,c,d,e,f; }
    [StructLayout(LayoutKind.Sequential)] struct EXT_LIMIT { public BASIC_LIMIT basic; public IO_COUNTERS io; public UIntPtr processMemory,jobMemory,peakProcess,peakJob; }
    [StructLayout(LayoutKind.Sequential)] struct ACCOUNTING { public long a,b,c,d; public uint faults,total,active,terminated; }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateJobObject(IntPtr a,string n);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetInformationJobObject(IntPtr j,int c,IntPtr p,uint l);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool QueryInformationJobObject(IntPtr j,int c,IntPtr p,uint l,out uint r);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateProcess(string app,StringBuilder cmd,IntPtr pa,IntPtr ta,bool inherit,uint flags,IntPtr env,string cwd,ref STARTUPINFO si,out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool AssignProcessToJobObject(IntPtr j,IntPtr p);
    [DllImport("kernel32.dll",SetLastError=true)] static extern uint ResumeThread(IntPtr t);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool TerminateJobObject(IntPtr j,uint e);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool TerminateProcess(IntPtr process,uint exitCode);
    [DllImport("kernel32.dll",SetLastError=true)] static extern uint WaitForSingleObject(IntPtr h,uint ms);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetExitCodeProcess(IntPtr h,out uint e);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool QueryFullProcessImageName(IntPtr h,uint flags,StringBuilder path,ref uint size);
    [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetProcessTimes(IntPtr process,out System.Runtime.InteropServices.ComTypes.FILETIME created,out System.Runtime.InteropServices.ComTypes.FILETIME exited,out System.Runtime.InteropServices.ComTypes.FILETIME kernel,out System.Runtime.InteropServices.ComTypes.FILETIME user);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetHandleInformation(IntPtr h,uint m,uint f);
    [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr GetStdHandle(int i);
    static string Q(string s){var b=new StringBuilder("\"");int n=0;foreach(char c in s){if(c=='\\'){n++;continue;}if(c=='\"'){b.Append('\\',n*2+1).Append('"');}else{if(n>0)b.Append('\\',n);b.Append(c);}n=0;}if(n>0)b.Append('\\',n*2);return b.Append('"').ToString();}
    static uint Active(IntPtr j){int z=Marshal.SizeOf(typeof(ACCOUNTING));IntPtr p=Marshal.AllocHGlobal(z);try{uint r;if(!QueryInformationJobObject(j,1,p,(uint)z,out r))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());return ((ACCOUNTING)Marshal.PtrToStructure(p,typeof(ACCOUNTING))).active;}finally{Marshal.FreeHGlobal(p);}}
    static uint[] ActivePids(IntPtr job){int capacity=16;int bytes=8+capacity*IntPtr.Size;IntPtr memory=Marshal.AllocHGlobal(bytes);try{uint returned;if(!QueryInformationJobObject(job,JobObjectBasicProcessIdList,memory,(uint)bytes,out returned))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());uint count=(uint)Marshal.ReadInt32(memory,4);var ids=new uint[count];for(int i=0;i<count;i++)ids[i]=unchecked((uint)Marshal.ReadIntPtr(memory,8+i*IntPtr.Size).ToInt64());return ids;}finally{Marshal.FreeHGlobal(memory);}}
    static string ProcessPathAndStart(uint pid,out string started,out string identity){IntPtr process=OpenProcess(0x1000,false,pid);if(process==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());try{var path=new StringBuilder(32768);uint size=(uint)path.Capacity;if(!QueryFullProcessImageName(process,0,path,ref size))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());System.Runtime.InteropServices.ComTypes.FILETIME c,e,k,u;if(!GetProcessTimes(process,out c,out e,out k,out u))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());long ticks=((long)c.dwHighDateTime<<32)|(uint)c.dwLowDateTime;started=DateTimeOffset.FromFileTime(ticks).ToString("O");string full=Path.GetFullPath(path.ToString());using(var file=new FileStream(full,FileMode.Open,FileAccess.Read,FileShare.Read)){identity=MigrationFileIdentity.Get(file);}return full;}finally{CloseHandle(process);}}
    static bool Offline(){foreach(var n in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()){if(n.NetworkInterfaceType!=System.Net.NetworkInformation.NetworkInterfaceType.Loopback && n.OperationalStatus==System.Net.NetworkInformation.OperationalStatus.Up)return false;}return true;}
    public static MigrationContainedResult Run(string exe,string[] args,string cwd,string stdoutPath,string stderrPath,int timeoutMs,long maxLog,string completionMode,string expectedRestartPath){
      if(completionMode!="NoRestart"&&completionMode!="SuccessExpectRestart"&&completionMode!="ExpectRootAlive")throw new ArgumentException("Unknown completion mode.");if(completionMode!="NoRestart"&&String.IsNullOrWhiteSpace(expectedRestartPath))throw new ArgumentException("Expected live-process path is required.");
      var r=new MigrationContainedResult{ExitCode=-1,SafeToCleanup=false};IntPtr job=IntPtr.Zero;PROCESS_INFORMATION pi=new PROCESS_INFORMATION();FileStream so=null,se=null;bool assigned=false;
      try{job=CreateJobObject(IntPtr.Zero,null);if(job==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());var lim=new EXT_LIMIT();lim.basic.flags=JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE|JOB_OBJECT_LIMIT_JOB_MEMORY|JOB_OBJECT_LIMIT_ACTIVE_PROCESS;lim.basic.active=8;lim.jobMemory=new UIntPtr(1073741824);int z=Marshal.SizeOf(typeof(EXT_LIMIT));IntPtr lp=Marshal.AllocHGlobal(z);try{Marshal.StructureToPtr(lim,lp,false);if(!SetInformationJobObject(job,9,lp,(uint)z))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());}finally{Marshal.FreeHGlobal(lp);}
        so=new FileStream(stdoutPath,FileMode.CreateNew,FileAccess.Write,FileShare.Read);se=new FileStream(stderrPath,FileMode.CreateNew,FileAccess.Write,FileShare.Read);SetHandleInformation(so.SafeFileHandle.DangerousGetHandle(),HANDLE_FLAG_INHERIT,HANDLE_FLAG_INHERIT);SetHandleInformation(se.SafeFileHandle.DangerousGetHandle(),HANDLE_FLAG_INHERIT,HANDLE_FLAG_INHERIT);
        var cmd=new StringBuilder(Q(exe));foreach(var a in args)cmd.Append(' ').Append(Q(a));var si=new STARTUPINFO();si.cb=(uint)Marshal.SizeOf(typeof(STARTUPINFO));si.flags=STARTF_USESTDHANDLES;si.input=GetStdHandle(-10);si.output=so.SafeFileHandle.DangerousGetHandle();si.error=se.SafeFileHandle.DangerousGetHandle();
        r.StartedUtc=DateTimeOffset.UtcNow.ToString("O");if(!CreateProcess(exe,cmd,IntPtr.Zero,IntPtr.Zero,true,CREATE_SUSPENDED|CREATE_NO_WINDOW,IntPtr.Zero,cwd,ref si,out pi))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());r.ProcessId=(int)pi.processId;
        var image=new StringBuilder(32768);uint imageSize=(uint)image.Capacity;if(!QueryFullProcessImageName(pi.process,0,image,ref imageSize))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());if(!String.Equals(Path.GetFullPath(image.ToString()),Path.GetFullPath(exe),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Suspended process executable path differs from the verified ApplicationName.");
        if(!AssignProcessToJobObject(job,pi.process))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());assigned=true;if(ResumeThread(pi.thread)==UInt32.MaxValue)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());CloseHandle(pi.thread);pi.thread=IntPtr.Zero;var timer=Stopwatch.StartNew();long rootDoneAt=-1;
        if(completionMode=="ExpectRootAlive"){if(WaitForSingleObject(pi.process,0)==WAIT_OBJECT_0)throw new InvalidOperationException("Expected root process exited before liveness proof.");string rootStarted,rootIdentity;string rootPath=ProcessPathAndStart(pi.processId,out rootStarted,out rootIdentity);if(!String.Equals(rootPath,Path.GetFullPath(expectedRestartPath),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("ExpectRootAlive executable path mismatch.");r.RestartPid=(int)pi.processId;r.RestartPath=rootPath;r.RestartStartedUtc=rootStarted;r.RestartFileIdentity=rootIdentity;r.JobHandle=job;job=IntPtr.Zero;uint ignored;if(GetExitCodeProcess(pi.process,out ignored))r.ExitCode=unchecked((int)ignored);CloseHandle(pi.process);pi.process=IntPtr.Zero;}
        while(completionMode!="ExpectRootAlive"){bool done=rootDoneAt>=0||WaitForSingleObject(pi.process,100)==WAIT_OBJECT_0;if(!Offline()){r.Failure="Offline invariant changed during contained process.";TerminateJobObject(job,0xEE10);break;}if(so.Length>maxLog||se.Length>maxLog||so.Length>maxLog-se.Length){r.Failure="Contained process output exceeded limit.";TerminateJobObject(job,0xEE11);break;}if(timer.ElapsedMilliseconds>timeoutMs){r.Failure="Contained process timed out.";TerminateJobObject(job,0xEE12);break;}if(done){if(rootDoneAt<0){rootDoneAt=timer.ElapsedMilliseconds;uint rootCode;if(GetExitCodeProcess(pi.process,out rootCode))r.ExitCode=unchecked((int)rootCode);CloseHandle(pi.process);pi.process=IntPtr.Zero;}var ids=ActivePids(job);if(completionMode=="NoRestart"){if(ids.Length!=0){r.Failure="NoRestart process left a live job child.";TerminateJobObject(job,0xEE14);}break;}if(ids.Length!=1||ids[0]==pi.processId){if(timer.ElapsedMilliseconds-rootDoneAt<750){System.Threading.Thread.Sleep(25);continue;}r.Failure="SuccessExpectRestart requires exactly one non-root live job process; observed ["+String.Join(",",ids)+"].";TerminateJobObject(job,0xEE15);break;}string childStarted,childIdentity;string childPath=ProcessPathAndStart(ids[0],out childStarted,out childIdentity);if(!String.Equals(childPath,Path.GetFullPath(expectedRestartPath),StringComparison.OrdinalIgnoreCase)){r.Failure="SuccessExpectRestart child executable path mismatch.";TerminateJobObject(job,0xEE16);break;}r.RestartPid=(int)ids[0];r.RestartPath=childPath;r.RestartStartedUtc=childStarted;r.RestartFileIdentity=childIdentity;r.JobHandle=job;job=IntPtr.Zero;break;}}
        if(r.JobHandle==IntPtr.Zero){var emptyTimer=Stopwatch.StartNew();while(Active(job)!=0&&emptyTimer.ElapsedMilliseconds<5000)System.Threading.Thread.Sleep(25);r.SafeToCleanup=Active(job)==0;}else r.SafeToCleanup=false;r.CompletedUtc=DateTimeOffset.UtcNow.ToString("O");
      }catch(Exception e){string cleanup=null;if(assigned&&job!=IntPtr.Zero){if(!TerminateJobObject(job,0xEE13))cleanup="TerminateJobObject failed: "+new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;}else if(pi.process!=IntPtr.Zero){if(!TerminateProcess(pi.process,0xEE17)||WaitForSingleObject(pi.process,5000)!=WAIT_OBJECT_0)cleanup="Unassigned root process could not be terminated and joined.";}r.Failure=e.Message+(cleanup==null?"":"; "+cleanup);r.CompletedUtc=DateTimeOffset.UtcNow.ToString("O");}
      finally{if(pi.thread!=IntPtr.Zero)CloseHandle(pi.thread);if(pi.process!=IntPtr.Zero)CloseHandle(pi.process);if(job!=IntPtr.Zero)CloseHandle(job);if(so!=null)so.Dispose();if(se!=null)se.Dispose();}
      if(r.JobHandle==IntPtr.Zero)try{var strict=new UTF8Encoding(false,true);r.StandardOutput=File.Exists(stdoutPath)?strict.GetString(File.ReadAllBytes(stdoutPath)):"";r.StandardError=File.Exists(stderrPath)?strict.GetString(File.ReadAllBytes(stderrPath)):"";}catch(Exception e){r.Failure=(r.Failure==null?"":r.Failure+"; ")+"Unable to read strict UTF-8 bounded logs: "+e.Message;}return r;
    }
    public static bool CloseSession(MigrationContainedResult result){if(result==null||result.JobHandle==IntPtr.Zero)return true;IntPtr job=result.JobHandle;result.JobHandle=IntPtr.Zero;try{TerminateJobObject(job,0xEE20);var timer=Stopwatch.StartNew();while(timer.ElapsedMilliseconds<5000){if(Active(job)==0){result.SafeToCleanup=true;return true;}System.Threading.Thread.Sleep(25);}return false;}finally{CloseHandle(job);}}
}
'@

# This gate deliberately has no network client. Its only asset source is the two
# caller-supplied local directories copied into the run-owned evidence directory.
$script:OldMainExecutableName = 'BaiduBatchTransfer.App.exe'
# UTF-8 contract text: 知道综合工具.exe. Constructed from code points because Windows
# PowerShell 5.1 decodes UTF-8-without-BOM scripts through the active ANSI code page.
$script:ChineseDisplayName = "$([char]0x77E5)$([char]0x9053)$([char]0x7EFC)$([char]0x5408)$([char]0x5DE5)$([char]0x5177)"
$script:NewMainExecutableName = $script:ChineseDisplayName + '.exe'
$script:FixtureName = 'migration-gate-fixture.json'
$script:FixtureSignatureName = 'migration-gate-fixture.json.sig'
$script:ExpectedPublisherSpkiSha256 = 'f905188d79bb427c1a6eacc4bc9741d68a407228f643f6a50bfd791211da6cff'
$script:PublisherCertificatePath = Join-Path (Split-Path -Parent $PSScriptRoot) 'eng\ota\SuperPan.OtaPublisher.cer'
$script:MaximumFixtureBytes = 65536
$script:MaximumDriverOutputBytes = 1048576
$script:OwnedPaths = New-Object System.Collections.Generic.List[string]

function New-DerElement([byte]$Tag, [byte[]]$Content) {
    if ($Content.Length -lt 128) { [byte[]]$length = [byte]$Content.Length }
    else {
        $lengthBytes = New-Object Collections.Generic.List[byte]
        $remaining = [uint64]$Content.Length
        while ($remaining -gt 0) { $lengthBytes.Insert(0, [byte]($remaining -band 0xff)); $remaining = $remaining -shr 8 }
        [byte[]]$length = @([byte](0x80 -bor $lengthBytes.Count)) + $lengthBytes.ToArray()
    }
    $result = New-Object byte[] (1 + $length.Length + $Content.Length)
    $result[0] = $Tag
    [Array]::Copy($length, 0, $result, 1, $length.Length)
    [Array]::Copy($Content, 0, $result, 1 + $length.Length, $Content.Length)
    return ,$result
}

function Convert-OidToDerValue([string]$Oid) {
    if ($Oid -cnotmatch '^[0-2](\.(0|[1-9][0-9]*)){1,}$') { throw 'Certificate public-key OID is invalid.' }
    [uint64[]]$parts = @($Oid.Split('.') | ForEach-Object { [uint64]::Parse($_, [Globalization.CultureInfo]::InvariantCulture) })
    if ($parts[0] -lt 2 -and $parts[1] -gt 39) { throw 'Certificate public-key OID is invalid.' }
    [uint64[]]$values = @([uint64](40 * $parts[0] + $parts[1])) + @($parts | Select-Object -Skip 2)
    $encoded = New-Object Collections.Generic.List[byte]
    foreach ($value in $values) {
        $component = New-Object Collections.Generic.List[byte]
        $remaining = [uint64]$value
        do { $component.Insert(0, [byte]($remaining -band 0x7f)); $remaining = $remaining -shr 7 } while ($remaining -gt 0)
        for ($index = 0; $index -lt $component.Count - 1; $index++) { $component[$index] = [byte]($component[$index] -bor 0x80) }
        $encoded.AddRange($component)
    }
    return ,$encoded.ToArray()
}

function Get-CertificateSubjectPublicKeyInfo([Security.Cryptography.X509Certificates.X509Certificate2]$Certificate) {
    [byte[]]$oid = New-DerElement 0x06 (Convert-OidToDerValue $Certificate.PublicKey.Oid.Value)
    [byte[]]$parameters = $Certificate.PublicKey.EncodedParameters.RawData
    [byte[]]$algorithm = New-DerElement 0x30 ([byte[]]($oid + $parameters))
    [byte[]]$keyValue = $Certificate.PublicKey.EncodedKeyValue.RawData
    [byte[]]$bitString = New-DerElement 0x03 ([byte[]](@(0) + $keyValue))
    return New-DerElement 0x30 ([byte[]]($algorithm + $bitString))
}

function Get-CertificateSpkiSha256 {
    param([Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return (($sha.ComputeHash((Get-CertificateSubjectPublicKeyInfo $Certificate)) | ForEach-Object { $_.ToString('x2') }) -join '') }
    finally { $sha.Dispose() }
}

function Import-P256SubjectPublicKeyInfo([byte[]]$Der) {
    [byte[]]$prefix=0x30,0x59,0x30,0x13,0x06,0x07,0x2a,0x86,0x48,0xce,0x3d,0x02,0x01,0x06,0x08,0x2a,0x86,0x48,0xce,0x3d,0x03,0x01,0x07,0x03,0x42,0x00,0x04
    if($Der.Length-ne 91){throw 'License SPKI length is invalid.'}
    for($index=0;$index-lt $prefix.Length;$index++){if($Der[$index]-ne $prefix[$index]){throw 'License SPKI is not canonical P-256.'}}
    [byte[]]$blob=0x45,0x43,0x53,0x31,0x20,0,0,0
    [Array]::Resize([ref]$blob,72);[Array]::Copy($Der,27,$blob,8,64)
    return [Security.Cryptography.CngKey]::Import($blob,[Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
}

function Copy-DerEcdsaInteger([byte[]]$Signature,[ref]$Offset,[byte[]]$Output,[int]$TargetOffset) {
    if($Offset.Value+2-gt $Signature.Length-or $Signature[$Offset.Value]-ne 0x02){throw 'ECDSA signature integer is invalid.'}
    $length=[int]$Signature[$Offset.Value+1];$Offset.Value+=2
    if($length-lt 1-or $length-gt 33-or $Offset.Value+$length-gt $Signature.Length-or ($Signature[$Offset.Value]-band 0x80)-ne 0-or($length-gt 1-and$Signature[$Offset.Value]-eq 0-and($Signature[$Offset.Value+1]-band 0x80)-eq 0)){throw 'ECDSA signature integer is invalid.'}
    if($Signature[$Offset.Value]-eq 0){$Offset.Value++;$length--};if($length-gt 32){throw 'ECDSA signature integer is invalid.'}
    [Array]::Copy($Signature,$Offset.Value,$Output,$TargetOffset+32-$length,$length);$Offset.Value+=$length
}

function Convert-DerEcdsaSignatureToP1363([byte[]]$Signature) {
    if($Signature.Length-lt 8-or $Signature[0]-ne 0x30-or $Signature[1]-ge 0x80-or $Signature[1]-ne $Signature.Length-2){throw 'ECDSA signature encoding is invalid.'}
    $output=New-Object byte[] 64;$offset=2
    Copy-DerEcdsaInteger $Signature ([ref]$offset) $output 0;Copy-DerEcdsaInteger $Signature ([ref]$offset) $output 32
    if($offset-ne $Signature.Length){throw 'ECDSA signature encoding is invalid.'};return ,$output
}

function Assert-DerEcdsaSignatureFile {
    param([string]$SignaturePath)
    Convert-DerEcdsaSignatureToP1363 ([IO.File]::ReadAllBytes($SignaturePath))|Out-Null
}

function Assert-AssetFileName {
    param([string]$FileName, [string]$Label)
    if ([string]::IsNullOrWhiteSpace($FileName) -or [IO.Path]::IsPathRooted($FileName) -or
        [IO.Path]::GetFileName($FileName) -cne $FileName -or $FileName -in @('.', '..')) {
        throw "$Label must be one top-level local asset file name."
    }
    return $FileName
}

function Assert-CanonicalSignedFixture {
    param([string]$FixturePath, [string]$SignaturePath, [string]$CertificatePath, [string]$ExpectedSpki)
    foreach ($file in @($FixturePath, $SignaturePath, $CertificatePath)) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Signed fixture input is missing: $file" }
        Assert-NoReparsePoint -ExistingPath $file -Label 'Signed fixture input'
    }
    if((Get-Item -LiteralPath $FixturePath).Length-gt 1048576-or(Get-Item -LiteralPath $SignaturePath).Length-gt 16384-or(Get-Item -LiteralPath $CertificatePath).Length-gt 1048576){throw 'Signed fixture input exceeds its size bound.'}
    $bytes = [IO.File]::ReadAllBytes($FixturePath)
    if ($bytes.Length -lt 3 -or $bytes.Length -gt 65536 -or
        ($bytes.Length -ge 3 -and $bytes[0] -eq 0xef -and $bytes[1] -eq 0xbb -and $bytes[2] -eq 0xbf) -or
        $bytes[$bytes.Length - 1] -ne 0x0a -or $bytes -contains 0x0d) {
        throw 'Migration fixture must be bounded canonical UTF-8 with LF and no BOM.'
    }
    $utf8 = New-Object Text.UTF8Encoding($false, $true)
    try { $json = $utf8.GetString($bytes) } catch { throw 'Migration fixture is not canonical UTF-8.' }
    try { $fixture = $json | ConvertFrom-Json } catch { throw 'Migration fixture JSON is malformed.' }
    $expectedProperties = @('application_failure_hook_relative_path','application_failure_package_file','download_interrupted_exit_code','download_interrupted_marker','download_interrupted_stderr_sha256','download_interrupted_stdout_sha256','license_signing_key_id','license_signing_spki_base64','new_delta_file','new_full_file','new_main_sha256','old_full_file','old_portable_file','old_setup_file','schema_version','state_driver_file','state_driver_sha256')
    $actualProperties = @($fixture.PSObject.Properties.Name | Sort-Object)
    if (($actualProperties -join "`n") -cne ($expectedProperties -join "`n") -or $fixture.schema_version -ne 1) {
        throw 'Migration fixture schema is not exactly version 1.'
    }
    foreach ($name in @('application_failure_package_file','new_delta_file','new_full_file','old_full_file','old_portable_file','old_setup_file','state_driver_file')) {
        Assert-AssetFileName -FileName ([string]$fixture.$name) -Label $name | Out-Null
    }
    if ([string]$fixture.state_driver_sha256 -cnotmatch '^[0-9a-f]{64}$'-or[string]$fixture.new_main_sha256-cnotmatch'^[0-9a-f]{64}$') { throw 'Fixture executable SHA-256 pin is invalid.' }
    if([int64]$fixture.download_interrupted_exit_code-eq 0-or[string]$fixture.download_interrupted_marker-cnotmatch'^[A-Za-z0-9_.:-]{1,128}$'-or[string]$fixture.download_interrupted_stdout_sha256-cnotmatch'^[0-9a-f]{64}$'-or[string]$fixture.download_interrupted_stderr_sha256-cnotmatch'^[0-9a-f]{64}$'){throw 'Signed DownloadInterrupted failure fingerprint is invalid or empty.'}
    $hookRelative=[string]$fixture.application_failure_hook_relative_path
    if([string]::IsNullOrWhiteSpace($hookRelative)-or[IO.Path]::IsPathRooted($hookRelative)){throw 'Application failure hook path must be relative.'}
    Assert-StrictChildPath -Candidate (Join-Path 'C:\fixture-root' $hookRelative) -Root 'C:\fixture-root' -Label 'Application failure hook relative path'|Out-Null
    if ([string]$fixture.license_signing_key_id -cnotmatch '^[A-Za-z0-9._-]{1,128}$') { throw 'license_signing_key_id is invalid.' }
    try { $licenseSpki=[Convert]::FromBase64String([string]$fixture.license_signing_spki_base64) } catch { throw 'license_signing_spki_base64 is invalid.' }
    if ($licenseSpki.Length -ne 91 -or [Convert]::ToBase64String($licenseSpki) -cne [string]$fixture.license_signing_spki_base64) { throw 'license signing SPKI must be canonical P-256 SPKI DER base64.' }
    $licenseKey=$null
    try { $licenseKey=Import-P256SubjectPublicKeyInfo $licenseSpki } finally { if($licenseKey){$licenseKey.Dispose()} }
    $canonical = ConvertTo-CanonicalJson $fixture
    $canonicalBytes = $utf8.GetBytes($canonical + "`n")
    if (-not [Linq.Enumerable]::SequenceEqual([byte[]]$bytes, [byte[]]$canonicalBytes)) { throw 'Migration fixture JSON is not canonical.' }
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new([IO.File]::ReadAllBytes($CertificatePath))
    try {
        if ((Get-CertificateSpkiSha256 $certificate) -cne $ExpectedSpki) { throw 'Migration fixture publisher certificate SPKI mismatch.' }
        $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($certificate)
        try {
            $signature = [IO.File]::ReadAllBytes($SignaturePath)
            if (-not $rsa -or $signature.Length -ne ($rsa.KeySize / 8) -or
                -not $rsa.VerifyData($bytes, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)) {
                throw 'Migration fixture detached RSA SHA-256/PKCS1 signature is invalid.'
            }
        } finally { if ($rsa) { $rsa.Dispose() } }
    } finally { $certificate.Dispose() }
    return $fixture
}

function Assert-StrictChildPath {
    param(
        [Parameter(Mandatory = $true)][string]$Candidate,
        [Parameter(Mandatory = $true)][string]$Root,
        [string]$Label = 'Path'
    )

    $canonicalRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $canonicalCandidate = [IO.Path]::GetFullPath($Candidate)
    if (-not $canonicalCandidate.StartsWith($canonicalRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label must be a strict child of '$canonicalRoot': $canonicalCandidate"
    }
    return $canonicalCandidate
}

function Assert-NoReparsePoint {
    param([Parameter(Mandatory = $true)][string]$ExistingPath, [string]$Label = 'Path')

    $item = Get-Item -LiteralPath $ExistingPath -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label must not be a reparse point: $ExistingPath"
    }
}

function Assert-NoNonLoopbackNetwork {
    # Primary boundary: a non-admin token with every non-loopback adapter disabled
    # and no reachable non-loopback route. Polling is defense-in-depth, not a claimed fixed interval.
    $adapters = @(Get-NetAdapter -IncludeHidden -ErrorAction Stop | Where-Object {
        $_.ifIndex -ne 1 -and [string]$_.InterfaceDescription -notmatch '(?i)loopback'
    })
    $enabled = @($adapters | Where-Object { [string]$_.Status -notin @('Disabled','Not Present') })
    if ($enabled.Count -ne 0) { throw 'Every non-loopback network adapter must be disabled in the isolated migration environment.' }
    $reachableRoutes = @(
        Get-NetRoute -AddressFamily IPv4 -ErrorAction Stop
        Get-NetRoute -AddressFamily IPv6 -ErrorAction Stop
    ) | Where-Object { $_.ifIndex -ne 1 -and [string]$_.State -notin @('Invalid','Unreachable') }
    if (@($reachableRoutes).Count -ne 0) { throw 'The isolated migration environment must have no reachable IPv4 or IPv6 non-loopback route.' }
    return [ordered]@{ NonLoopbackAdapters=$adapters.Count; ReachableRoutes=0; VerifiedUtc=[DateTimeOffset]::UtcNow.ToString('O') }
}

function Assert-NoReparseAncestors {
    param([Parameter(Mandatory = $true)][string]$ExistingPath, [string]$Label = 'Path')
    $current = Get-Item -LiteralPath ([IO.Path]::GetFullPath($ExistingPath)) -Force
    while ($current) {
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Label has a reparse-point ancestor: $($current.FullName)"
        }
        $current = $current.Parent
    }
}

function Assert-SafeInputDirectory {
    param([Parameter(Mandatory = $true)][string]$InputPath, [Parameter(Mandatory = $true)][string]$Label)

    if (-not [IO.Path]::IsPathRooted($InputPath)) { throw "$Label must be an absolute path." }
    $canonical = [IO.Path]::GetFullPath($InputPath).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $canonical -PathType Container)) { throw "$Label does not exist: $canonical" }
    Assert-NoReparseAncestors -ExistingPath $canonical -Label $Label
    Assert-NoReparsePoint -ExistingPath $canonical -Label $Label
    return $canonical
}

function Assert-NewOwnedDirectory {
    param([Parameter(Mandatory = $true)][string]$Directory, [Parameter(Mandatory = $true)][string]$RunId)

    if (-not [IO.Path]::IsPathRooted($Directory)) { throw 'EvidenceDirectory must be an absolute path.' }
    $canonical = [IO.Path]::GetFullPath($Directory).TrimEnd('\')
    if (Test-Path -LiteralPath $canonical) { throw "EvidenceDirectory must not already exist: $canonical" }
    $parent = Split-Path -Parent $canonical
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) { throw "EvidenceDirectory parent does not exist: $parent" }
    Assert-NoReparseAncestors -ExistingPath $parent -Label 'EvidenceDirectory parent'
    New-Item -ItemType Directory -Path $canonical | Out-Null
    Assert-SafeProfileTopology -EvidencePath $canonical -IsolationConfirmed $true|Out-Null
    $script:OwnedPaths.Add($canonical)
    $owner = [ordered]@{ schema_version = 1; run_id = $RunId; created_utc = [DateTimeOffset]::UtcNow.ToString('O') }
    Write-CanonicalJson -Value $owner -Destination (Join-Path $canonical 'ownership.json') -CreateNew
    return $canonical
}

function Assert-IsolatedWindowsProfile {
    if (-not $IUnderstandThisIsAnIsolatedWindowsProfile) {
        throw 'The explicit isolation confirmation switch is required.'
    }
    $profile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $identity.User) { throw 'The current Windows identity has no SID.' }
    $principal = New-Object Security.Principal.WindowsPrincipal $identity
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'The migration gate must run as a non-Administrators isolated user token.'
    }
    $markerDirectory = Join-Path $profile $script:IsolationDirectoryName
    $markerPath = Join-Path $markerDirectory 'marker.json'
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        throw "The profile-bound isolation marker is missing: $markerPath"
    }
    Assert-NoReparsePoint -ExistingPath $markerDirectory -Label 'Isolation marker directory'
    Assert-NoReparsePoint -ExistingPath $markerPath -Label 'Isolation marker'
    try { $marker = Get-Content -LiteralPath $markerPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "The profile-bound isolation marker is malformed: $($_.Exception.Message)" }
    $canonicalProfile = [IO.Path]::GetFullPath($profile).TrimEnd('\')
    if (-not [string]::Equals([IO.Path]::GetFullPath([string]$marker.profile_path).TrimEnd('\'), $canonicalProfile, [StringComparison]::OrdinalIgnoreCase) -or
        [string]$marker.sid -cne $identity.User.Value) {
        throw 'The profile-bound isolation marker does not match the current profile and SID.'
    }
    try { $nonce = [Convert]::FromBase64String([string]$marker.nonce) }
    catch { throw 'The profile-bound isolation marker nonce is malformed.' }
    if ($nonce.Length -lt 32) { throw 'The profile-bound isolation marker nonce must contain at least 256 bits.' }
    foreach ($securedPath in @($markerDirectory, $markerPath)) {
        $acl = Get-Acl -LiteralPath $securedPath
        if (-not $acl.AreAccessRulesProtected) { throw 'The profile-bound isolation marker ACL must disable inheritance.' }
        $owner = New-Object Security.Principal.NTAccount -ArgumentList ([string]$acl.Owner)
        $ownerSid = $owner.Translate([Security.Principal.SecurityIdentifier]).Value
        if ($ownerSid -cne 'S-1-5-18' -and $ownerSid -cne 'S-1-5-32-544') {
            throw "The profile-bound isolation marker owner must be SYSTEM or Administrators, actual SID '$ownerSid'."
        }
        $writeProbe = $null
        try {
            if ($securedPath -ceq $markerDirectory) {
                $writeProbe = Join-Path $securedPath ("write-probe-$([Guid]::NewGuid().ToString('N')).tmp")
                $stream = [IO.File]::Open($writeProbe, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            } else {
                $stream = [IO.File]::Open($securedPath, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::None)
            }
            $stream.Dispose()
            throw 'The profile-bound isolation marker or its directory is writable by the isolated user.'
        } catch [UnauthorizedAccessException] {
            # Expected: marker was provisioned by the VM administrator and is read-only to this user.
        } finally {
            if ($writeProbe -and (Test-Path -LiteralPath $writeProbe)) {
                Remove-Item -LiteralPath $writeProbe -Force
            }
        }
    }
    return $canonicalProfile
}

function New-RunId {
    $bytes = New-Object byte[] 32
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes) } finally { $generator.Dispose() }
    return (-join @($bytes | ForEach-Object { $_.ToString('x2') }))
}

function New-RunChallenge {
    $bytes = New-Object byte[] 32
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes) } finally { $generator.Dispose() }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash=(($sha.ComputeHash($bytes)|ForEach-Object{$_.ToString('x2')}) -join '') } finally { $sha.Dispose() }
    return [ordered]@{ Base64=[Convert]::ToBase64String($bytes); Sha256=$hash }
}

function ConvertTo-CanonicalNode {
    param($Value)
    if ($null -eq $Value -or $Value -is [string] -or $Value -is [ValueType]) { return $Value }
    if ($Value -is [Collections.IDictionary]) {
        $ordered = [ordered]@{}
        foreach ($key in @($Value.Keys | Sort-Object)) { $ordered[[string]$key] = ConvertTo-CanonicalNode $Value[$key] }
        return $ordered
    }
    if ($Value -is [Collections.IEnumerable]) {
        return @($Value | ForEach-Object { ConvertTo-CanonicalNode $_ })
    }
    $object = [ordered]@{}
    foreach ($property in @($Value.PSObject.Properties | Sort-Object Name)) {
        $object[$property.Name] = ConvertTo-CanonicalNode $property.Value
    }
    return $object
}

function ConvertTo-CanonicalJson {
    param([Parameter(Mandatory = $true)]$Value)
    return ((ConvertTo-CanonicalNode $Value) | ConvertTo-Json -Depth 20 -Compress)
}

function Write-CanonicalJson {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Destination,
        [switch]$CreateNew
    )
    $json = ConvertTo-CanonicalJson $Value
    $mode = if ($CreateNew) { [IO.FileMode]::CreateNew } else { [IO.FileMode]::Create }
    $stream = [IO.File]::Open($Destination, $mode, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes($json + "`n")
        $stream.Write($bytes, 0, $bytes.Length)
    } finally { $stream.Dispose() }
}

function Copy-BoundedLockedAsset {
    param([string]$Source,[string]$Destination,[long]$MaximumBytes=1073741824,[long]$RemainingBudget=4294967296)
    $input=[IO.File]::Open($Source,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        if($input.Length-lt 1-or$input.Length-gt$MaximumBytes-or$input.Length-gt$RemainingBudget){throw 'Allowlisted asset size exceeds the per-file or remaining total budget.'}
        $output=[IO.File]::Open($Destination,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try{$buffer=New-Object byte[] 1048576;$copied=0L;while(($read=$input.Read($buffer,0,$buffer.Length))-gt 0){$copied+=$read;if($copied-gt$MaximumBytes-or$copied-gt$RemainingBudget){throw 'Allowlisted asset copy exceeded the remaining total budget.'};$output.Write($buffer,0,$read)}}finally{$output.Dispose()}
        if((Get-Item -LiteralPath $Destination).Length-ne$input.Length){throw 'Allowlisted asset copy length mismatch.'}
        $input.Position=0;$sourceHash=Get-StreamingSha256 -File $Source;$destinationHash=Get-StreamingSha256 -File $Destination
        if($sourceHash-cne$destinationHash){throw 'Allowlisted asset copy hash mismatch.'}
        return [ordered]@{Length=$input.Length;Sha256=$sourceHash}
    } finally {$input.Dispose()}
}

function Open-ArtifactReadLock {
    param([string]$File,[string]$Label)
    Assert-NoReparseAncestors -ExistingPath $File -Label $Label
    Get-FileHardLinkCount -File $File|Out-Null
    $stream=[IO.File]::Open($File,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        $sha=[Security.Cryptography.SHA256]::Create()
        try{$hash=(($sha.ComputeHash($stream)|ForEach-Object{$_.ToString('x2')})-join '')}finally{$sha.Dispose()}
        $identity=[MigrationFileIdentity]::Get($stream)
        $stream.Position=0
        return [pscustomobject]@{Stream=$stream;Path=[IO.Path]::GetFullPath($File);Identity=$identity;Length=$stream.Length;Sha256=$hash;Label=$Label}
    } catch {$stream.Dispose();throw}
}

function Assert-ArtifactReadLockUnchanged {
    param($Lock)
    $Lock.Stream.Position=0;$sha=[Security.Cryptography.SHA256]::Create()
    try{$hash=(($sha.ComputeHash($Lock.Stream)|ForEach-Object{$_.ToString('x2')})-join '')}finally{$sha.Dispose()}
    if($Lock.Stream.Length-ne$Lock.Length-or$hash-cne$Lock.Sha256-or[MigrationFileIdentity]::Get($Lock.Stream)-cne$Lock.Identity){throw "$($Lock.Label) changed while locked."}
    $Lock.Stream.Position=0
}

function Copy-FromArtifactReadLock {
    param($Lock,[string]$Destination,[long]$MaximumBytes)
    if($MaximumBytes-lt 1-or$MaximumBytes-gt$Lock.Length){throw 'Locked copy byte count is invalid.'}
    $Lock.Stream.Position=0;$output=[IO.File]::Open($Destination,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try{$remaining=$MaximumBytes;$buffer=New-Object byte[] 1048576;while($remaining-gt 0){$read=$Lock.Stream.Read($buffer,0,[Math]::Min([long]$buffer.Length,$remaining));if($read-le 0){throw 'Locked source ended during copy.'};$output.Write($buffer,0,$read);$remaining-=$read}}finally{$output.Dispose();$Lock.Stream.Position=0}
}

function Resolve-FixturePath {
    param([string]$Root, [string]$RelativePath, [string]$Label, [switch]$Directory, [switch]$AllowRoot)
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [IO.Path]::IsPathRooted($RelativePath)) {
        throw "$Label must be a non-empty relative path."
    }
    $candidate = [IO.Path]::GetFullPath((Join-Path $Root $RelativePath))
    if ($AllowRoot -and [string]::Equals($candidate.TrimEnd('\'), [IO.Path]::GetFullPath($Root).TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
        $resolved = $candidate
    } else {
        $resolved = Assert-StrictChildPath -Candidate $candidate -Root $Root -Label $Label
    }
    $pathType = if ($Directory) { 'Container' } else { 'Leaf' }
    if (-not (Test-Path -LiteralPath $resolved -PathType $pathType)) { throw "$Label does not exist: $resolved" }
    Assert-NoReparsePoint -ExistingPath $resolved -Label $Label
    return $resolved
}

function Assert-FileSha256 {
    param([string]$File, [string]$Expected, [string]$Label)
    if ($Expected -cnotmatch '^[0-9a-f]{64}$') { throw "$Label SHA-256 is not canonical lowercase hex." }
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $File).Hash.ToLowerInvariant()
    if ($actual -cne $Expected) { throw "$Label SHA-256 mismatch." }
}

function Get-StreamingSha256 {
    param([string]$File)
    $stream = [IO.File]::Open($File, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return (($sha.ComputeHash($stream) | ForEach-Object { $_.ToString('x2') }) -join '') }
    finally { $sha.Dispose(); $stream.Dispose() }
}

function Get-Utf8Sha256 {
    param([string]$Text)
    $sha=[Security.Cryptography.SHA256]::Create()
    try { return (($sha.ComputeHash((New-Object Text.UTF8Encoding($false,$true)).GetBytes($Text))|ForEach-Object{$_.ToString('x2')}) -join '') }
    finally { $sha.Dispose() }
}

function Get-FileHardLinkCount {
    param([string]$File)
    $output = @(& fsutil.exe hardlink list $File 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Unable to inspect hard links for '$File'." }
    $links = @($output | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    if ($links.Count -ne 1) { throw "State file has $($links.Count) hard links: $File" }
    return 1
}

function Assert-ExactProfileFile {
    param([string]$Actual, [string]$Expected, [long]$MaximumBytes, [string]$Label)
    $canonicalActual = [IO.Path]::GetFullPath($Actual)
    $canonicalExpected = [IO.Path]::GetFullPath($Expected)
    if (-not [string]::Equals($canonicalActual, $canonicalExpected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label path is not the fixed isolated-profile path."
    }
    if (-not (Test-Path -LiteralPath $canonicalActual -PathType Leaf)) { throw "$Label is missing." }
    Assert-NoReparseAncestors -ExistingPath $canonicalActual -Label $Label
    $info = Get-Item -LiteralPath $canonicalActual -Force
    if ($info.Length -lt 1 -or $info.Length -gt $MaximumBytes) { throw "$Label size is outside the allowed bound." }
    Get-FileHardLinkCount -File $canonicalActual | Out-Null
    return $info
}

function Assert-StateEvidence {
    param([string]$StateJson, [string]$StateJsonPath, [string]$ProfileRoot, [string]$RunId, [string]$ExpectedChallengeSha256, [DateTimeOffset]$WindowStartedUtc, [DateTimeOffset]$WindowCompletedUtc)
    if ([string]::IsNullOrWhiteSpace($StateJson)) {
        if ([string]::IsNullOrWhiteSpace($StateJsonPath)) { throw 'State evidence JSON is missing.' }
        $StateJson = Get-Content -LiteralPath $StateJsonPath -Raw -Encoding UTF8
    }
    try { $state = $StateJson | ConvertFrom-Json } catch { throw 'State evidence JSON is malformed.' }
    if ([string]$state.run_id -cne $RunId -or [string]$state.challenge_sha256 -cne $ExpectedChallengeSha256) { throw 'State query run id or challenge does not match this invocation.' }
    $queryTime=[DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParseExact([string]$state.query_completed_utc,'O',[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind,[ref]$queryTime) -or
        $queryTime -lt $WindowStartedUtc -or $queryTime -gt $WindowCompletedUtc) { throw 'State query time is outside this invocation window.' }
    # The fixed-production-signed, signed-fixture-hash-pinned driver is the TCB for
    # SQLite row selection. This detects replay/bugs, not a malicious trusted TCB.
    foreach ($kind in @('account','settings','task')) {
        $markerName = $kind + '_marker'
        $countName = $kind + '_marker_count'
        $hashName = $kind + '_row_sha256'
        $rowName = $kind + '_row'
        $canonicalRow = ConvertTo-CanonicalJson $state.$rowName
        if (-not $state.$rowName -or [string]$state.$markerName -cne $RunId -or [string]$state.$rowName.marker -cne $RunId -or [int]$state.$countName -ne 1 -or
            [string]$state.$hashName -cne (Get-Utf8Sha256 $canonicalRow)) {
            throw "$kind marker must be one unique canonical row bound to the current run id and row hash."
        }
    }
    $expectedDatabase = Join-Path $ProfileRoot 'AppData\Local\BaiduBatchTransfer\data\baidu-batch-transfer.db'
    $expectedLicense = Join-Path $ProfileRoot 'AppData\Local\SuperPan\Licensing\v1\licenses.dat'
    $database = Assert-ExactProfileFile -Actual ([string]$state.database_path) -Expected $expectedDatabase -MaximumBytes 536870912 -Label 'Database state file'
    $license = Assert-ExactProfileFile -Actual ([string]$state.license_path) -Expected $expectedLicense -MaximumBytes 1048576 -Label 'License state file'
    $databaseHash = Get-StreamingSha256 -File $database.FullName
    $licenseHash = Get-StreamingSha256 -File $license.FullName
    if ([string]$state.database_file_sha256 -cne $databaseHash -or [long]$state.database_length -ne $database.Length) {
        throw 'Database state file hash or length is not independently bound to driver evidence.'
    }
    if ([string]$state.license_file_sha256 -cne $licenseHash -or [long]$state.license_length -ne $license.Length) {
        throw 'License state file hash or length is not independently bound to driver evidence.'
    }
    if ($state.production_trust_chain -ne $true -or [string]$state.signed_payload_sha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw 'The trusted driver did not verify the production-chain license signature.'
    }
    $expiry = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParseExact([string]$state.expires_at_utc, 'O', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind, [ref]$expiry) -or
        $expiry -le [DateTimeOffset]::UtcNow) { throw 'The production-chain isolation license is expired or malformed.' }
    $activation = [Guid]::Empty
    if (-not [Guid]::TryParseExact([string]$state.activation_request_id, 'D', [ref]$activation)) { throw 'activation_request_id is invalid.' }
    if ($state.environment_authorization_is_auxiliary -ne $true -or $state.network_accessed -ne $false -or
        [string]$state.license_source -cne 'preissued-local-production-chain-license') {
        throw 'License state evidence is not an offline preissued production-chain authorization.'
    }
    return $state
}

function Assert-StateEvidencePreserved {
    param($Before, $After)
    foreach ($field in @(
        'account_marker','account_marker_count','account_row_sha256','settings_marker','settings_marker_count','settings_row_sha256',
        'task_marker','task_marker_count','task_row_sha256','database_path','database_file_sha256','database_length',
        'license_path','license_file_sha256','license_length','signed_payload_sha256','expires_at_utc','activation_request_id','license_source')) {
        if ([string]$Before.$field -cne [string]$After.$field) { throw "Preserved state field changed: $field" }
    }
    if ($After.reactivation_required -ne $false) { throw 'The upgraded application required reactivation.' }
}

function Assert-IndependentLicenseAudit {
    param($State,[string]$StateJson,[string]$ProfileRoot,$Fixture,[string]$FixtureJson)
    if(-not$State-and$StateJson){$State=$StateJson|ConvertFrom-Json};if(-not$Fixture-and$FixtureJson){$Fixture=$FixtureJson|ConvertFrom-Json}
    $licensePath=Join-Path $ProfileRoot 'AppData\Local\SuperPan\Licensing\v1\licenses.dat'
    Assert-ExactProfileFile -Actual $licensePath -Expected $licensePath -MaximumBytes 1048576 -Label 'License state file'|Out-Null
    $entropy=[Security.Cryptography.SHA256]::Create()
    try{$purposeHash=$entropy.ComputeHash([Text.Encoding]::UTF8.GetBytes('SuperPan.Licensing.State.v1'))}finally{$entropy.Dispose()}
    try{$plaintext=[Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($licensePath),$purposeHash,[Security.Cryptography.DataProtectionScope]::CurrentUser)}catch{throw 'License DPAPI CurrentUser audit failed.'}
    try {
        if($plaintext.Length-lt 2-or $plaintext.Length-gt 786432){throw 'License plaintext size is invalid.'}
        $utf8=New-Object Text.UTF8Encoding($false,$true)
        try{$document=$utf8.GetString($plaintext)|ConvertFrom-Json}catch{throw 'License plaintext JSON is invalid.'}
        if($document.version-ne 1){throw 'License state version is invalid.'}
        $credentials=@($document.credentials|Where-Object{[string]$_.activation_request_id-ceq[string]$State.activation_request_id})
        if($credentials.Count-ne 1){throw 'License activation request id is not unique in DPAPI state.'}
        $leases=@($credentials[0].leases|Where-Object{[string]$_.software_id-ceq'8270e8d7-1791-487d-ad76-0da7a0cbed12'-and[string]$_.machine_hash-ceq[string]$State.machine_hash})
        if($leases.Count-ne 1){throw 'License ProductId/machine lease is not unique in DPAPI state.'}
        $lease=$leases[0];$expires=[DateTimeOffset]$lease.expires_at;$leaseUntil=[DateTimeOffset]$lease.lease_until
        if($expires-le[DateTimeOffset]::UtcNow-or $leaseUntil-le[DateTimeOffset]::UtcNow-or $leaseUntil-gt$expires-or $expires.ToString('O')-cne[string]$State.expires_at_utc-or $leaseUntil.ToString('O')-cne[string]$State.lease_until_utc-or[string]$lease.package_name-cne[string]$State.package_name){throw 'License expiry/lease/package audit failed.'}
        try{$envelope=[string]$lease.signed_payload|ConvertFrom-Json;$payload=[Convert]::FromBase64String([string]$envelope.payload);$signature=[Convert]::FromBase64String([string]$envelope.signature)}catch{throw 'Stored signed license envelope is malformed.'}
        if([string]$envelope.key_id-cne[string]$Fixture.license_signing_key_id-or (Get-Utf8Sha256 ([string]$lease.signed_payload))-cne[string]$State.signed_payload_sha256){throw 'Stored license key id or signed-envelope hash audit failed.'}
        $cng=$null;$ecdsa=$null
        try{$cng=Import-P256SubjectPublicKeyInfo ([Convert]::FromBase64String([string]$Fixture.license_signing_spki_base64));$ecdsa=[Security.Cryptography.ECDsaCng]::new($cng);$ecdsa.HashAlgorithm=[Security.Cryptography.CngAlgorithm]::Sha256;if(-not$ecdsa.VerifyData($payload,(Convert-DerEcdsaSignatureToP1363 $signature))){throw 'Stored license production P-256 signature is invalid.'}}
        finally{if($ecdsa){$ecdsa.Dispose()};if($cng){$cng.Dispose()}}
        $claims=$utf8.GetString($payload)|ConvertFrom-Json
        $claimsExpires=[DateTimeOffset]$claims.expires_at;$claimsLease=[DateTimeOffset]$claims.lease_until;$serverTime=[DateTimeOffset]$claims.server_time
        if([int]$claims.protocol_version-ne 1-or[string]$claims.request_id-cne[string]$State.activation_request_id-or[string]$claims.software_id-cne'8270e8d7-1791-487d-ad76-0da7a0cbed12'-or[string]$claims.machine_hash-cne[string]$State.machine_hash-or[string]$claims.package_name-cne[string]$lease.package_name-or[string]$claims.card_id-cne[string]$lease.card_id-or$claimsExpires-ne$expires-or$claimsLease-ne$leaseUntil-or$claimsLease-le[DateTimeOffset]::UtcNow-or$claimsLease-gt$claimsExpires-or$serverTime-gt[DateTimeOffset]::UtcNow.AddMinutes(5)-or$serverTime-lt[DateTimeOffset]::UnixEpoch-or[string]$claims.decision-cne'allow'-or[string]$claims.code-cne'ok'){throw 'Signed license protocol/request/software/machine/package/time/decision bindings are invalid.'}
        return [ordered]@{KeyId=[string]$envelope.key_id;SoftwareId=[string]$claims.software_id;MachineHash=[string]$claims.machine_hash;RequestId=[string]$claims.request_id;ExpiresAtUtc=$expires.ToString('O');LeaseUntilUtc=$leaseUntil.ToString('O')}
    } finally { if($plaintext){[Array]::Clear($plaintext,0,$plaintext.Length)} }
}

function Assert-FileIsExactPrefix {
    param([string]$Source,[string]$Prefix)
    $sourceStream=[IO.File]::OpenRead($Source);$prefixStream=[IO.File]::OpenRead($Prefix)
    try {
        if($prefixStream.Length-le 0-or$prefixStream.Length-ge$sourceStream.Length){throw 'Partial artifact length is not a strict source prefix.'}
        $sourceBuffer=New-Object byte[] 65536;$prefixBuffer=New-Object byte[] 65536
        while(($prefixRead=$prefixStream.Read($prefixBuffer,0,$prefixBuffer.Length))-gt 0){$sourceRead=$sourceStream.Read($sourceBuffer,0,$prefixRead);if($sourceRead-ne$prefixRead){throw 'Partial artifact is not a source prefix.'};for($i=0;$i-lt$prefixRead;$i++){if($sourceBuffer[$i]-ne$prefixBuffer[$i]){throw 'Partial artifact is not a source prefix.'}}}
    } finally {$sourceStream.Dispose();$prefixStream.Dispose()}
}

function Assert-DownloadInterruptedFingerprint {
    param([int]$ExitCode,[string]$StdoutPath,[string]$StderrPath,[string]$ExpectedStdoutSha256,[string]$ExpectedStderrSha256,[int]$ExpectedExitCode,[string]$ExpectedMarker,[string]$SourceLeaf,[string]$PartialLeaf)
    if($ExitCode-ne$ExpectedExitCode-or$ExitCode-eq 0){throw 'DownloadInterrupted exit code does not match the signed Velopack fingerprint.'}
    $strict=New-Object Text.UTF8Encoding($false,$true)
    try{$stdoutBytes=[IO.File]::ReadAllBytes($StdoutPath);$stderrBytes=[IO.File]::ReadAllBytes($StderrPath);$stdout=$strict.GetString($stdoutBytes);$stderr=$strict.GetString($stderrBytes)}catch{throw 'DownloadInterrupted output is not strict UTF-8.'}
    if((Get-StreamingSha256 $StdoutPath)-cne$ExpectedStdoutSha256-or(Get-StreamingSha256 $StderrPath)-cne$ExpectedStderrSha256){throw 'DownloadInterrupted output hash does not match the signed Velopack fingerprint.'}
    $combined=$stdout+"`n"+$stderr
    foreach($required in @($ExpectedMarker,$SourceLeaf,$PartialLeaf)){if([string]::IsNullOrWhiteSpace($required)-or-not$combined.Contains($required)){throw 'DownloadInterrupted output is not bound to the exact partial/source leaf and signed marker.'}}
}

function Assert-PublisherExecutable {
    param([string]$Executable, [string]$ExpectedSpki = $script:ExpectedPublisherSpkiSha256)
    $signature = Get-AuthenticodeSignature -LiteralPath $Executable
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or -not $signature.SignerCertificate) {
        throw "Executable is not validly Authenticode signed: $Executable"
    }
    if ((Get-CertificateSpkiSha256 $signature.SignerCertificate) -cne $ExpectedSpki) {
        throw "Executable signer SPKI does not match the fixed production OTA publisher: $Executable"
    }
    Assert-PeMachineAmd64 -Executable $Executable -Label ([IO.Path]::GetFileName($Executable))
}

function Assert-PeMachineAmd64 {
    param([string]$Executable, [string]$Label)
    $stream = [IO.File]::OpenRead($Executable)
    $reader = New-Object IO.BinaryReader($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5a4d) { throw "$Label is not a PE executable." }
        $stream.Position = 0x3c
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0x40 -or $peOffset -gt $stream.Length - 6) { throw "$Label has an invalid PE header offset." }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0x8664) {
            throw "$Label is not an x64 PE executable."
        }
    } finally { $reader.Dispose(); $stream.Dispose() }
}

function Assert-CanonicalSemverText {
    param([string]$Actual,[string]$Expected)
    if($Actual-cnotmatch'^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'-or$Actual-cne$Expected){throw "Version is not exact canonical semver ${Expected}: $Actual"}
}

function Assert-ExactThreePartVersion {
    param([string]$Executable,[string]$Expected)
    $version=[string][Diagnostics.FileVersionInfo]::GetVersionInfo($Executable).ProductVersion
    Assert-CanonicalSemverText -Actual $version -Expected $Expected
}

function Invoke-BoundedDriver {
    param([string]$Driver, [string[]]$Arguments, [string]$WorkingDirectory, [int]$TimeoutSeconds = 120)
    Assert-NoNonLoopbackNetwork|Out-Null
    $token=[Guid]::NewGuid().ToString('N');$stdoutPath=Join-Path $WorkingDirectory ".driver-$token.stdout";$stderrPath=Join-Path $WorkingDirectory ".driver-$token.stderr"
    $result=[MigrationContainedRunner]::Run($Driver,$Arguments,$WorkingDirectory,$stdoutPath,$stderrPath,($TimeoutSeconds*1000),$script:MaximumDriverOutputBytes,'NoRestart',$null)
    Assert-NoNonLoopbackNetwork|Out-Null
    if($result.Failure-or-not$result.SafeToCleanup){throw "Trusted state driver containment failed: $($result.Failure)"}
    if ($result.ExitCode -ne 0) { throw "Trusted state driver failed with exit code $($result.ExitCode): $($result.StandardError)" }
    if (-not [string]::IsNullOrEmpty($result.StandardError)) { throw 'Trusted state driver wrote unexpected stderr.' }
    [byte[]]$outputBytes = (New-Object Text.UTF8Encoding($false, $true)).GetBytes($result.StandardOutput)
    if ($outputBytes.Length -lt 3 -or $outputBytes.Length -gt $script:MaximumDriverOutputBytes -or $outputBytes[$outputBytes.Length - 1] -ne 0x0a -or $outputBytes -contains 0x0d) { throw 'Trusted state driver stdout must be one bounded canonical UTF-8/LF JSON record.' }
    try { $record=$result.StandardOutput|ConvertFrom-Json } catch { throw 'Trusted state driver stdout is malformed JSON.' }
    if ((ConvertTo-CanonicalJson $record) + "`n" -cne $result.StandardOutput) { throw 'Trusted state driver stdout JSON is not canonical.' }
    return $result
}

function Invoke-CheckedProcess {
    param([string]$File, [string[]]$Arguments, [string]$WorkingDirectory, [int]$TimeoutSeconds = 120, [switch]$ExpectFailure)
    Assert-NoNonLoopbackNetwork|Out-Null
    $token=[Guid]::NewGuid().ToString('N');$result=[MigrationContainedRunner]::Run($File,$Arguments,$WorkingDirectory,(Join-Path $WorkingDirectory ".process-$token.stdout"),(Join-Path $WorkingDirectory ".process-$token.stderr"),($TimeoutSeconds*1000),$script:MaximumDriverOutputBytes,'NoRestart',$null)
    Assert-NoNonLoopbackNetwork|Out-Null
    if($result.Failure-or-not$result.SafeToCleanup){throw "Contained process safety enforcement failed: $($result.Failure)"}
    if (-not $ExpectFailure -and $result.ExitCode -ne 0) { throw "Process failed ($($result.ExitCode)): $File`n$($result.StandardOutput)$($result.StandardError)" }
    if ($ExpectFailure -and $result.ExitCode -eq 0) { throw "Injected failure unexpectedly succeeded: $File" }
    return [pscustomobject]@{ ExitCode=$result.ExitCode; Output=($result.StandardOutput+$result.StandardError) }
}

function Resolve-AssetFile {
    param([string]$Root, [string]$FileName, [string]$Label)
    Assert-AssetFileName -FileName $FileName -Label $Label | Out-Null
    return Resolve-FixturePath -Root $Root -RelativePath $FileName -Label $Label
}

function Expand-LocalPortableAsset {
    param([string]$Archive, [string]$Destination)
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        if($zip.Entries.Count-gt 4096){throw 'Portable archive entry count exceeds 4096.'}
        $names=New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase);$declaredTotal=0L
        foreach ($entry in $zip.Entries) {
            $name = ([string]$entry.FullName).Replace('/', '\')
            $normalized=$name.Normalize([Text.NormalizationForm]::FormC)
            $segments=@($normalized.TrimEnd('\').Split('\'))
            $badSegments=@($segments|Where-Object{$_-in@('.','..')-or([IO.Path]::GetFileNameWithoutExtension($_)-match'(?i)^(con|prn|aux|nul|com[1-9]|lpt[1-9])$')})
            if ([string]::IsNullOrWhiteSpace($normalized) -or [IO.Path]::IsPathRooted($normalized)-or$normalized-cne$name-or$normalized.Contains(':')-or$badSegments.Count-ne 0) { throw 'Portable archive contains an invalid canonical path.' }
            if(-not$names.Add($normalized.TrimEnd('\'))){throw 'Portable archive contains a duplicate case-insensitive path.'}
            if($entry.Length-gt 536870912){throw 'Portable archive entry exceeds 512 MiB.'};$declaredTotal+=$entry.Length;if($declaredTotal-gt 2147483648){throw 'Portable archive total exceeds 2 GiB.'}
            if($entry.Length-gt 0-and($entry.CompressedLength-le 0-or($entry.Length/[double]$entry.CompressedLength)-gt 200)){throw 'Portable archive compression ratio exceeds 200.'}
            $target = Assert-StrictChildPath -Candidate (Join-Path $Destination $name) -Root $Destination -Label 'Portable archive entry'
            if ([string]::IsNullOrEmpty($entry.Name)) {
                New-Item -ItemType Directory -Path $target -Force | Out-Null
            } else {
                $parent = Split-Path -Parent $target
                if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
                $input=$entry.Open();$output=[IO.File]::Open($target,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
                try{$written=0L;$buffer=New-Object byte[] 1048576;while(($read=$input.Read($buffer,0,$buffer.Length))-gt 0){$written+=$read;if($written-gt 536870912-or$written-gt$entry.Length){throw 'Portable archive streamed entry exceeds its bound.'};$output.Write($buffer,0,$read)};if($written-ne$entry.Length){throw 'Portable archive entry length mismatch.'}}finally{$output.Dispose();$input.Dispose()}
            }
        }
    } finally { $zip.Dispose() }
    foreach ($item in @(Get-ChildItem -LiteralPath $Destination -Force -Recurse)) { Assert-NoReparsePoint -ExistingPath $item.FullName -Label 'Portable archive output' }
}

function Assert-PackageContainsPinnedMain {
    param([string]$Package,[string]$ExpectedFileName,[string]$ExpectedSha256)
    $zip=[IO.Compression.ZipFile]::OpenRead($Package)
    try{$matches=@($zip.Entries|Where-Object{[string]::Equals([IO.Path]::GetFileName(([string]$_.FullName).Replace('/','\')),$ExpectedFileName,[StringComparison]::Ordinal)})
        if($matches.Count-ne 1-or$matches[0].Length-le 0-or$matches[0].Length-gt 536870912){throw 'Signed candidate package must contain exactly one bounded expected main executable.'}
        $stream=$matches[0].Open();$sha=[Security.Cryptography.SHA256]::Create();try{$actual=(($sha.ComputeHash($stream)|ForEach-Object{$_.ToString('x2')})-join '')}finally{$sha.Dispose();$stream.Dispose()}
        if($actual-cne$ExpectedSha256){throw 'Signed candidate package main executable hash does not match the signed fixture pin.'}
    }finally{$zip.Dispose()}
}

function Get-ShortcutTarget {
    param([string]$Shortcut)
    $shell = New-Object -ComObject WScript.Shell
    return [IO.Path]::GetFullPath([string]$shell.CreateShortcut($Shortcut).TargetPath)
}

function Assert-OldVersionStillLaunches {
    param([string]$OldExecutable,[string]$RunId,[string]$EvidenceRoot,$ArtifactLock)
    Assert-ArtifactReadLockUnchanged $ArtifactLock
    $token=[Guid]::NewGuid().ToString('N');$run=$null
    try{$run=[MigrationContainedRunner]::Run($OldExecutable,@('--migration-gate-rollback',$RunId),(Split-Path -Parent $OldExecutable),(Join-Path $EvidenceRoot "rollback-$token.stdout"),(Join-Path $EvidenceRoot "rollback-$token.stderr"),45000,1048576,'ExpectRootAlive',$OldExecutable);if($run.Failure-or$run.JobHandle-eq[IntPtr]::Zero-or[string]$run.RestartFileIdentity-cne$ArtifactLock.Identity){throw "Old version liveness proof failed: $($run.Failure)"};return [ordered]@{Pid=$run.RestartPid;Path=$run.RestartPath;StartedUtc=$run.RestartStartedUtc;FileIdentity=$run.RestartFileIdentity;JobOwned=$true}}
    finally{if($run-and$run.JobHandle-ne[IntPtr]::Zero){if(-not[MigrationContainedRunner]::CloseSession($run)){throw 'Old-version liveness job did not terminate cleanly.'}}}
}

function Get-UninstallEntry {
    param([string]$InstallRoot)
    $roots = @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*')
    $matches = @($roots | ForEach-Object { Get-ItemProperty -Path $_ -ErrorAction SilentlyContinue } |
        Where-Object { [string]$_.DisplayName -ceq $script:ChineseDisplayName })
    if ($matches.Count -ne 1) { throw "Expected one exact Chinese UninstallEntry, found $($matches.Count)." }
    $entry = $matches[0]
    $expectedUpdater = Join-Path $InstallRoot 'Update.exe'
    if (-not[string]::Equals([IO.Path]::GetFullPath([string]$entry.InstallLocation).TrimEnd('\'),[IO.Path]::GetFullPath($InstallRoot).TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)) {
        throw 'UninstallEntry InstallLocation is not the isolated product install root.'
    }
    $uninstallString = [string]$entry.UninstallString
    Assert-UninstallCommandLine -CommandLine $uninstallString -ExpectedUpdater $expectedUpdater
    return [ordered]@{ Key = [string]$entry.PSPath; DisplayName = [string]$entry.DisplayName; InstallLocation = [string]$entry.InstallLocation; UninstallString = $uninstallString }
}

function Assert-UninstallCommandLine {
    param([string]$CommandLine,[string]$ExpectedUpdater)
    $arguments=@([MigrationNativeCommandLine]::Split($CommandLine))
    if($arguments.Count-ne 2-or-not[string]::Equals([IO.Path]::GetFullPath([string]$arguments[0]),[IO.Path]::GetFullPath($ExpectedUpdater),[StringComparison]::OrdinalIgnoreCase)-or[string]$arguments[1]-cne'--uninstall'){throw 'Uninstall command line is not the exact installed Update.exe --uninstall contract.'}
}

function Assert-CleanSetupSurfaces {
    param([string]$Profile)
    foreach ($shortcut in @(
        (Join-Path $Profile "Desktop\$($script:ChineseDisplayName).lnk"),
        (Join-Path $Profile "Desktop\BaiduBatchTransfer.App.lnk"),
        (Join-Path $Profile "AppData\Roaming\Microsoft\Windows\Start Menu\Programs\$($script:ChineseDisplayName).lnk"),
        (Join-Path $Profile "AppData\Roaming\Microsoft\Windows\Start Menu\Programs\BaiduBatchTransfer.App.lnk"))) {
        if (Test-Path -LiteralPath $shortcut) { throw "The isolated profile contains a pre-existing product shortcut: $shortcut" }
    }
    $existing = @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' |
        ForEach-Object { Get-ItemProperty -Path $_ -ErrorAction SilentlyContinue } |
        Where-Object { [string]$_.DisplayName -in @($script:ChineseDisplayName, 'BaiduBatchTransfer.App') })
    if ($existing.Count -ne 0) { throw 'The isolated profile contains a pre-existing product uninstall entry.' }
}

function Assert-SetupSurfaces {
    param([string]$InstallRoot, [string]$CurrentMain, [string]$RootLauncher, [string]$LegacyLauncher, [string]$Profile)
    $desktopShortcut = Join-Path $Profile ("Desktop\$($script:ChineseDisplayName).lnk")
    $startMenuShortcut = Join-Path $Profile ("AppData\Roaming\Microsoft\Windows\Start Menu\Programs\$($script:ChineseDisplayName).lnk")
    foreach ($required in @($CurrentMain, $RootLauncher, $LegacyLauncher, $desktopShortcut, $startMenuShortcut)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required Setup migration surface is missing: $required" }
    }
    $desktopTarget = Get-ShortcutTarget $desktopShortcut
    $startMenuTarget = Get-ShortcutTarget $startMenuShortcut
    $exactCurrent = [IO.Path]::GetFullPath($CurrentMain)
    if (-not[string]::Equals($desktopTarget,$exactCurrent,[StringComparison]::OrdinalIgnoreCase)-or-not[string]::Equals($startMenuTarget,$exactCurrent,[StringComparison]::OrdinalIgnoreCase)) { throw 'Chinese shortcuts must target the exact current Chinese executable.' }
    foreach($legacyShortcut in @((Join-Path $Profile 'Desktop\BaiduBatchTransfer.App.lnk'),(Join-Path $Profile 'AppData\Roaming\Microsoft\Windows\Start Menu\Programs\BaiduBatchTransfer.App.lnk'))){if(Test-Path -LiteralPath $legacyShortcut){throw 'Legacy-named shortcut remains after migration.'}}
    $legacyUninstall=@('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*'|ForEach-Object{Get-ItemProperty -Path $_ -ErrorAction SilentlyContinue}|Where-Object{[string]$_.DisplayName-ceq'BaiduBatchTransfer.App'})
    if($legacyUninstall.Count-ne 0){throw 'Legacy-named uninstall entry remains after migration.'}
    $uninstall = Get-UninstallEntry -InstallRoot $InstallRoot
    if ([string]$uninstall.DisplayName -cne $script:ChineseDisplayName) { throw 'UninstallEntry does not use the Chinese product name.' }
    return [ordered]@{
        InstallRoot = $InstallRoot; CurrentDirectory = (Split-Path -Parent $CurrentMain)
        InstallRootLauncher = $RootLauncher; CurrentMainExecutable = $CurrentMain
        LegacyCompatibilityLauncher = $LegacyLauncher
        DesktopShortcut = [ordered]@{ Path = $desktopShortcut; Target = $desktopTarget }
        StartMenuShortcut = [ordered]@{ Path = $startMenuShortcut; Target = $startMenuTarget }
        UninstallEntry = $uninstall
    }
}

function Get-FileInventory {
    param([string[]]$Roots)
    $records = New-Object System.Collections.Generic.List[object]
    foreach ($root in $Roots) {
        foreach ($file in @(Get-ChildItem -LiteralPath $root -Recurse -File | Sort-Object FullName)) {
            $records.Add([ordered]@{ Path = $file.FullName; Length = $file.Length; Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() })
        }
    }
    return @($records)
}

function Write-TerminalMigrationEvidence {
    param($Evidence,[string]$EvidenceRoot,[string]$RunId,[Collections.Generic.List[string]]$Failures)
    $evidencePath=Join-Path $EvidenceRoot 'evidence.json';$inventoryPath=Join-Path $EvidenceRoot 'files.sha256.json'
    $setTerminalState={
        $Evidence.FinishedUtc=[DateTimeOffset]::UtcNow.ToString('O')
        if($Failures.Count-ne 0){$Evidence.Succeeded=$false;$Evidence.Error=(@($Failures|Select-Object -Unique)-join ' | ')}
    }
    & $setTerminalState
    try{Write-CanonicalJson -Value $Evidence -Destination $evidencePath}catch{throw ('Evidence terminal record is not writable; migration result is failed and must not be reported as success: '+($_.Exception.Message))}
    try{$hashes=@(Get-FileInventory -Roots @($EvidenceRoot));Write-CanonicalJson -Value ([ordered]@{SchemaVersion=1;RunId=$RunId;Files=$hashes}) -Destination $inventoryPath}
    catch{$Failures.Add('Evidence inventory finalization failed: '+$_.Exception.Message);& $setTerminalState;try{Write-CanonicalJson -Value $Evidence -Destination $evidencePath}catch{throw ('Evidence terminal failure record could not be rewritten: '+($_.Exception.Message))}}
    if($Failures.Count-ne 0){& $setTerminalState;try{Write-CanonicalJson -Value $Evidence -Destination $evidencePath}catch{throw ('Evidence aggregated terminal failure record could not be written: '+($_.Exception.Message))}}
}

$runId = New-RunId
$startedUtc = [DateTimeOffset]::UtcNow
$evidenceRoot = $null
$driverLock=$null;$updaterLock=$null;$selectedAssetLock=$null;$updaterPackageLock=$null;$oldSetupLock=$null;$oldPortableLock=$null;$currentMainLock=$null;$legacyLock=$null;$oldMainLock=$null;$upgradeRun=$null
$evidence = [ordered]@{
    SchemaVersion = 2; RunId = $runId; StartedUtc = $startedUtc.ToString('O'); FinishedUtc = $null
    AssetSource = 'caller-supplied-local-directories'; AssetKind = $AssetKind; Path = $Path
    Scenario = "$AssetKind-$Path"; FailureInjection = $FailureInjection; Succeeded = $false
    FailureRollbackVerified = $false; VelopackUpdaterVersion = $null; Upgrade = $null
    Surfaces = $null; BeforeState = $null; AfterState = $null; BeforeLicenseAudit=$null; AfterLicenseAudit=$null; RollbackLaunch=$null; MaximumOfflinePollMilliseconds=$null; Error = $null
}
$primaryFailure=$null;$finalizationFailures=New-Object Collections.Generic.List[string]

try {
    $profile = Assert-IsolatedWindowsProfile
    Assert-NoNonLoopbackNetwork | Out-Null
    $oldSource = Assert-SafeInputDirectory -InputPath $OldAssets -Label 'OldAssets'
    $newSource = Assert-SafeInputDirectory -InputPath $NewAssets -Label 'NewAssets'
    if ([string]::Equals($oldSource, $newSource, [StringComparison]::OrdinalIgnoreCase)) { throw 'OldAssets and NewAssets must be distinct.' }
    $intendedEvidenceRoot = [IO.Path]::GetFullPath($EvidenceDirectory).TrimEnd('\')
    foreach ($source in @($oldSource, $newSource)) {
        if ($intendedEvidenceRoot.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase) -or
            $source.StartsWith($intendedEvidenceRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'EvidenceDirectory and input asset directories must not contain one another.'
        }
    }
    $sourceFixturePath=Resolve-AssetFile -Root $oldSource -FileName $script:FixtureName -Label 'Migration fixture'
    $sourceFixtureSignature=Resolve-AssetFile -Root $oldSource -FileName $script:FixtureSignatureName -Label 'Migration fixture signature'
    $fixture=Assert-CanonicalSignedFixture -FixturePath $sourceFixturePath -SignaturePath $sourceFixtureSignature -CertificatePath $script:PublisherCertificatePath -ExpectedSpki $script:ExpectedPublisherSpkiSha256
    $evidenceRoot = Assert-NewOwnedDirectory -Directory $intendedEvidenceRoot -RunId $runId
    if ($evidence.Scenario -notin @('Setup-Delta','Setup-Full','Portable-Full')) { throw "Unsupported migration scenario '$($evidence.Scenario)'." }

    $stagedRoot = Join-Path $evidenceRoot 'staged-assets'
    New-Item -ItemType Directory -Path $stagedRoot | Out-Null
    $script:OwnedPaths.Add($stagedRoot)
    $stagedOld = Join-Path $stagedRoot 'old'
    $stagedNew = Join-Path $stagedRoot 'new'
    New-Item -ItemType Directory -Path $stagedOld|Out-Null;New-Item -ItemType Directory -Path $stagedNew|Out-Null
    $oldNames=@($script:FixtureName,$script:FixtureSignatureName,[string]$fixture.state_driver_file,[string]$fixture.old_setup_file,[string]$fixture.old_portable_file,[string]$fixture.old_full_file)|Select-Object -Unique
    $newNames=@([string]$fixture.new_delta_file,[string]$fixture.new_full_file,[string]$fixture.application_failure_package_file)|Select-Object -Unique
    $totalBytes=0L
    foreach($name in $oldNames){$source=Resolve-AssetFile -Root $oldSource -FileName $name -Label 'Allowlisted old asset';$copy=Copy-BoundedLockedAsset -Source $source -Destination (Join-Path $stagedOld $name) -RemainingBudget (4294967296-$totalBytes);$totalBytes+=$copy.Length}
    foreach($name in $newNames){$source=Resolve-AssetFile -Root $newSource -FileName $name -Label 'Allowlisted new asset';$copy=Copy-BoundedLockedAsset -Source $source -Destination (Join-Path $stagedNew $name) -RemainingBudget (4294967296-$totalBytes);$totalBytes+=$copy.Length}
    if($totalBytes-gt 4294967296){throw 'Allowlisted asset total exceeds 4 GiB.'}

    $fixturePath = Resolve-AssetFile -Root $stagedOld -FileName $script:FixtureName -Label 'Migration fixture'
    $fixtureSignature = Resolve-AssetFile -Root $stagedOld -FileName $script:FixtureSignatureName -Label 'Migration fixture signature'
    $fixture = Assert-CanonicalSignedFixture -FixturePath $fixturePath -SignaturePath $fixtureSignature -CertificatePath $script:PublisherCertificatePath -ExpectedSpki $script:ExpectedPublisherSpkiSha256
    $driver = Resolve-AssetFile -Root $stagedOld -FileName ([string]$fixture.state_driver_file) -Label 'State driver'
    Assert-FileSha256 -File $driver -Expected ([string]$fixture.state_driver_sha256) -Label 'State driver'
    $driverLock=Open-ArtifactReadLock -File $driver -Label 'State driver'
    Assert-PublisherExecutable -Executable $driver

    # ProductIdentity.StablePackageId fixes this topology. Signed fixtures cannot redirect it.
    $installRoot = Join-Path $profile 'AppData\Local\SuperPan.BaiduBatchTransfer'
    $currentDirectory = Join-Path $installRoot 'current'
    $oldMain = Join-Path $currentDirectory $script:OldMainExecutableName
    $currentMain = Join-Path $currentDirectory $script:NewMainExecutableName
    $legacy = Join-Path $currentDirectory $script:OldMainExecutableName
    $rootLauncher = Join-Path $installRoot $script:NewMainExecutableName
    $updater = Join-Path $installRoot 'Update.exe'
    $dataRoot = Join-Path $profile 'AppData\Local\BaiduBatchTransfer'
    $licenseRoot = Join-Path $profile 'AppData\Local\SuperPan\Licensing\v1'
    foreach ($cleanPath in @($installRoot,$dataRoot,$licenseRoot)) {
        if (Test-Path -LiteralPath $cleanPath) { throw "The isolated profile is not clean: $cleanPath" }
    }
    Assert-CleanSetupSurfaces -Profile $profile

    if ($AssetKind -ceq 'Setup') {
        $oldSetup = Resolve-AssetFile -Root $stagedOld -FileName ([string]$fixture.old_setup_file) -Label 'Old Setup'
        $oldSetupLock=Open-ArtifactReadLock -File $oldSetup -Label 'Old Setup'
        Assert-PublisherExecutable -Executable $oldSetup
        Invoke-CheckedProcess -File $oldSetup -Arguments @('--silent') -WorkingDirectory $stagedOld | Out-Null
    } else {
        $oldPortable = Resolve-AssetFile -Root $stagedOld -FileName ([string]$fixture.old_portable_file) -Label 'Old Portable'
        $oldPortableLock=Open-ArtifactReadLock -File $oldPortable -Label 'Old Portable'
        New-Item -ItemType Directory -Path $installRoot | Out-Null
        $script:OwnedPaths.Add($installRoot)
        Expand-LocalPortableAsset -Archive $oldPortable -Destination $installRoot
        Assert-NoReparseAncestors -ExistingPath $installRoot -Label 'Portable install root'
    }

    if (-not (Test-Path -LiteralPath $oldMain -PathType Leaf)) {
        throw 'Old main executable must be the real fixed-path 1.1.0 executable.'
    }
    Assert-ExactThreePartVersion -Executable $oldMain -Expected '1.1.0'
    Assert-PublisherExecutable -Executable $oldMain
    if (-not (Test-Path -LiteralPath $updater -PathType Leaf)) { throw 'Velopack Update.exe is missing from the fixed install root.' }
    $updaterLock=Open-ArtifactReadLock -File $updater -Label 'Installed Velopack updater';Assert-PublisherExecutable -Executable $updater
    $evidence.VelopackUpdaterVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($updater).ProductVersion

    $commonDriverArguments = @('--run-id',$runId,'--profile-root',$profile,'--fixture',$fixturePath)
    $beforeChallenge=New-RunChallenge
    $beforeRun = Invoke-BoundedDriver -Driver $driver -WorkingDirectory $stagedOld -Arguments (@('seed-and-probe','--challenge',$beforeChallenge.Base64) + $commonDriverArguments)
    $before = Assert-StateEvidence -StateJson $beforeRun.StandardOutput -ProfileRoot $profile -RunId $runId -ExpectedChallengeSha256 $beforeChallenge.Sha256 -WindowStartedUtc $beforeRun.StartedUtc -WindowCompletedUtc $beforeRun.CompletedUtc
    $evidence.BeforeState = $before
    $evidence.BeforeLicenseAudit=Assert-IndependentLicenseAudit -State $before -ProfileRoot $profile -Fixture $fixture

    if($FailureInjection-ceq'ApplicationFailed'){
        $selectedAsset=Resolve-AssetFile -Root $stagedNew -FileName ([string]$fixture.application_failure_package_file) -Label 'Signed application-failure package'
    } elseif ($Path -ceq 'Delta') {
        Resolve-AssetFile -Root $stagedOld -FileName ([string]$fixture.old_full_file) -Label 'Old full package' | Out-Null
        $selectedAsset = Resolve-AssetFile -Root $stagedNew -FileName ([string]$fixture.new_delta_file) -Label 'New delta package'
    } else {
        $selectedAsset = Resolve-AssetFile -Root $stagedNew -FileName ([string]$fixture.new_full_file) -Label 'New full package'
    }
    $selectedAssetLock=Open-ArtifactReadLock -File $selectedAsset -Label 'Selected update asset'
    $selectedAssetHash = $selectedAssetLock.Sha256
    $selectedAssetBytes = $selectedAssetLock.Length
    if($selectedAssetBytes-lt 2){throw 'Selected local update asset is too small.'}
    $candidateVerificationPackage=if($FailureInjection-ceq'ApplicationFailed'){$selectedAsset}else{Resolve-AssetFile -Root $stagedNew -FileName ([string]$fixture.new_full_file) -Label 'Pinned candidate full package'}
    Assert-PackageContainsPinnedMain -Package $candidateVerificationPackage -ExpectedFileName $script:NewMainExecutableName -ExpectedSha256 ([string]$fixture.new_main_sha256)
    $localReleaseSource=Join-Path $evidenceRoot "local-release-source-$runId"
    New-Item -ItemType Directory -Path $localReleaseSource|Out-Null;$script:OwnedPaths.Add($localReleaseSource)
    Write-CanonicalJson -Value ([ordered]@{run_id=$runId;source_asset_sha256=$selectedAssetHash}) -Destination (Join-Path $localReleaseSource ".migration-source-$runId.json") -CreateNew
    $updaterPackage=Join-Path $localReleaseSource ([IO.Path]::GetFileName($selectedAsset))
    if($FailureInjection-ceq'DownloadInterrupted'){
        $updaterPackage=Join-Path $localReleaseSource 'interrupted-partial.nupkg'
        Copy-FromArtifactReadLock -Lock $selectedAssetLock -Destination $updaterPackage -MaximumBytes ([Math]::Max(1,[Math]::Floor($selectedAssetBytes/3)))
        Assert-FileIsExactPrefix -Source $selectedAsset -Prefix $updaterPackage
    }else{Copy-FromArtifactReadLock -Lock $selectedAssetLock -Destination $updaterPackage -MaximumBytes $selectedAssetBytes}
    Assert-NoReparseAncestors -ExistingPath $updaterPackage -Label 'Script-created updater package';Get-FileHardLinkCount -File $updaterPackage|Out-Null
    $updaterPackageLock=Open-ArtifactReadLock -File $updaterPackage -Label 'Script-created updater package'
    $restartStarted = [DateTimeOffset]::UtcNow
    Assert-ArtifactReadLockUnchanged $updaterLock;Assert-ArtifactReadLockUnchanged $selectedAssetLock;Assert-ArtifactReadLockUnchanged $updaterPackageLock
    $updaterStdout=Join-Path $evidenceRoot 'updater.stdout.log';$updaterStderr=Join-Path $evidenceRoot 'updater.stderr.log'
    $updaterArguments=@('apply','--package',$updaterPackage,'--rootDir',$installRoot,'--packageDir',$localReleaseSource)
    $upgradeChallenge=New-RunChallenge;$priorRun=$env:SUPERPAN_MIGRATION_RUN_ID;$priorChallenge=$env:SUPERPAN_MIGRATION_CHALLENGE_SHA256;$priorScenario=$env:SUPERPAN_MIGRATION_SCENARIO
    $completionMode=if($FailureInjection-ceq'None'){'SuccessExpectRestart'}else{'NoRestart'}
    try{$env:SUPERPAN_MIGRATION_RUN_ID=$runId;$env:SUPERPAN_MIGRATION_CHALLENGE_SHA256=$upgradeChallenge.Sha256;$env:SUPERPAN_MIGRATION_SCENARIO=$evidence.Scenario;$upgradeRun=[MigrationContainedRunner]::Run($updater,$updaterArguments,$installRoot,$updaterStdout,$updaterStderr,300000,1048576,$completionMode,$currentMain)}finally{$env:SUPERPAN_MIGRATION_RUN_ID=$priorRun;$env:SUPERPAN_MIGRATION_CHALLENGE_SHA256=$priorChallenge;$env:SUPERPAN_MIGRATION_SCENARIO=$priorScenario}
    if($upgradeRun.Failure-or($completionMode-ceq'NoRestart'-and-not$upgradeRun.SafeToCleanup)-or($completionMode-ceq'SuccessExpectRestart'-and$upgradeRun.JobHandle-eq[IntPtr]::Zero)){throw "Contained Velopack updater failed safety enforcement: $($upgradeRun.Failure)"}
    Assert-NoNonLoopbackNetwork|Out-Null
    Assert-ArtifactReadLockUnchanged $updaterLock;Assert-ArtifactReadLockUnchanged $selectedAssetLock;Assert-ArtifactReadLockUnchanged $updaterPackageLock
    $upgrade=[ordered]@{run_id=$runId;challenge_sha256=$upgradeChallenge.Sha256;scenario=$evidence.Scenario;asset_kind=$AssetKind;path=$Path;failure_injection=$FailureInjection;started_utc=$upgradeRun.StartedUtc;completed_utc=$upgradeRun.CompletedUtc;source_asset_path=$selectedAsset;source_asset_sha256=$selectedAssetHash;source_asset_length=$selectedAssetBytes;updater_path=$updater;updater_identity=$updaterLock.Identity;updater_pid=$upgradeRun.ProcessId;updater_exit_code=$upgradeRun.ExitCode;updater_arguments=$updaterArguments;package_path=$updaterPackage;package_sha256=$updaterPackageLock.Sha256;candidate_main_expected_sha256=[string]$fixture.new_main_sha256;candidate_main_observed_sha256=$null}
    if($FailureInjection-ceq'None'){if($upgradeRun.ExitCode-ne 0){throw 'Velopack updater returned nonzero for the success scenario.'}}
    else{if($upgradeRun.ExitCode-eq 0){throw 'Failure injection did not make the real Velopack updater fail.'}}
    if($FailureInjection-ceq'DownloadInterrupted'){Assert-FileIsExactPrefix -Source $selectedAsset -Prefix $updaterPackage;Assert-DownloadInterruptedFingerprint -ExitCode $upgradeRun.ExitCode -StdoutPath $updaterStdout -StderrPath $updaterStderr -ExpectedStdoutSha256 ([string]$fixture.download_interrupted_stdout_sha256) -ExpectedStderrSha256 ([string]$fixture.download_interrupted_stderr_sha256) -ExpectedExitCode ([int]$fixture.download_interrupted_exit_code) -ExpectedMarker ([string]$fixture.download_interrupted_marker) -SourceLeaf ([IO.Path]::GetFileName($selectedAsset)) -PartialLeaf ([IO.Path]::GetFileName($updaterPackage));$upgrade.partial_artifact_path=$updaterPackage;$upgrade.failure_fingerprint_verified=$true}
    if($FailureInjection-ceq'ApplicationFailed'){
        $hook=Assert-StrictChildPath -Candidate (Join-Path $installRoot ([string]$fixture.application_failure_hook_relative_path)) -Root $installRoot -Label 'Application failure hook trace'
        if(-not(Test-Path -LiteralPath $hook -PathType Leaf)){throw 'Signed candidate application failure hook was not produced.'}
        Assert-NoReparseAncestors -ExistingPath $hook -Label 'Application failure hook trace';Get-FileHardLinkCount -File $hook|Out-Null
        $hookObject=Read-CanonicalJsonFile -File $hook -MaximumBytes 65536
        $hookCreated=[DateTimeOffset]$hookObject.created_utc
        if([string]$hookObject.run_id-cne$runId-or[string]$hookObject.challenge_sha256-cne$upgradeChallenge.Sha256-or[string]$hookObject.source_sha256-cne$selectedAssetHash-or[string]$hookObject.scenario-cne$evidence.Scenario-or$hookCreated-lt[DateTimeOffset]$upgradeRun.StartedUtc-or$hookCreated-gt[DateTimeOffset]$upgradeRun.CompletedUtc){throw 'Application failure hook is not bound to this run/source/scenario/window.'}
        $upgrade.candidate_hook_trace_path=$hook;$upgrade.candidate_hook_trace_sha256=Get-StreamingSha256 $hook
    }
    $evidence.Upgrade = $upgrade

    if ($FailureInjection -cne 'None') {
        Assert-ExactThreePartVersion -Executable $oldMain -Expected '1.1.0'
        Assert-PublisherExecutable -Executable $oldMain;$oldMainLock=Open-ArtifactReadLock -File $oldMain -Label 'Rollback old main executable'
        $evidence.RollbackLaunch=Assert-OldVersionStillLaunches -OldExecutable $oldMain -RunId $runId -EvidenceRoot $evidenceRoot -ArtifactLock $oldMainLock
        $afterChallenge=New-RunChallenge
        $afterRun = Invoke-BoundedDriver -Driver $driver -WorkingDirectory $stagedOld -Arguments (@('probe','--challenge',$afterChallenge.Base64) + $commonDriverArguments)
        $after = Assert-StateEvidence -StateJson $afterRun.StandardOutput -ProfileRoot $profile -RunId $runId -ExpectedChallengeSha256 $afterChallenge.Sha256 -WindowStartedUtc $afterRun.StartedUtc -WindowCompletedUtc $afterRun.CompletedUtc
        $evidence.AfterLicenseAudit=Assert-IndependentLicenseAudit -State $after -ProfileRoot $profile -Fixture $fixture
        if((ConvertTo-CanonicalJson $evidence.BeforeLicenseAudit)-cne(ConvertTo-CanonicalJson $evidence.AfterLicenseAudit)){throw 'Independent license audit changed across failed upgrade.'}
        Assert-StateEvidencePreserved -Before $before -After $after
        $evidence.AfterState = $after
        $evidence.FailureRollbackVerified = $true
        $evidence.Succeeded = $true
    } else {
        if (-not (Test-Path -LiteralPath $currentMain -PathType Leaf)) {
            throw 'Current Chinese executable must be the real fixed-path 1.1.1 executable.'
        }
        Assert-ExactThreePartVersion -Executable $currentMain -Expected '1.1.1'
        foreach ($application in @($currentMain,$rootLauncher,$legacy)) { Assert-PublisherExecutable -Executable $application }
        $currentMainLock=Open-ArtifactReadLock -File $currentMain -Label 'Job-owned restarted current executable'
        if(-not[string]::Equals([IO.Path]::GetFullPath([string]$upgradeRun.RestartPath),[IO.Path]::GetFullPath($currentMain),[StringComparison]::OrdinalIgnoreCase)-or[int]$upgradeRun.RestartPid-le 0-or[string]$upgradeRun.RestartFileIdentity-cne$currentMainLock.Identity-or$currentMainLock.Sha256-cne[string]$fixture.new_main_sha256){throw 'Contained updater did not retain the exact signed-fixture-pinned current Chinese restart process/file identity/hash.'}
        $restartTime=[DateTimeOffset]$upgradeRun.RestartStartedUtc;if($restartTime-lt$restartStarted-or$restartTime-gt[DateTimeOffset]$upgradeRun.CompletedUtc){throw 'Contained restart process creation time is outside the upgrade window.'}
        $restartEvidence = [ordered]@{ Id=[int]$upgradeRun.RestartPid; Path=[string]$upgradeRun.RestartPath; StartTimeUtc=$restartTime.ToString('O'); FileIdentity=$currentMainLock.Identity; JobOwned=$true }
        $upgrade.candidate_main_observed_sha256=$currentMainLock.Sha256
        if ($AssetKind -ceq 'Setup') {
            $surfaces = Assert-SetupSurfaces -InstallRoot $installRoot -CurrentMain $currentMain -RootLauncher $rootLauncher -LegacyLauncher $legacy -Profile $profile
        } else {
            $surfaces = [ordered]@{ InstallRoot=$installRoot; CurrentDirectory=$currentDirectory; InstallRootLauncher=$rootLauncher; CurrentMainExecutable=$currentMain; LegacyCompatibilityLauncher=$legacy; DesktopShortcut=$null; StartMenuShortcut=$null; UninstallEntry=$null }
        }
        $surfaces.UpgradeRestartProcess = $restartEvidence
        $legacyLock=Open-ArtifactReadLock -File $legacy -Label 'Legacy compatibility launcher';$compatRun=$null;$compatToken=[Guid]::NewGuid().ToString('N')
        try{$compatRun=[MigrationContainedRunner]::Run($legacy,@('--migration-gate-compat',$runId),$currentDirectory,(Join-Path $evidenceRoot "compat-$compatToken.stdout"),(Join-Path $evidenceRoot "compat-$compatToken.stderr"),45000,1048576,'SuccessExpectRestart',$currentMain);if($compatRun.Failure-or$compatRun.JobHandle-eq[IntPtr]::Zero-or[string]$compatRun.RestartFileIdentity-cne$currentMainLock.Identity){throw "Legacy compatibility forwarding proof failed: $($compatRun.Failure)"};$surfaces.LegacyCompatibilityForwardTarget=$compatRun.RestartPath;$surfaces.LegacyCompatibilityForwardPid=$compatRun.RestartPid}
        finally{if($compatRun-and$compatRun.JobHandle-ne[IntPtr]::Zero){if(-not[MigrationContainedRunner]::CloseSession($compatRun)){throw 'Compatibility forwarding job did not terminate cleanly.'}}}
        $afterChallenge=New-RunChallenge
        $afterRun = Invoke-BoundedDriver -Driver $driver -WorkingDirectory $stagedOld -Arguments (@('probe','--challenge',$afterChallenge.Base64) + $commonDriverArguments)
        $after = Assert-StateEvidence -StateJson $afterRun.StandardOutput -ProfileRoot $profile -RunId $runId -ExpectedChallengeSha256 $afterChallenge.Sha256 -WindowStartedUtc $afterRun.StartedUtc -WindowCompletedUtc $afterRun.CompletedUtc
        $evidence.AfterLicenseAudit=Assert-IndependentLicenseAudit -State $after -ProfileRoot $profile -Fixture $fixture
        if((ConvertTo-CanonicalJson $evidence.BeforeLicenseAudit)-cne(ConvertTo-CanonicalJson $evidence.AfterLicenseAudit)){throw 'Independent license audit changed across successful upgrade.'}
        Assert-StateEvidencePreserved -Before $before -After $after
        $evidence.Surfaces = $surfaces
        $evidence.AfterState = $after
        $evidence.Succeeded = $true
    }
    Assert-NoNonLoopbackNetwork | Out-Null
} catch {
    $evidence.Error = $_.Exception.Message
    $evidence.Succeeded=$false
    $primaryFailure=$_
} finally {
    try{if($upgradeRun-and$upgradeRun.JobHandle-ne[IntPtr]::Zero){if(-not[MigrationContainedRunner]::CloseSession($upgradeRun)){throw 'Contained restart job did not terminate cleanly.'}}}catch{$finalizationFailures.Add($_.Exception.Message)}
    foreach($lock in @($oldMainLock,$legacyLock,$currentMainLock,$updaterPackageLock,$selectedAssetLock,$updaterLock,$oldPortableLock,$oldSetupLock,$driverLock)){if($lock){try{$lock.Stream.Dispose()}catch{$finalizationFailures.Add($_.Exception.Message)}}}
    if ($evidenceRoot) {
        if($primaryFailure){$finalizationFailures.Insert(0,'Migration body failed: '+$primaryFailure.Exception.Message)}
        try{Write-TerminalMigrationEvidence -Evidence $evidence -EvidenceRoot $evidenceRoot -RunId $runId -Failures $finalizationFailures}catch{$finalizationFailures.Add($_.Exception.Message)}
    }
}

if($primaryFailure-or$finalizationFailures.Count-ne 0){throw ('Migration gate failed: '+(@($finalizationFailures|Select-Object -Unique)-join ' | '))}
if (-not $evidence.Succeeded) { throw 'The migration gate did not complete successfully.' }
Write-Host "REAL_MIGRATION_GATE_PASS=$evidenceRoot"

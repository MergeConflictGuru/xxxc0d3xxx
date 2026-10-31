#requires -Version 5.1
[CmdletBinding(DefaultParameterSetName='Register')]
param(
    [Parameter(Mandatory, Position=0, ParameterSetName='Register')][string]$Source,
    [Parameter(Mandatory, Position=1, ParameterSetName='Register')][string]$Destination,
    [Parameter(ParameterSetName='Register')][ValidatePattern('^([01]\d|2[0-3]):[0-5]\d$')][string]$At = '12:00',
    [Parameter(ParameterSetName='Register')][string]$TaskName,
    [Parameter(Mandatory, ParameterSetName='Run')][string]$RunConfig
)
$ErrorActionPreference = 'Stop'

if (-not ('NtfsDailyCopy.Native' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace NtfsDailyCopy {
 public static class Native {
  [StructLayout(LayoutKind.Sequential)] struct Info {
   public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
   public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
  }
  [StructLayout(LayoutKind.Explicit, Size=24)] struct Descriptor {
   [FieldOffset(0)] public uint Size; [FieldOffset(4)] public uint Type; [FieldOffset(8)] public ulong Id;
  }
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
  static extern SafeFileHandle CreateFile(string p,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetFileInformationByHandle(SafeFileHandle h,out Info info);
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
  static extern uint GetFinalPathNameByHandle(SafeFileHandle h,StringBuilder path,uint size,uint flags);
  [DllImport("kernel32.dll", SetLastError=true)]
  static extern SafeFileHandle OpenFileById(SafeFileHandle volume,ref Descriptor id,uint access,uint share,IntPtr security,uint flags);
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
  static extern bool GetVolumeInformation(string root,StringBuilder name,uint nameSize,out uint serial,out uint maxComponent,out uint flags,StringBuilder fs,uint fsSize);
  static void Check(SafeFileHandle h) { if(h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error()); }
  static string PathOf(SafeFileHandle h,uint flags) {
   var b=new StringBuilder(32768); uint n=GetFinalPathNameByHandle(h,b,(uint)b.Capacity,flags);
   if(n==0) throw new Win32Exception(Marshal.GetLastWin32Error());
   if(n>=b.Capacity) throw new Exception("Resolved path is too long.");
   return b.ToString();
  }
  public static string Canonical(string path) {
   using(var h=CreateFile(path,0,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero)) { Check(h); return PathOf(h,0); }
  }
  public static string[] Identify(string path) {
   using(var h=CreateFile(path,0,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero)) {
    Check(h); Info i; if(!GetFileInformationByHandle(h,out i)) throw new Win32Exception(Marshal.GetLastWin32Error());
    if((i.Attributes & 16)==0) throw new Exception("Source must be a directory.");
    string p=PathOf(h,1); int end=p.IndexOf('}');
    if(!p.StartsWith(@"\\?\Volume{") || end<0) throw new Exception("Source must be on a local NTFS volume.");
    string root=p.Substring(0,end+1)+@"\";
    uint serial, maxComponent, flags; var fs=new StringBuilder(64);
    if(!GetVolumeInformation(root,null,0,out serial,out maxComponent,out flags,fs,64)) throw new Win32Exception(Marshal.GetLastWin32Error());
    if(!String.Equals(fs.ToString(),"NTFS",StringComparison.OrdinalIgnoreCase)) throw new Exception("Source must be on NTFS.");
    return new[]{root, (((ulong)i.IndexHigh<<32)|i.IndexLow).ToString(), PathOf(h,0)};
   }
  }
  public static string Resolve(string volume,string id) {
   using(var v=CreateFile(volume,0,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero)) {
    Check(v); var d=new Descriptor { Size=24, Type=0, Id=UInt64.Parse(id) };
    using(var h=OpenFileById(v,ref d,0,7,IntPtr.Zero,0x02000000)) {
     Check(h); Info i; if(!GetFileInformationByHandle(h,out i)) throw new Win32Exception(Marshal.GetLastWin32Error());
     if((i.Attributes & 16)==0) throw new Exception("Saved ID no longer identifies a directory.");
     return PathOf(h,0);
    }
   }
  }
 }
}
'@
}

function Normal-Path([string]$Path) {
    if ($Path.StartsWith('\\?\UNC\')) { $Path = '\\' + $Path.Substring(8) }
    elseif ($Path.StartsWith('\\?\') -and $Path.Substring(4) -match '^[A-Za-z]:\\') { $Path = $Path.Substring(4) }
    return [IO.Path]::GetFullPath($Path).TrimEnd('\') + '\'
}
function Check-Separation([string]$A, [string]$B) {
    $aPath = Normal-Path $A; $bPath = Normal-Path $B
    if ($aPath.StartsWith($bPath, [StringComparison]::OrdinalIgnoreCase) -or
        $bPath.StartsWith($aPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Source and destination must be separate directories, with neither inside the other.'
    }
}

if ($PSCmdlet.ParameterSetName -eq 'Run') {
    $configPath = (Resolve-Path -LiteralPath $RunConfig).ProviderPath
    $jobDir = Split-Path -Parent $configPath
    $lock = $null
    try {
        try { $lock = [IO.File]::Open((Join-Path $jobDir 'run.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
        catch [IO.IOException] { exit 0 }
        $cfg = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
        $stamp = Join-Path $jobDir 'last-attempt.txt'
        if (Test-Path -LiteralPath $stamp) {
            $last = [DateTimeOffset]::Parse((Get-Content -LiteralPath $stamp -Raw).Trim())
            if (([DateTimeOffset]::UtcNow - $last).TotalHours -lt 24) { exit 0 }
        }
        $currentSource = [NtfsDailyCopy.Native]::Resolve($cfg.Volume, $cfg.FileId)
        # Prefer a regular DOS/UNC path for Robocopy and filesystem policy compatibility.
        $currentSource = (Normal-Path $currentSource).TrimEnd('\')
        if ($currentSource -match '^[A-Za-z]:$') { $currentSource += '\' }
        # Require an existing destination: never create a replacement mount/root accidentally.
        if (-not (Test-Path -LiteralPath $cfg.Destination -PathType Container)) { throw 'Destination is unavailable. Mount the drive and create the destination folder first.' }
        $actualDestination = [NtfsDailyCopy.Native]::Canonical($cfg.Destination)
        Check-Separation $currentSource $actualDestination
        [DateTimeOffset]::UtcNow.ToString('o') | Set-Content -LiteralPath $stamp -Encoding UTF8
        "$(Get-Date -Format o) Source: $currentSource -> $($cfg.Destination)" | Add-Content -LiteralPath (Join-Path $jobDir 'history.log')
        & robocopy.exe $currentSource $cfg.Destination /E /COPY:DAT /DCOPY:DA /XJ /R:2 /W:5 /NP "/LOG+:$(Join-Path $jobDir 'copy.log')"
        $result = $LASTEXITCODE
        "$(Get-Date -Format o) Robocopy exit code: $result" | Add-Content -LiteralPath (Join-Path $jobDir 'history.log')
        if ($result -ge 8) { exit $result }
    } catch {
        $_ | Out-String | Add-Content -LiteralPath (Join-Path $jobDir 'errors.log')
        Write-Error $_ -ErrorAction Continue
        exit 1
    } finally { if ($lock) { $lock.Dispose() } }
    exit 0
}

$sourceItem = Get-Item -LiteralPath $Source
if (-not $sourceItem.PSIsContainer) { throw 'Source must be a folder.' }
$sourceFull = $sourceItem.FullName
$identity = [NtfsDailyCopy.Native]::Identify($sourceFull)
# Verify ID resolution now, before installing anything.
$null = [NtfsDailyCopy.Native]::Resolve($identity[0], $identity[1])
$destItem = Get-Item -LiteralPath $Destination
if (-not $destItem.PSIsContainer) { throw 'Create the destination folder first.' }
$destFull = $destItem.FullName
$destResolved = [NtfsDailyCopy.Native]::Canonical($destFull)
Check-Separation $identity[2] $destResolved
if (-not $TaskName) { $TaskName = 'Daily copy - ' + $sourceItem.Name + ' - ' + [Guid]::NewGuid().ToString('N').Substring(0,8) }
if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) { throw "Task already exists: $TaskName" }
$jobId = [Guid]::NewGuid().ToString('N')
$jobDir = Join-Path $env:LOCALAPPDATA "NtfsDailyCopy\$jobId"
$null = New-Item -ItemType Directory -Path $jobDir
$installedScript = Join-Path $jobDir 'Register-NtfsDailyCopy.ps1'
Copy-Item -LiteralPath $PSCommandPath -Destination $installedScript
$configPath = Join-Path $jobDir 'config.json'
@{ Volume=$identity[0]; FileId=$identity[1]; OriginalSource=$sourceFull; Destination=$destFull; TaskName=$TaskName } |
    ConvertTo-Json | Set-Content -LiteralPath $configPath -Encoding UTF8
$action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -Argument "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$installedScript`" -RunConfig `"$configPath`""
$trigger = New-ScheduledTaskTrigger -Daily -At ([datetime]::Today.Add([TimeSpan]::Parse($At)))
$userId = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$principal = New-ScheduledTaskPrincipal -UserId $userId -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 23)
$description = "Copy new/changed files, at most once per 24 hours. Original source: $sourceFull | Destination: $destFull | Source NTFS volume: $($identity[0]) | File ID: $($identity[1]). Follows source renames/moves on that volume. No deletions; source wins on changed files. Daily $At, while user is logged in. Config/logs: $jobDir"
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description $description | Out-Null
Write-Output "Created task: $TaskName"
Write-Output "Daily at $At; at least 24 hours between copy attempts. No immediate copy."
Write-Output "Source: $sourceFull`nDestination: $destFull`nConfig and logs: $jobDir"

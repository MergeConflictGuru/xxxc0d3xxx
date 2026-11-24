param([switch]$PackageChild)
$ErrorActionPreference = 'Stop'
$log = Join-Path $PSScriptRoot 'InvisibleLauncher.child.log'
function Log($message) {
    $line = '[' + [DateTimeOffset]::Now.ToString('O') + '] powershell pid=' + $PID + ': ' + $message + [Environment]::NewLine
    try { [IO.File]::AppendAllText($log, $line) }
    catch {
        $script:log = Join-Path $env:LOCALAPPDATA 'InvisibleLauncher.child.log'
        [IO.File]::AppendAllText($script:log, $line)
    }
}
function Show-CodexWindow([int]$DesktopPid = 0) {
    $arguments = '--show-codex-window'
    if ($DesktopPid) { $arguments += ' ' + $DesktopPid }
    $helper = Start-Process -FilePath (Join-Path $PSScriptRoot 'InvisibleLauncher.exe') -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    if ($helper.ExitCode -ne 0) { throw 'Codex has no visible desktop window. See InvisibleLauncher.error.log.' }
}
try {
    Log ('BEGIN user=' + [Security.Principal.WindowsIdentity]::GetCurrent().Name + '; session=' + [Diagnostics.Process]::GetCurrentProcess().SessionId)
    # Launch settings belong to this user's selected environment, never the chat
    # or Electron process that happened to invoke the launcher.
    foreach ($key in @([Environment]::GetEnvironmentVariables('Process').Keys)) {
        if ($key -match '^(CA_|CODEX_APP_TOOLS_)' -or $key -in @('CODEX_HOME','CODEX_CLI_PATH','CODEX_SQLITE_HOME','CODEX_ELECTRON_USER_DATA_PATH','CODEX_APP_SERVER_WS_URL','CODEX_APP_SERVER_FORCE_CLI','CODEX_THREAD_ID','CODEX_INTERNAL_ORIGINATOR_OVERRIDE','NODE_OPTIONS','ELECTRON_RUN_AS_NODE')) {
            [Environment]::SetEnvironmentVariable($key, $null, 'Process')
        }
    }
    $package = Get-AppxPackage -Name OpenAI.Codex -ErrorAction Stop | Select-Object -First 1
    if (!$package) { throw 'OpenAI.Codex is not installed for this user.' }
    $manifest = Get-AppxPackageManifest -Package $package.PackageFullName
    $application = $manifest.Package.Applications.Application | Where-Object Id -eq App | Select-Object -First 1
    if (!$application) { throw 'The Codex package manifest contains no Application with Id=App.' }
    $executable = Join-Path $package.InstallLocation $application.Executable
    Set-Location -LiteralPath ([Environment]::GetFolderPath('UserProfile'))
    if ($PackageChild) {
        Start-Process -FilePath $executable -WorkingDirectory (Get-Location).Path -WindowStyle Normal | Out-Null
        exit 0
    }
    Invoke-CommandInDesktopPackage -PackageFamilyName $package.PackageFamilyName -AppId $application.Id -Command $executable | Out-Null
    Show-CodexWindow
    Log 'END success; normal Codex visible'
    exit 0
} catch {
    Log ('ERROR: ' + $_.Exception.ToString() + '; scriptStack=' + $_.ScriptStackTrace)
    exit 1
}

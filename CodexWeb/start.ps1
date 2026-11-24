param(
    [int]$Port = 4783,
    [string]$WorkingDirectory = (Split-Path -Parent $PSScriptRoot),
    [string]$RouterRoot,
    [switch]$NoBrowser,
    [switch]$Background,
    [switch]$Stop
)
$ErrorActionPreference = 'Stop'
$runtime = Join-Path (Split-Path -Parent $PSScriptRoot) '.app_tmp\codex-web'
$recordPath = Join-Path $runtime "server-$Port.json"
function New-WebSession($record) {
    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $parts = $record.cookie.Split('=',2)
    $session.Cookies.Add((New-Object Net.Cookie($parts[0],$parts[1],'/','127.0.0.1')))
    return $session
}
function Get-WebInstance {
    if (-not (Test-Path -LiteralPath $recordPath)) { return $null }
    try {
        $record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
        if ($record.port -ne $Port) { return $null }
        $status = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/api/status" -WebSession (New-WebSession $record) -TimeoutSec 2
        $record | Add-Member -NotePropertyName 'backendRoot' -NotePropertyValue $status.routerRoot -Force
        $record | Add-Member -NotePropertyName 'activeTurns' -NotePropertyValue @($status.activeTurns) -Force
        $record | Add-Member -NotePropertyName 'ready' -NotePropertyValue ([bool]$status.ready) -Force
        $record | Add-Member -NotePropertyName 'failure' -NotePropertyValue $status.failure -Force
        return $record
    } catch { return $null }
}
$existing = Get-WebInstance
function Stop-WebInstance($instance) {
    if (@($instance.activeTurns | Where-Object { $_ }).Count) { throw 'Codex Web has a running turn. Finish or stop it before changing backends.' }
    Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$Port/api/shutdown" -WebSession (New-WebSession $instance) | Out-Null
    for ($attempt=0; $attempt -lt 50; $attempt++) {
        Start-Sleep -Milliseconds 100
        if (-not (Get-WebInstance)) {
            try { $listener = New-Object Net.Sockets.TcpClient; $listener.Connect('127.0.0.1', $Port); $listener.Dispose() }
            catch { return }
        }
    }
    throw 'Codex Web has not stopped yet; no other process was terminated.'
}
if ($Stop) {
    if (-not $existing) { Write-Host 'No running Codex Web instance on this port.'; return }
    Stop-WebInstance $existing
    Write-Host 'Stopped Codex Web. A shared router backend remains running.'
    return
}
if ($RouterRoot) {
    $resolvedRouter = (Resolve-Path -LiteralPath $RouterRoot).Path
    # The regular launcher is idempotent: it verifies or starts the persistent
    # server. A stale server.json alone does not prove that the backend is alive.
    & (Join-Path $resolvedRouter 'python\python.exe') (Join-Path $resolvedRouter 'bridge\launch.py') --server-only | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The multi-account backend could not start.' }
    if ($existing -and ((-not $existing.backendRoot) -or
        ([IO.Path]::GetFullPath($existing.backendRoot) -ne [IO.Path]::GetFullPath($resolvedRouter)) -or $existing.failure)) {
        Stop-WebInstance $existing
        $existing = $null
    }
}
if ($existing) {
    Write-Host "Codex Web: $($existing.url)"
    if (-not $NoBrowser) { Start-Process $existing.url }
    return
}
$env:PORT = "$Port"
$env:CODEX_WEB_CWD = $WorkingDirectory
if ($RouterRoot) {
    $record = Join-Path $resolvedRouter '.app_tmp\persistent\server.json'
    if (-not (Test-Path -LiteralPath $record)) { throw 'Start ConversationAccounts.exe --server-only first, then run this launcher again.' }
    $env:CA_PERSISTENT_RECORD = $record
    $env:CODEX_HOME = (Get-Content -LiteralPath (Join-Path $resolvedRouter 'bridge\router-config.json') -Raw | ConvertFrom-Json).sharedStore
    $env:CODEX_WEB_COMMAND = ConvertTo-Json -Compress -InputObject @((Join-Path $resolvedRouter 'python\python.exe'), (Join-Path $resolvedRouter 'bridge\router.py'), 'app-server')
} elseif (-not $env:CODEX_WEB_COMMAND) {
    $env:CODEX_WEB_BINARY = (Get-Command codex -ErrorAction Stop).Source
}
if ($Background) {
    New-Item -ItemType Directory -Force -Path $runtime | Out-Null
    $nodePath = (Get-Command node -ErrorAction Stop).Source
    $scriptPath = Join-Path $PSScriptRoot 'server.mjs'
    $serverProcess = Start-Process -FilePath $nodePath -ArgumentList @('"' + $scriptPath + '"') -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -RedirectStandardOutput (Join-Path $runtime "server-$Port.log") -RedirectStandardError (Join-Path $runtime "server-$Port.error.log") -PassThru
    for ($attempt=0; $attempt -lt 80; $attempt++) {
        Start-Sleep -Milliseconds 250
        $instance = Get-WebInstance
        if ($instance -and $instance.failure) { throw $instance.failure }
        if ($instance -and $instance.ready) {
            Write-Host "Codex Web: $($instance.url)"
            if (-not $NoBrowser) { Start-Process $instance.url }
            return
        }
        if ($serverProcess.HasExited) { throw "Codex Web exited. See $runtime\server-$Port.error.log" }
    }
    throw "Codex Web did not become available. See $runtime\server-$Port.error.log"
}
Write-Host 'Starting Codex Web. Keep this terminal open; Ctrl+C stops the web bridge.'
& node (Join-Path $PSScriptRoot 'server.mjs') | ForEach-Object {
    Write-Host $_
    if (-not $NoBrowser -and $_ -match '^Codex Web: (http://127\.0\.0\.1:\d+/\?key=[a-f0-9]+)$') { Start-Process $Matches[1] }
}

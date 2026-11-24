# Auto-generate 7-Zip backup archive dialog for essential Codex files
$ErrorActionPreference = 'Stop'
$codexDir = 'C:\Users\aiaiai\.codex'
$sevenZip = 'C:\Program Files\7-Zip\7zG.exe'

if (!(Test-Path -LiteralPath $sevenZip)) {
    $cmd = Get-Command 7zG -ErrorAction SilentlyContinue
    if ($cmd) {
        $sevenZip = $cmd.Source
    } else {
        throw "7zG.exe not found at '$sevenZip' or in PATH."
    }
}

$files = [System.Collections.Generic.List[string]]::new()

# 1. Everything matching auth.json* pattern
Get-ChildItem -Path $codexDir -Filter 'auth.json*' -File | ForEach-Object {
    $files.Add($_.Name)
}

# 2. Both SQLite databases + WAL and SHM companions
$databases = @('state_5.sqlite', 'thread_history_1.sqlite')
foreach ($db in $databases) {
    foreach ($suffix in @('', '-wal', '-shm')) {
        $candidate = $db + $suffix
        if (Test-Path -LiteralPath (Join-Path $codexDir $candidate)) {
            $files.Add($candidate)
        }
    }
}

# 3. Obligatory metadata/sidebar files + AGENTS.md
$extras = @(
    '.codex-global-state.json',
    '.codex-global-state.json.bak',
    'session_index.jsonl',
    'AGENTS.md'
)
foreach ($extra in $extras) {
    if (Test-Path -LiteralPath (Join-Path $codexDir $extra)) {
        $files.Add($extra)
    }
}

$uniqueFiles = $files | Select-Object -Unique

# Suggest default archive name (e.g. codex_backup_2026-10-31.7z)
$timestamp = Get-Date -Format 'yyyy-MM-dd'
$archivePath = Join-Path $codexDir "codex_backup_$timestamp.7z"

# -ad instructs 7zG to display the "Add to archive" GUI dialog
$arguments = @('a', '-ad', $archivePath) + $uniqueFiles

Write-Host "Opening 7-Zip Add to Archive dialog with $($uniqueFiles.Count) files..."
Start-Process -FilePath $sevenZip -ArgumentList $arguments -WorkingDirectory $codexDir

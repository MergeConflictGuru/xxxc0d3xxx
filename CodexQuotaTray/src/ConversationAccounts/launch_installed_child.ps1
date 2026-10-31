param([Parameter(Mandatory=$true)][string]$EnvironmentFile,[Parameter(Mandatory=$true)][string]$Executable,[Parameter(Mandatory=$true)][string]$Root,[Parameter(Mandatory=$true)][string]$UserData,[Parameter(Mandatory=$true)][int]$Port)
$ErrorActionPreference='Stop'
try {
 try { $values=Get-Content -LiteralPath $EnvironmentFile -Raw | ConvertFrom-Json }
 finally { Remove-Item -LiteralPath $EnvironmentFile -Force -ErrorAction SilentlyContinue }
 foreach ($property in $values.PSObject.Properties) { [Environment]::SetEnvironmentVariable($property.Name,[string]$property.Value,'Process') }
 # Retarget desktop-tool paths before retiring the old runtime copy.
 $cfg=Get-Content -LiteralPath (Join-Path $Root 'bridge/router-config.json') -Raw|ConvertFrom-Json
 $oldBases=@((Join-Path $Root 'desktop-app'))
 $pathRecord=Join-Path $Root 'bridge/runtime-paths.json'
 if (Test-Path -LiteralPath $pathRecord) {
  $prior=Get-Content -LiteralPath $pathRecord -Raw|ConvertFrom-Json
  if ($prior.appDirectory) { $oldBases+=$prior.appDirectory }
 }
 $newBase=Split-Path $Executable
 # Use the engine bundled with the discovered installation, not a cached version path.
 $engine=Join-Path $newBase 'resources/codex.exe'
 if (!(Test-Path -LiteralPath $engine -PathType Leaf)) { throw 'Installed Codex engine is missing.' }
 $cfg.engine=$engine
 $cfg|ConvertTo-Json -Depth 30|Set-Content -LiteralPath (Join-Path $Root 'bridge/router-config.json')

 $homes=@($cfg.sharedStore)+@($cfg.profiles.PSObject.Properties|ForEach-Object {$_.Value.home})
 $backup=Join-Path $Root 'settings-backups/runtime-injection'
 New-Item -ItemType Directory -Path $backup -Force|Out-Null
 $index=0
 foreach ($homeDir in $homes) {
  $configPath=Join-Path $homeDir 'config.toml'
  if (!(Test-Path -LiteralPath $configPath)) { continue }
  $original=[IO.File]::ReadAllText($configPath);$updated=$original
  # Only the MU desktop launch environment uses ProtocolBridge. Tool subprocesses
  # and normal Codex launches must retain the actual installed CLI.
  $updated=[regex]::Replace($updated, '(?m)^CODEX_CLI_PATH\s*=.*$', { param($match) "CODEX_CLI_PATH = '"+$engine+"'" })
  foreach ($oldBase in $oldBases) {
   if ($oldBase -eq $newBase) { continue }
   $updated=$updated.Replace($oldBase.Replace('\','\\'),$newBase.Replace('\','\\')).Replace($oldBase,$newBase).Replace($oldBase.Replace('\','/'),$newBase.Replace('\','/'))
  }
  if ($updated -ne $original) {
   $backupFile=Join-Path $backup ('config-'+$index+'.toml')
   if (!(Test-Path -LiteralPath $backupFile)) { [IO.File]::WriteAllText($backupFile,$original) }
   [IO.File]::WriteAllText($configPath,$updated)
  }
  $index++
 }
 @{appDirectory=$newBase}|ConvertTo-Json|Set-Content -LiteralPath $pathRecord
 $argsText='--user-data-dir="'+$UserData+'" --remote-debugging-address=127.0.0.1 --remote-debugging-port='+$Port
 $process=Start-Process -FilePath $Executable -WorkingDirectory $Root -ArgumentList $argsText -WindowStyle Normal -PassThru -RedirectStandardOutput (Join-Path $Root 'bridge/installed-desktop.stdout.log') -RedirectStandardError (Join-Path $Root 'bridge/installed-desktop.stderr.log')
 @{pid=$process.Id;app=$Executable;userData=$UserData;port=$Port;mode='installed-runtime';launchedAt=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds()} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Root 'bridge/installed-desktop-process.json')
} catch {
 $_.Exception.Message | Set-Content -LiteralPath (Join-Path $Root 'bridge/installed-launch-error.log')
 exit 1
}


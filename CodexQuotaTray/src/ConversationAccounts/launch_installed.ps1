param([Parameter(Mandatory=$true)][string]$Root,[Parameter(Mandatory=$true)][string]$UserData,[Parameter(Mandatory=$true)][int]$Port)
$ErrorActionPreference='Stop'
$config=Get-Content -LiteralPath (Join-Path $Root 'bridge/router-config.json') -Raw|ConvertFrom-Json
$packages=@(Get-AppxPackage -Name OpenAI.Codex)
if ($config.packageFamilyName) { $packages=@($packages|Where-Object PackageFamilyName -eq $config.packageFamilyName) }
$pkg=$packages|Select-Object -First 1
$custom=$config.desktopExecutable -and $config.desktopExecutable -notlike '*\WindowsApps\*' -and !$config.packageFamilyName
if ($custom) { $exe=$config.desktopExecutable }
elseif ($pkg) {
 $manifest=Get-AppxPackageManifest -Package $pkg.PackageFullName
 $app=$manifest.Package.Applications.Application | Where-Object Id -eq App | Select-Object -First 1
 if (!$app) { throw 'Installed Codex package has no App application.' }
 $exe=Join-Path $pkg.InstallLocation $app.Executable
} else { throw 'Selected Codex installation is missing. Run ConversationAccounts.exe --setup to select another installation.' }
$relay=Join-Path $Root ('bridge/launch-env-'+[guid]::NewGuid().ToString('N')+'.json')
New-Item -ItemType File -Path $relay | Out-Null
try {
 # Package activation drops the caller's environment. Transfer only launch settings
 # using a short-lived file accessible to the current user and SYSTEM.
 $acl=New-Object Security.AccessControl.FileSecurity
 $acl.SetAccessRuleProtection($true,$false)
 foreach ($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User,(New-Object Security.Principal.SecurityIdentifier('S-1-5-18')))) {
  $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid,'FullControl','Allow')))
 }
 [IO.File]::SetAccessControl($relay,$acl)
 $values=@{}
 foreach ($key in @('CODEX_HOME','CODEX_CLI_PATH','CODEX_SQLITE_HOME','CODEX_ELECTRON_USER_DATA_PATH','CA_PROJECT_ROOT','CA_CONTROL_TOKEN','CA_CONTROL_ENDPOINT','CA_PERSISTENT_RECORD')) {
  $value=[Environment]::GetEnvironmentVariable($key,'Process')
  if ($value) { $values[$key]=$value }
 }
 $values['CODEX_APP_SERVER_FORCE_CLI']='1'
 $values|ConvertTo-Json|Set-Content -LiteralPath $relay
 $helper=Join-Path $Root 'bridge/launch_installed_child.ps1'
 $argsText='--hidden-powershell -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "'+$helper+'" -EnvironmentFile "'+$relay+'" -Executable "'+$exe+'" -Root "'+$Root+'" -UserData "'+$UserData+'" -Port '+$Port
 if ($custom) {
  & (Join-Path $Root 'bridge/invisible-launcher/InvisibleLauncher.exe') --hidden-powershell -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File $helper -EnvironmentFile $relay -Executable $exe -Root $Root -UserData $UserData -Port $Port
  if ($LASTEXITCODE -ne 0) { throw 'Custom Codex launch failed.' }
 } else {
  Invoke-CommandInDesktopPackage -PackageFamilyName $pkg.PackageFamilyName -AppId $app.Id -Command (Join-Path $Root 'bridge/invisible-launcher/InvisibleLauncher.exe') -Args $argsText -PreventBreakaway | Out-Null
 }
 $deadline=[DateTime]::UtcNow.AddSeconds(20)
 do {
  $metadataPath=Join-Path $Root 'bridge/installed-desktop-process.json'
  if (Test-Path -LiteralPath $metadataPath) {
   try { $metadata=Get-Content -LiteralPath $metadataPath -Raw|ConvertFrom-Json; if ($metadata.port -eq $Port) { return } } catch {}
  }
  Start-Sleep -Milliseconds 200
 } while ([DateTime]::UtcNow -lt $deadline)
 throw 'Installed app launch timed out. See bridge/installed-launch-error.log.'
} finally { if (Test-Path -LiteralPath $relay) { Remove-Item -LiteralPath $relay -Force } }



param([Parameter(Mandatory=$true)][string]$Root)
$ErrorActionPreference='Stop'
$package=[IO.Path]::GetFullPath($Root).TrimEnd('\')
$copy=[IO.Path]::GetFullPath((Join-Path $package 'desktop-app'))
if ($copy -ne ($package+'\desktop-app')) { throw 'Unexpected copied-app path.' }
if (!(Test-Path -LiteralPath $copy)) { exit 0 }
$entry=Get-Item -LiteralPath $copy -Force
if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing to remove a linked desktop folder.' }
$metadata=Get-Content -LiteralPath (Join-Path $package 'bridge/desktop-process.json') -Raw|ConvertFrom-Json
if ($metadata.mode -ne 'installed-runtime' -or $metadata.app.StartsWith($copy+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Installed-app launch not confirmed.' }
$active=Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($copy+'\',[StringComparison]::OrdinalIgnoreCase) }
if ($active) { Write-Output 'Copied app is still in use; cleanup deferred.'; exit 0 }
# Final absolute target is fixed inside the explicit package directory and checked above.
Remove-Item -LiteralPath $copy -Recurse -Force
Write-Output 'Retired copied desktop app after installed runtime controls became ready.'

$ErrorActionPreference = 'Stop'
$trayRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$staging = Join-Path $trayRoot '.agents_tmp\tray-update'
& dotnet publish (Join-Path $trayRoot 'CodexQuotaTray.csproj') -c Release --no-self-contained -o $staging --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed; the running switcher was left untouched.' }
if (!(Test-Path -LiteralPath (Join-Path $staging 'CodexQuotaTray.exe') -PathType Leaf)) { throw 'Published executable is missing.' }
foreach ($trayProcess in (Get-Process CodexQuotaTray -ErrorAction SilentlyContinue)) {
    if ($trayProcess.Path -and $trayProcess.Path.StartsWith($trayRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        Stop-Process -Id $trayProcess.Id -Force
        if (!$trayProcess.WaitForExit(10000)) { throw 'The previous switcher did not exit.' }
        $trayProcess.Dispose()
    }
}
$destination = Join-Path $trayRoot 'publish'
if (Test-Path -LiteralPath $destination -PathType Leaf) { throw "Expected a folder: $destination" }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Get-ChildItem -LiteralPath $staging -File | Where-Object { $_.Extension -in @('.exe', '.dll', '.json', '.pdb') } | Copy-Item -Destination $destination -Force
$finalExecutable = [IO.Path]::GetFullPath((Join-Path $destination 'CodexQuotaTray.exe'))
Get-ChildItem -LiteralPath $trayRoot -Recurse -File -Filter 'CodexQuotaTray.exe' | ForEach-Object {
    $duplicate = [IO.Path]::GetFullPath($_.FullName)
    if (!$duplicate.StartsWith($trayRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Executable outside project folder' }
    if (![string]::Equals($duplicate, $finalExecutable, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $duplicate -Force }
}
$stagingPath = [IO.Path]::GetFullPath($staging)
if (!$stagingPath.StartsWith($trayRoot + '\.agents_tmp\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Staging folder outside project temporary folder' }
Remove-Item -LiteralPath $stagingPath -Recurse -Force
$published = Join-Path $trayRoot 'publish\CodexQuotaTray.exe'
$shellApplication = New-Object -ComObject Shell.Application
$shellApplication.ShellExecute($published, '--tray', (Split-Path -Parent $published), 'open', 0)

param([switch]$Android)
$ErrorActionPreference = 'Stop'
$candidates = @()
$cmdDotnet = (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1)
if ($cmdDotnet) { $candidates += $cmdDotnet }
if ($env:DOTNET_ROOT) { $candidates += (Join-Path $env:DOTNET_ROOT 'dotnet.exe') }
$candidates += 'C:\Program Files\dotnet\dotnet.exe'
$candidates += (Get-ChildItem 'Q:\_coding\tools\dotnet*\dotnet.exe' -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -ExpandProperty FullName)
$dotnet = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) -and (Test-Path -LiteralPath (Join-Path (Split-Path $_ -Parent) 'sdk')) } | Select-Object -First 1
if (!$dotnet) { throw 'dotnet SDK not found.' }
$destination = Join-Path $PSScriptRoot 'publish'
& $dotnet publish (Join-Path $PSScriptRoot 'Nightcall.csproj') -c Release -r win-x64 --self-contained false -p:PlatformTarget=x64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $destination --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Windows build failed' }
if ($Android) {
    Push-Location (Join-Path $PSScriptRoot 'android')
    try {
        & .\gradlew.bat --console=plain assembleDebug
        if ($LASTEXITCODE -ne 0) { throw 'Android build failed' }
        Copy-Item -LiteralPath 'app\build\outputs\apk\debug\app-debug.apk' -Destination (Join-Path $destination 'Nightcall.apk')
    } finally { Pop-Location }
}
# Keep only the permanent launchable executable in searches.
foreach ($buildFolder in @('bin', 'obj')) {
    $buildPath = Join-Path $PSScriptRoot $buildFolder
    if (Test-Path -LiteralPath $buildPath) {
        Get-ChildItem -LiteralPath $buildPath -Recurse -File | Where-Object { $_.Name -in @('Nightcall.exe', 'apphost.exe') } | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
    }
}
Write-Output (Join-Path $destination 'Nightcall.exe')
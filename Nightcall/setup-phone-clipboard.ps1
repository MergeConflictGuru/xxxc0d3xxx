<#
.SYNOPSIS
    Pairs/connects an Android phone over Wireless Debugging (or uses an existing USB/Wi-Fi ADB device)
    and grants Nightcall the one-time permissions needed for automatic background clipboard sync.

.EXAMPLE
    .\setup-phone-clipboard.ps1
    .\setup-phone-clipboard.ps1 -PhoneIp 192.168.1.50 -PairPort 37123 -PairCode 123456 -ConnectPort 41829
    .\setup-phone-clipboard.ps1 -PhoneIp 192.168.1.50 -ConnectPort 41829
#>
param(
    [string]$PhoneIp = '',
    [string]$PairPort = '',
    [string]$PairCode = '',
    [string]$ConnectPort = '',
    [switch]$InstallApk
)

$ErrorActionPreference = 'Stop'
$pkg = 'local.dictationrelay'

function Find-Adb {
    $cmd = Get-Command adb.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidates = @()
    if ($env:ANDROID_HOME) { $candidates += (Join-Path $env:ANDROID_HOME 'platform-tools\adb.exe') }
    if ($env:ANDROID_SDK_ROOT) { $candidates += (Join-Path $env:ANDROID_SDK_ROOT 'platform-tools\adb.exe') }
    $candidates += 'Q:\_coding\tools\android_sdk\Sdk\platform-tools\adb.exe'
    if ($env:LOCALAPPDATA) { $candidates += (Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe') }
    foreach ($p in $candidates) {
        if ($p -and (Test-Path -LiteralPath $p)) { return $p }
    }
    throw 'adb.exe not found. Install Android Platform-Tools or set ANDROID_HOME.'
}

function Get-OnlineDevices([string]$adb) {
    $lines = & $adb devices 2>$null
    $result = @()
    foreach ($line in $lines) {
        if ($line -match '^(\S+)\s+device$') { $result += $Matches[1] }
    }
    return $result
}

function Find-MdnsConnectTarget([string]$adb, [string]$preferIp) {
    $lines = & $adb mdns services 2>$null
    foreach ($line in $lines) {
        if ($line -match '_adb-tls-connect\._tcp\s+(\d+\.\d+\.\d+\.\d+:\d+)') {
            $addr = $Matches[1]
            if (-not $preferIp -or $addr.StartsWith("$preferIp`:")) { return $addr }
        }
    }
    return ''
}

function Normalize-Endpoint([string]$inputVal, [string]$defaultIp) {
    $v = $inputVal.Trim()
    if (-not $v) { return '' }
    if ($v -match '^\d+$') {
        if (-not $defaultIp) { throw "Port '$v' given without an IP address." }
        return "$defaultIp`:$v"
    }
    return $v
}

$adb = Find-Adb
Write-Host "Using ADB: $adb" -ForegroundColor DarkGray
& $adb start-server | Out-Null

$serial = ''
$existing = Get-OnlineDevices $adb
if ($existing.Count -eq 1 -and -not $PhoneIp -and -not $ConnectPort -and -not $PairPort) {
    Write-Host "Found connected device: $($existing[0])" -ForegroundColor Cyan
    $use = Read-Host "Use this device? [Y/n]"
    if (-not $use -or $use -match '^[Yy]') { $serial = $existing[0] }
} elseif ($existing.Count -gt 1 -and -not $PhoneIp -and -not $ConnectPort -and -not $PairPort) {
    Write-Host "Connected devices:" -ForegroundColor Cyan
    for ($i = 0; $i -lt $existing.Count; $i++) { Write-Host "  [$($i+1)] $($existing[$i])" }
    $pick = Read-Host "Pick a device number (or press Enter for Wi-Fi setup)"
    if ($pick -match '^\d+$' -and [int]$pick -ge 1 -and [int]$pick -le $existing.Count) {
        $serial = $existing[[int]$pick - 1]
    }
}

if (-not $serial) {
    Write-Host ""
    Write-Host "On your phone: Settings -> Developer options -> Wireless debugging" -ForegroundColor Yellow

    # Step 1: Optional pairing
    if (-not $PairPort -and -not $ConnectPort) {
        $pairInput = Read-Host "Pairing IP:port (from 'Pair device with pairing code', or press Enter if already paired)"
        if ($pairInput.Trim()) {
            if ($pairInput -match '^(\d+\.\d+\.\d+\.\d+):(\d+)$') {
                $PhoneIp = $Matches[1]
                $PairPort = $Matches[2]
            } else {
                $PairPort = $pairInput.Trim()
            }
        }
    }

    if ($PairPort) {
        if (-not $PhoneIp -and $PairPort -notmatch ':') {
            $PhoneIp = (Read-Host "Phone IP address (e.g. 192.168.1.50)").Trim()
        }
        $pairEndpoint = Normalize-Endpoint $PairPort $PhoneIp
        if (-not $PhoneIp -and $pairEndpoint -match '^(\d+\.\d+\.\d+\.\d+):') { $PhoneIp = $Matches[1] }
        if (-not $PairCode) { $PairCode = (Read-Host "6-digit Wi-Fi pairing code").Trim() }
        Write-Host "Pairing with $pairEndpoint..." -ForegroundColor Cyan
        $pairOut = & $adb pair $pairEndpoint $PairCode 2>&1
        Write-Host ($pairOut -join "`n")
        if ($LASTEXITCODE -ne 0 -or (($pairOut -join ' ') -notmatch 'Successfully paired')) {
            throw "ADB pairing failed."
        }
        Start-Sleep -Milliseconds 800
    }

    # Step 2: Connect (try mDNS auto-discovery first)
    $connectEndpoint = ''
    if ($ConnectPort) {
        if (-not $PhoneIp -and $ConnectPort -notmatch ':') {
            $PhoneIp = (Read-Host "Phone IP address (e.g. 192.168.1.50)").Trim()
        }
        $connectEndpoint = Normalize-Endpoint $ConnectPort $PhoneIp
    } else {
        $mdns = Find-MdnsConnectTarget $adb $PhoneIp
        if ($mdns) {
            Write-Host "Auto-detected Wireless Debugging address via mDNS: $mdns" -ForegroundColor Green
            $connectEndpoint = $mdns
        } else {
            $hint = if ($PhoneIp) { "Connect port (or IP:port) from main Wireless debugging screen [IP: $PhoneIp]" } else { "Connect IP:port from main Wireless debugging screen" }
            $connInput = Read-Host $hint
            $connectEndpoint = Normalize-Endpoint $connInput $PhoneIp
        }
    }

    if (-not $connectEndpoint) { throw "No connect address provided." }
    Write-Host "Connecting to $connectEndpoint..." -ForegroundColor Cyan
    $connOut = & $adb connect $connectEndpoint 2>&1
    Write-Host ($connOut -join "`n")
    if ($LASTEXITCODE -ne 0 -or (($connOut -join ' ') -notmatch 'connected to')) {
        throw "ADB connect failed."
    }
    $serial = $connectEndpoint
}

# Step 3: Ensure Nightcall is installed (or install APK if requested)
$apkPath = Join-Path $PSScriptRoot 'publish\Nightcall.apk'
$installed = (& $adb -s $serial shell pm path $pkg 2>$null) -join ''
if ($InstallApk -or -not $installed) {
    if (Test-Path -LiteralPath $apkPath) {
        Write-Host "Installing $apkPath..." -ForegroundColor Cyan
        & $adb -s $serial install -r $apkPath
        if ($LASTEXITCODE -ne 0) { throw "APK install failed." }
    } elseif (-not $installed) {
        throw "Nightcall ($pkg) is not installed on the phone and $apkPath was not found."
    }
}

# Step 4: Grant background clipboard permissions
Write-Host "Granting READ_LOGS and SYSTEM_ALERT_WINDOW to $pkg..." -ForegroundColor Cyan
& $adb -s $serial shell pm grant $pkg android.permission.READ_LOGS
if ($LASTEXITCODE -ne 0) { throw "Failed to grant READ_LOGS." }

& $adb -s $serial shell appops set $pkg SYSTEM_ALERT_WINDOW allow
if ($LASTEXITCODE -ne 0) { throw "Failed to allow SYSTEM_ALERT_WINDOW." }

# Optional: supported on some OEM ROMs / older Android builds; ignore if unsupported on AOSP 14+
& $adb -s $serial shell appops set $pkg READ_CLIPBOARD_IN_BACKGROUND allow 2>$null | Out-Null

Write-Host "Restarting Nightcall so permissions take effect..." -ForegroundColor Cyan
& $adb -s $serial shell am force-stop $pkg

# Step 5: Verify
$appop = (& $adb -s $serial shell appops get $pkg SYSTEM_ALERT_WINDOW 2>$null) -join ' '
$perm = (& $adb -s $serial shell dumpsys package $pkg 2>$null | Select-String 'android.permission.READ_LOGS: granted=true')
Write-Host ""
Write-Host "=== Verification ===" -ForegroundColor Green
Write-Host "Device:               $serial"
Write-Host "READ_LOGS:            $(if ($perm) { 'GRANTED' } else { 'check manually' })"
Write-Host "SYSTEM_ALERT_WINDOW:  $($appop.Trim())"
Write-Host ""
Write-Host "Done! Background phone -> PC clipboard sync is now active whenever Nightcall is connected." -ForegroundColor Green

param([switch]$Elevated)
$ErrorActionPreference = 'Stop'
if (!$Elevated) {
    $taskArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $PSCommandPath + '"'), '-Elevated')
    $admin = Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -ArgumentList $taskArgs -Wait -PassThru
    if ($admin.ExitCode -ne 0) { throw 'Windows did not apply the local-phone firewall exception.' }
    return
}
$appPath = Join-Path $PSScriptRoot 'publish\Nightcall.exe'
if (!(Test-Path -LiteralPath $appPath)) { throw 'Build Nightcall first with build.ps1.' }
# Windows-created explicit blocks override an allow rule, including on hotspots
# classified as Public. Remove only inbound blocks for this exact executable.
$programFilters = Get-NetFirewallApplicationFilter | Where-Object { $_.Program -ieq $appPath }
foreach ($filter in $programFilters) {
    $filter | Get-NetFirewallRule | Where-Object { $_.Direction -eq 'Inbound' -and $_.Action -eq 'Block' } | Remove-NetFirewallRule
}
$existing = Get-NetFirewallRule -Name 'Nightcall-LocalPhones' -ErrorAction SilentlyContinue
if ($existing) { $existing | Remove-NetFirewallRule }
New-NetFirewallRule -Name 'Nightcall-LocalPhones' -DisplayName 'Nightcall (local phones)' -Direction Inbound -Action Allow -Program $appPath -Protocol TCP -RemoteAddress LocalSubnet -Profile Any | Out-Null
Write-Output 'Nightcall now allows TCP connections from the local subnet on all network profiles.'

param([Parameter(Mandatory=$true)][string]$Config)
$ErrorActionPreference = 'Stop'
$cfg = Get-Content -LiteralPath $Config -Raw | ConvertFrom-Json
$store = [IO.Path]::GetFullPath($cfg.sharedStore)
$package = Split-Path (Split-Path ([IO.Path]::GetFullPath($Config)))
$profilesRoot = [IO.Path]::GetFullPath((Join-Path $package 'profiles'))
$backupRoot = Join-Path $package 'shared-home-backup'
$folders = @('sessions','archived_sessions','generated_images','skills','rules','plugins/cache','plugins/.plugin-appserver','.sandbox-bin','visualizations','pets','vendor_imports','attachments','packages')
$report = [Collections.Generic.List[object]]::new()
foreach ($profile in $cfg.profiles.PSObject.Properties) {
    $homeDir = [IO.Path]::GetFullPath($profile.Value.home)
    if (-not $homeDir.StartsWith($profilesRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Profile outside package: $homeDir" }
    foreach ($folder in $folders) {
        $source = [IO.Path]::GetFullPath((Join-Path $homeDir $folder))
        $target = [IO.Path]::GetFullPath((Join-Path $store $folder))
        $backup = [IO.Path]::GetFullPath((Join-Path (Join-Path $backupRoot $profile.Name) $folder))
        if (-not $source.StartsWith($homeDir + '\') -or -not $target.StartsWith($store + '\') -or -not $backup.StartsWith($backupRoot + '\')) { throw 'Invalid migration path' }
        if (Test-Path -LiteralPath $source) {
            $entry = Get-Item -LiteralPath $source -Force
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                if ([IO.Path]::GetFullPath($entry.Target) -ne $target) { throw "Unexpected link: $source" }
                continue
            }
        }
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        if (Test-Path -LiteralPath $source) {
            if (Test-Path -LiteralPath $backup) { throw "Backup already exists: $backup" }
            New-Item -ItemType Directory -Path (Split-Path $backup) -Force | Out-Null
            # All recursive moves stay inside the validated package paths above.
            try { Move-Item -LiteralPath $source -Destination $backup }
            catch {
                $report.Add([pscustomobject]@{Account=$profile.Name;Folder=$folder;Pending=$true;Reason=$_.Exception.Message})
                Write-Output "$($profile.Name) $folder pending until restart (in use)"
                continue
            }
        }
        try { New-Item -ItemType Junction -Path $source -Target $target | Out-Null }
        catch {
            if (Test-Path -LiteralPath $backup) { Move-Item -LiteralPath $backup -Destination $source }
            throw
        }
        $conflicts = 0; $added = 0; $deduped = 0
        if (Test-Path -LiteralPath $backup) {
            foreach ($file in Get-ChildItem -LiteralPath $backup -File -Recurse) {
                $relative = $file.FullName.Substring($backup.Length + 1)
                $dest = Join-Path $target $relative
                New-Item -ItemType Directory -Path (Split-Path $dest) -Force | Out-Null
                if (-not (Test-Path -LiteralPath $dest)) {
                    New-Item -ItemType HardLink -Path $dest -Target $file.FullName | Out-Null
                    $added++
                } else {
                    $destFile = Get-Item -LiteralPath $dest
                    if ($destFile.Length -eq $file.Length -and (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) {
                        # Preserve backup paths while releasing byte-identical duplicate allocations.
                        try {
                            $staged = $file.FullName + '.dedup-link'
                            New-Item -ItemType HardLink -Path $staged -Target $dest | Out-Null
                            try { Move-Item -LiteralPath $staged -Destination $file.FullName -Force; $deduped++ }
                            finally { if (Test-Path -LiteralPath $staged) { Remove-Item -LiteralPath $staged -Force } }
                        } catch { Write-Warning "Kept in-use duplicate: $($file.FullName)" }
                    } else {
                        $conflicts++
                        # Preserve divergent histories under a searchable global path as well.
                        if ($folder -in @('sessions','archived_sessions')) {
                            $variant = Join-Path (Join-Path $target ('account-imports/' + $profile.Name)) $relative
                            New-Item -ItemType Directory -Path (Split-Path $variant) -Force | Out-Null
                            if (-not (Test-Path -LiteralPath $variant)) { New-Item -ItemType HardLink -Path $variant -Target $file.FullName | Out-Null }
                        }
                    }
                }
            }
        }
        $report.Add([pscustomobject]@{Account=$profile.Name;Folder=$folder;Added=$added;Deduplicated=$deduped;ConflictsPreserved=$conflicts})
        Write-Output "$($profile.Name) $folder shared; added=$added duplicates=$deduped conflicts=$conflicts"
    }
}
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $package 'shared-home-migration.json')



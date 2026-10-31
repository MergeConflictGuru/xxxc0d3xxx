param([switch]$PackageChild)
$log=Join-Path $PSScriptRoot 'InvisibleLauncher.child.log'

                $ErrorActionPreference='Stop'
                function Log($message) {
                    $line='['+[DateTimeOffset]::Now.ToString('O')+'] powershell pid='+$PID+': '+$message+[Environment]::NewLine
                    try { [IO.File]::AppendAllText($log,$line) }
                    catch { $script:log=Join-Path $env:LOCALAPPDATA 'InvisibleLauncher.child.log'; [IO.File]::AppendAllText($script:log,$line) }
                }
                try {
                    Log ('BEGIN user='+[Security.Principal.WindowsIdentity]::GetCurrent().Name+'; PowerShell='+$PSVersionTable.PSVersion+'; session='+[Diagnostics.Process]::GetCurrentProcess().SessionId)
                    $p=Get-AppxPackage -Name OpenAI.Codex -ErrorAction Stop | Select-Object -First 1
                    if (!$p) { throw 'OpenAI.Codex is not installed for this user.' }
                    Log ('Package='+$p.PackageFullName+'; location='+$p.InstallLocation)
                    $m=Get-AppxPackageManifest -Package $p.PackageFullName
                    $a=$m.Package.Applications.Application | Where-Object Id -eq App | Select-Object -First 1
                    if (!$a) { throw 'The Codex package manifest contains no Application with Id=App.' }
                    $exe=Join-Path $p.InstallLocation $a.Executable
                    if ($PackageChild) {
                        # A cached/deleted working directory can produce spawn ENOENT
                        # even when the CLI executable exists. Use this user's real home.
                        $start = New-Object Diagnostics.ProcessStartInfo
                        $start.FileName=$exe
                        $start.WorkingDirectory=[Environment]::GetFolderPath('UserProfile')
                        $start.UseShellExecute=$false
                        $start.CreateNoWindow=$true
                        $start.WindowStyle='Normal'
                        foreach ($key in @($start.EnvironmentVariables.Keys)) {
                            if ($key -match '^(CA_|CODEX_APP_TOOLS_)' -or $key -in @('CODEX_HOME','CODEX_CLI_PATH','CODEX_SQLITE_HOME','CODEX_ELECTRON_USER_DATA_PATH','CODEX_APP_SERVER_WS_URL','CODEX_APP_SERVER_FORCE_CLI','CODEX_THREAD_ID','NODE_OPTIONS')) {
                                $start.EnvironmentVariables.Remove($key)
                            }
                        }
                        $appProcess=[Diagnostics.Process]::Start($start)
                        Log ('GUI started pid='+$appProcess.Id+'; windowStyle='+$start.WindowStyle+'; cwd='+$start.WorkingDirectory)
                        exit 0
                    }
                    $relay=Join-Path $PSScriptRoot 'InvisibleLauncher.exe'
                    $arguments='--hidden-powershell -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "'+$PSCommandPath+'" -PackageChild'
                    $result=Invoke-CommandInDesktopPackage -PackageFamilyName $p.PackageFamilyName -AppId $a.Id -Command $relay -Args $arguments -PreventBreakaway
                    Log ('Launch command returned: '+($result | Out-String))
                    Log ('Codex processes: '+(Get-Process ChatGPT -ErrorAction SilentlyContinue | Select-Object Id,SessionId,Path | Out-String))
                    Log 'END success'
                    exit 0
                }
                catch {
                    Log ('ERROR: '+($_ | Format-List * -Force | Out-String))
                    Log ('EXCEPTION: '+$_.Exception.ToString()+'; scriptStack='+$_.ScriptStackTrace)
                    exit 1
                }
                

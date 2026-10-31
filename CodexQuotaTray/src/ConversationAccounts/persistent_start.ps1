param([Parameter(Mandatory=$true)][string]$Root)
$ErrorActionPreference='Stop'
try {
 $path=Join-Path $Root 'bridge/router-config.json'
 $config=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json
 $packages=@(Get-AppxPackage -Name OpenAI.Codex)
 if ($config.packageFamilyName) { $packages=@($packages|Where-Object PackageFamilyName -eq $config.packageFamilyName) }
 $pkg=$packages|Select-Object -First 1
 if (!$pkg) {throw 'Selected Codex package is missing. Run ConversationAccounts.exe --setup.'}
 $engine=Join-Path $pkg.InstallLocation 'app/resources/codex.exe'
 if (!(Test-Path -LiteralPath $engine -PathType Leaf)) {throw 'Installed Codex engine missing'}
 $config.engine=$engine
 $config|ConvertTo-Json -Depth 30|Set-Content -LiteralPath $path
 $bridge=Get-Content -LiteralPath (Join-Path $Root 'bridge/bridge-config.json') -Raw|ConvertFrom-Json
 $python=Join-Path (Split-Path $bridge.engine) 'pythonw.exe'
 $arguments='"'+(Join-Path $Root 'bridge/persistent.py')+'" serve "'+$Root+'"'
 Invoke-CommandInDesktopPackage -PackageFamilyName $pkg.PackageFamilyName -AppId App -Command $python -Args $arguments -PreventBreakaway|Out-Null
} catch {
 $_.Exception.ToString()|Set-Content -LiteralPath (Join-Path $Root '.app_tmp/persistent/start-error.log')
 exit 1
}

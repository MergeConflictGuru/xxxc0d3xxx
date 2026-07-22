param(
    [Parameter(Mandatory = $true)][string]$FilePath,
    [Parameter(Mandatory = $true)][string]$TextPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

$resolvedFile = (Resolve-Path -LiteralPath $FilePath).Path
$text = [System.IO.File]::ReadAllText(
    (Resolve-Path -LiteralPath $TextPath).Path,
    [System.Text.UTF8Encoding]::new($false)
)

$data = [System.Windows.Forms.DataObject]::new()
$files = [System.Collections.Specialized.StringCollection]::new()
[void]$files.Add($resolvedFile)

# Put the file representation first as the preferred/richest representation,
# followed by Unicode text as the fallback for text-only receivers.
$data.SetFileDropList($files)
$data.SetText($text, [System.Windows.Forms.TextDataFormat]::UnicodeText)
[System.Windows.Forms.Clipboard]::SetDataObject($data, $true)

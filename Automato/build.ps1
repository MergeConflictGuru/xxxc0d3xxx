param(
    [switch]$Install,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ProjectRoot

function Require-Command {
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "'$Name' was not found. Install Node.js LTS and reopen the terminal."
    }
}

Require-Command "node"
Require-Command "npm"

$PackageJsonPath = Join-Path $ProjectRoot "package.json"

if (-not (Test-Path $PackageJsonPath -PathType Leaf)) {
    throw @"
package.json was not found in:

    $ProjectRoot

Put build-automato.ps1 in the root of the Automato source directory.
"@
}

$Package = Get-Content $PackageJsonPath -Raw | ConvertFrom-Json
$Name = if ($Package.name) { [string]$Package.name } else { "automato" }
$Version = if ($Package.version) { [string]$Package.version } else { "local" }

$VsixPath = Join-Path $ProjectRoot "$Name-$Version.vsix"

Write-Host ""
Write-Host "Building $Name $Version" -ForegroundColor Cyan
Write-Host "Project: $ProjectRoot"
Write-Host ""

if (Test-Path (Join-Path $ProjectRoot "package-lock.json")) {
    Write-Host "Installing exact dependencies with npm ci..."
    & npm ci
} else {
    Write-Host "No package-lock.json found; using npm install..."
    & npm install
}

if ($LASTEXITCODE -ne 0) {
    throw "Dependency installation failed with exit code $LASTEXITCODE."
}

$Scripts = $Package.scripts

if ($Scripts -and $Scripts.compile) {
    Write-Host ""
    Write-Host "Compiling TypeScript..."
    & npm run compile
} elseif ($Scripts -and $Scripts.build) {
    Write-Host ""
    Write-Host "Running build script..."
    & npm run build
} else {
    throw "package.json contains neither a 'compile' nor a 'build' script."
}

if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE."
}

if (-not $SkipTests) {
    if ($Scripts -and $Scripts.test) {
        Write-Host ""
        Write-Host "Running tests..."
        & npm test

        if ($LASTEXITCODE -ne 0) {
            throw "Tests failed with exit code $LASTEXITCODE."
        }
    } else {
        Write-Host ""
        Write-Host "No test script found; skipping tests." -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "Packaging VSIX..."

if (Test-Path $VsixPath) {
    Remove-Item $VsixPath -Force
}

& npx --yes "@vscode/vsce" package --out $VsixPath --allow-missing-repository

if ($LASTEXITCODE -ne 0) {
    throw "VSIX packaging failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path $VsixPath -PathType Leaf)) {
    throw "The packaging command finished without creating $VsixPath."
}

Write-Host ""
Write-Host "Build completed:" -ForegroundColor Green
Write-Host $VsixPath -ForegroundColor Green

if ($Install) {
    Require-Command "code"

    Write-Host ""
    Write-Host "Installing into VS Code..."

    & code --install-extension $VsixPath --force

    if ($LASTEXITCODE -ne 0) {
        throw "VS Code installation failed with exit code $LASTEXITCODE."
    }

    Write-Host "Automato installed. Reload VS Code." -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "To install it, run:"
    Write-Host "code --install-extension `"$VsixPath`" --force"
}
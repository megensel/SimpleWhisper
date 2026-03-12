# SimpleWhisper Build Script
# Publishes the app and optionally builds the Inno Setup installer.
#
# Usage:
#   .\build.ps1                  # Full build: publish + installer
#   .\build.ps1 -InstallerOnly   # Skip dotnet publish, just build installer
#   .\build.ps1 -PublishOnly     # Only dotnet publish, skip installer

param(
    [switch]$InstallerOnly,
    [switch]$PublishOnly,
    [string]$InnoPath = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
)

$ErrorActionPreference = "Stop"
$projectDir = "src\SimpleWhisper"
$publishDir = "$projectDir\bin\publish"

Write-Host ""
Write-Host "=== SimpleWhisper Build ===" -ForegroundColor White
Write-Host ""

# Step 1: Publish the .NET application
if (-not $InstallerOnly) {
    Write-Host "[1/2] Publishing SimpleWhisper..." -ForegroundColor Cyan

    # Clean previous publish output
    if (Test-Path $publishDir) {
        Remove-Item -Recurse -Force $publishDir
    }

    dotnet publish $projectDir `
        -c Release `
        -r win-x64 `
        --self-contained `
        -o $publishDir `
        /p:PublishSingleFile=false

    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: dotnet publish failed with exit code $LASTEXITCODE" -ForegroundColor Red
        exit $LASTEXITCODE
    }

    $fileCount = (Get-ChildItem -Recurse -File $publishDir).Count
    $sizeBytes = (Get-ChildItem -Recurse -File $publishDir | Measure-Object -Property Length -Sum).Sum
    $sizeMB = [math]::Round($sizeBytes / 1MB, 1)
    Write-Host "Published $fileCount files ($sizeMB MB) to $publishDir" -ForegroundColor Green
}

if ($PublishOnly) {
    Write-Host ""
    Write-Host "Done (publish only)." -ForegroundColor Green
    exit 0
}

# Step 2: Build the Inno Setup installer
Write-Host ""
if (Test-Path $InnoPath) {
    Write-Host "[2/2] Building installer..." -ForegroundColor Cyan
    & $InnoPath "installer\SimpleWhisper.iss"

    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Inno Setup compiler failed with exit code $LASTEXITCODE" -ForegroundColor Red
        exit $LASTEXITCODE
    }

    $installerFile = Get-ChildItem "installer\output\*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($installerFile) {
        $installerSizeMB = [math]::Round($installerFile.Length / 1MB, 1)
        Write-Host "Installer: $($installerFile.FullName) ($installerSizeMB MB)" -ForegroundColor Green
    }
} else {
    Write-Host "[2/2] Inno Setup not found at:" -ForegroundColor Yellow
    Write-Host "      $InnoPath" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "      Download from: https://jrsoftware.org/isinfo.php" -ForegroundColor Yellow
    Write-Host "      Then re-run this script to build the installer." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green

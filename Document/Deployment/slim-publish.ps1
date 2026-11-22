# AggPanSearchAdmin Release slimming + standard zip pack script (Windows)
# Reuses verified Win-only slimming and zip-packing logic from pack-publish.ps1.
# Idempotent: safe to run repeatedly on already-published Admin output.
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\Document\Deployment\slim-publish.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\Document\Deployment\slim-publish.ps1 -PublishDir "D:\path\to\Publish"
# Flow: remove non win/win-x64 runtimes RID dirs -> delete old Publish.zip -> pack Admin as standard zip.
param(
    [string]$PublishDir
)

$ErrorActionPreference = 'Stop'

# 1) Resolve publish dir: default to repo-root/Publish when not specified
if (-not $PublishDir) {
    $repo     = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $PublishDir = Join-Path $repo 'Publish'
}
if (-not (Test-Path $PublishDir)) {
    throw "PublishDir not found: $PublishDir"
}
$zipPath = Join-Path $PublishDir 'Publish.zip'
$targets = 'Admin'
foreach ($t in $targets) {
    if (-not (Test-Path (Join-Path $PublishDir $t))) {
        throw "Missing publish output: $(Join-Path $PublishDir $t)"
    }
}

# 2) Keep only Windows x64 native assets (Win-only slimming)
# Full FDD publish brings android/linux/osx/unix/win-x86/win-arm64 RID dirs which are
# useless for Windows deployment. Keep runtimes\win (managed fallback) and
# runtimes\win-x64 (native: libSkiaSharp.dll, SqlClient.SNI.dll, sni.dll).
foreach ($dir in $targets) {
    $rt = Join-Path $PublishDir "$dir\runtimes"
    if (-not (Test-Path $rt)) { continue }
    Get-ChildItem $rt -Directory |
        Where-Object { $_.Name -notin @('win', 'win-x64') } |
        ForEach-Object {
            Remove-Item $_.FullName -Recurse -Force -ErrorAction Stop
            Write-Host "  [slim] $dir : removed runtimes\$($_.Name)"
        }
}

# 3) Remove old package
Remove-Item $zipPath -Force -ErrorAction SilentlyContinue

# 4) Standard zip packing (forward-slash entries; extractable by Explorer/WinRAR)
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($top in $targets) {
        $root  = Join-Path $PublishDir $top
        $files = Get-ChildItem $root -Recurse -File
        $count = 0
        foreach ($f in $files) {
            $rel  = $f.FullName.Substring($root.Length + 1)
            $name = "$top/" + ($rel -replace '\\', '/')
            $entry = $zip.CreateEntry($name, [System.IO.Compression.CompressionLevel]::Optimal)
            $es = $entry.Open()
            $stream = [System.IO.File]::OpenRead($f.FullName)
            try { $stream.CopyTo($es) } finally { $stream.Dispose(); $es.Dispose() }
            $count++
        }
        Write-Host "  $top : $count files"
    }
} finally {
    $zip.Dispose()
}

$sizeMB = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host "Done: Publish.zip ($sizeMB MB)"
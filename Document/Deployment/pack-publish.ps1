# AggPanSearchAdmin Release 出包脚本（Windows）
# 在仓库根目录执行：powershell -NoProfile -ExecutionPolicy Bypass -File .\Document\Deployment\pack-publish.ps1
# 流程：清理旧产物 -> dotnet publish 两个 net10.0 项目 -> 删除 web.config/Resource -> 标准 zip 打包
$ErrorActionPreference = 'Stop'
$repo    = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$publish = Join-Path $repo 'Publish'
$zipPath = Join-Path $publish 'Publish.zip'

# 1) 清理旧产物
foreach ($dir in 'Admin','Api') {
    Remove-Item (Join-Path $publish $dir) -Recurse -Force -ErrorAction SilentlyContinue
}
Remove-Item $zipPath -Force -ErrorAction SilentlyContinue

# 2) 编译两个项目（net10.0 Release）
dotnet publish (Join-Path $repo 'AggPanSearch.Web\AggPanSearch.Admin.Web\AggPanSearch.Admin.Web.csproj')   -c Release -o (Join-Path $publish 'Admin')
dotnet publish (Join-Path $repo 'AggPanSearch.Web\AggPanSearch.Admin.WebApi\AggPanSearch.Admin.WebApi.csproj') -c Release -o (Join-Path $publish 'Api')

# 3) 仅删除 web.config / Resource（IIS 托管配置线上保留；Resource 程序启动自动重建）
foreach ($dir in 'Admin','Api') {
    Remove-Item (Join-Path $publish "$dir\web.config") -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $publish "$dir\Resource")   -Recurse -Force -ErrorAction SilentlyContinue
}

# 3.5) runtimes 仅保留 Windows x64 原生资产（Windows-only 瘦身）
# 全平台 FDD 发布会带 android/linux/osx/unix/win-x86/win-arm64 等 RID 目录，
# 对 Windows 部署无用且占体积（实测约 400MB+），统一删除。
# 保留 runtimes\win（托管回退）与 runtimes\win-x64（native：libSkiaSharp.dll、SqlClient.SNI.dll、sni.dll）。
foreach ($dir in 'Admin','Api') {
    $rt = Join-Path $publish "$dir\runtimes"
    if (-not (Test-Path $rt)) { continue }
    Get-ChildItem $rt -Directory |
        Where-Object { $_.Name -notin @('win', 'win-x64') } |
        ForEach-Object {
            Remove-Item $_.FullName -Recurse -Force -ErrorAction Stop
            Write-Host "  [slim] removed runtimes\$($_.Name)"
        }
}

# 4) 标准 zip 打包（条目使用正斜杠路径，Windows 资源管理器 / WinRAR / 7-Zip 均可直接解压）
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($top in 'Admin','Api') {
        $root  = Join-Path $publish $top
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
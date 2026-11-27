param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
function Remove-GeneratedDirectory([string]$path, [string]$releaseRoot) {
    $fullPath = [IO.Path]::GetFullPath($path)
    $allowedRoot = [IO.Path]::GetFullPath($releaseRoot) + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw '生成目录超出发布范围。' }
    if (Test-Path -LiteralPath $fullPath) {
        $entries = @((Get-Item -LiteralPath $fullPath)) + @(Get-ChildItem -LiteralPath $fullPath -Recurse -Force)
        if (@($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -gt 0) { throw '生成目录包含重解析点，停止清理。' }
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
}
Push-Location -LiteralPath $projectRoot
try {
    & dotnet build AuxLauncher.sln -c Release --nologo -m:1 -p:UseSharedCompilation=false --disable-build-servers -v:minimal
    if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
    if ($Publish) {
        $releaseRoot = Join-Path $projectRoot 'release'
        $destination = Join-Path $releaseRoot 'win-x64'
        $staging = Join-Path $releaseRoot ('.publish-' + [Guid]::NewGuid().ToString('N'))
        $stagedDirectory = Join-Path $staging 'win-x64'
        $stagedZip = Join-Path $staging 'AuxLauncher-win-x64.zip'
        New-Item -ItemType Directory -Force -Path $stagedDirectory | Out-Null
        try {
            foreach ($component in @('AuxLauncher.App', 'AuxLauncher.Guard', 'AuxLauncher.Relay')) {
                & dotnet publish "src/$component/$component.csproj" -c Release --no-build --no-restore --nologo -o $stagedDirectory -v:minimal
                if ($LASTEXITCODE -ne 0) { throw "发布 $component 失败。" }
            }
            Copy-Item -LiteralPath 'vendor/WinDivert/LICENSE' -Destination (Join-Path $stagedDirectory 'WinDivert-LICENSE.txt')
            Copy-Item -LiteralPath 'vendor/WinDivert/NOTICE.md' -Destination (Join-Path $stagedDirectory 'WinDivert-NOTICE.md')
            Copy-Item -LiteralPath '使用说明.txt' -Destination $stagedDirectory
            & '.\tests\AuxLauncher.Tests\bin\Release\net8.0-windows\AuxLauncher.Tests.exe' --verify-release $stagedDirectory
            if ($LASTEXITCODE -ne 0) { throw '发布隐私检查失败，未替换现有运行包。' }
            $files = Get-ChildItem -LiteralPath $stagedDirectory -File | Sort-Object Name
            $hashes = foreach ($file in $files) { '{0}  {1}' -f (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $file.Name }
            $hashes | Set-Content -LiteralPath (Join-Path $stagedDirectory 'SHA256SUMS.txt') -Encoding utf8
            Compress-Archive -LiteralPath $stagedDirectory -DestinationPath $stagedZip
            # 暂存产物检查通过后，才替换脚本专用的生成目录。
            Remove-GeneratedDirectory $destination $releaseRoot
            Move-Item -LiteralPath $stagedDirectory -Destination $destination
            $zipPath = Join-Path $releaseRoot 'AuxLauncher-win-x64.zip'
            Move-Item -LiteralPath $stagedZip -Destination $zipPath -Force
            $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
            [IO.File]::WriteAllText($zipPath + '.sha256', $zipHash + '  AuxLauncher-win-x64.zip' + [Environment]::NewLine, [Text.Encoding]::ASCII)
            Write-Host "可运行程序：$(Join-Path $destination 'AuxLauncher.exe')"
        }
        finally { Remove-GeneratedDirectory $staging $releaseRoot }
    }
}
finally { Pop-Location }

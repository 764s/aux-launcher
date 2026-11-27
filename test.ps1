param([switch]$Driver, [switch]$IPv6, [switch]$NoBuild)
$ErrorActionPreference = 'Stop'
Push-Location -LiteralPath $PSScriptRoot
try {
    if (-not $NoBuild) { & (Join-Path $PSScriptRoot 'build.ps1') }
    $runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
    $artifacts = Join-Path $PSScriptRoot "test-results/runs/$runId"
    $testArgs = @('--artifacts', $artifacts)
    if ($Driver) { $testArgs += @('--integration', '--loopback') }
    if ($IPv6) {
        if (-not $Driver) { throw '-IPv6 需要同时指定 -Driver。' }
        $testArgs += '--ipv6'
    }
    & '.\tests\AuxLauncher.Tests\bin\Release\net8.0-windows\AuxLauncher.Tests.exe' @testArgs
    if ($LASTEXITCODE -ne 0) { throw "测试失败，请查看 $artifacts/report.json" }
}
finally { Pop-Location }

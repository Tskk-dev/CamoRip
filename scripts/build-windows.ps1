param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    cmake -S src/CamoRip.Decoder -B artifacts/decoder -A x64
    if ($LASTEXITCODE) { throw 'Native video decoder configuration failed.' }
    cmake --build artifacts/decoder --config $Configuration
    if ($LASTEXITCODE) { throw 'Native video decoder build failed.' }
    ctest --test-dir artifacts/decoder -C $Configuration --output-on-failure
    if ($LASTEXITCODE) { throw 'Native preview shader checks failed.' }
    cmake -S src/CamoRip.MediaSource -B artifacts/native -A x64
    if ($LASTEXITCODE) { throw 'Native camera configuration failed.' }
    cmake --build artifacts/native --config $Configuration
    if ($LASTEXITCODE) { throw 'Native camera build failed.' }
    ctest --test-dir artifacts/native -C $Configuration --output-on-failure
    if ($LASTEXITCODE) { throw 'Native camera source smoke test failed.' }
    dotnet test tests/CamoRip.Tests/CamoRip.Tests.csproj -c $Configuration -p:Platform=x64 --logger trx --logger "console;verbosity=normal" --results-directory artifacts/tests --blame-hang-timeout 5m
    if ($LASTEXITCODE) { throw 'Windows tests failed.' }
    dotnet publish src/CamoRip/CamoRip.csproj -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=false -o artifacts/windows
    if ($LASTEXITCODE) { throw 'Windows application publish failed.' }
    foreach ($resource in 'CamoRip.pri', 'App.xbf', 'Views/MainWindow.xbf') {
        if (-not (Test-Path -LiteralPath "artifacts/windows/$resource")) { throw "Published WinUI resource missing: $resource" }
    }
    Copy-Item -LiteralPath "artifacts/native/$Configuration/CamoRipMediaSource.dll" -Destination artifacts/windows
    Copy-Item -LiteralPath "artifacts/decoder/$Configuration/CamoRip.Decoder.dll" -Destination artifacts/windows
    Copy-Item -LiteralPath LICENSE,THIRD_PARTY_NOTICES.md,README.md -Destination artifacts/windows
    Copy-Item -LiteralPath src/CamoRip.MediaSource/MICROSOFT-LICENSE.txt -Destination artifacts/windows
    Copy-Item -LiteralPath docs -Destination artifacts/windows -Recurse -Force
    $startup = Start-Process -FilePath (Join-Path $projectRoot 'artifacts/windows/CamoRip.exe') -ArgumentList '--startup-check' -WindowStyle Hidden -PassThru
    if (-not $startup.WaitForExit(30000)) {
        Stop-Process -Id $startup.Id -ErrorAction SilentlyContinue
        throw 'Published application startup timed out.'
    }
    $startupLog = 'artifacts/windows/startup-check-data/Logs/latest.log'
    if ($startup.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $startupLog) -or -not (Select-String -LiteralPath $startupLog -SimpleMatch 'Startup check passed: WinUI resources loaded.' -Quiet)) {
        if (Test-Path -LiteralPath $startupLog) { Get-Content -LiteralPath $startupLog }
        throw "Published application startup failed (exit $($startup.ExitCode))."
    }
    Write-Host 'Published application startup check passed.'
} finally { Pop-Location }


param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    cmake -S src/UniversalCam.Decoder -B artifacts/decoder -A x64
    if ($LASTEXITCODE) { throw 'Native video decoder configuration failed.' }
    cmake --build artifacts/decoder --config $Configuration
    if ($LASTEXITCODE) { throw 'Native video decoder build failed.' }
    cmake -S src/UniversalCam.MediaSource -B artifacts/native -A x64
    if ($LASTEXITCODE) { throw 'Native camera configuration failed.' }
    cmake --build artifacts/native --config $Configuration
    if ($LASTEXITCODE) { throw 'Native camera build failed.' }
    ctest --test-dir artifacts/native -C $Configuration --output-on-failure
    if ($LASTEXITCODE) { throw 'Native camera source smoke test failed.' }
    dotnet test tests/UniversalCam.Tests/UniversalCam.Tests.csproj -c $Configuration -p:Platform=x64 --logger trx --results-directory artifacts/tests
    if ($LASTEXITCODE) { throw 'Windows tests failed.' }
    dotnet publish src/UniversalCam/UniversalCam.csproj -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=false -o artifacts/windows
    if ($LASTEXITCODE) { throw 'Windows application publish failed.' }
    Copy-Item -LiteralPath "artifacts/native/$Configuration/UCamMediaSource.dll" -Destination artifacts/windows
    Copy-Item -LiteralPath "artifacts/decoder/$Configuration/UniversalCam.Decoder.dll" -Destination artifacts/windows
    Copy-Item -LiteralPath LICENSE,THIRD_PARTY_NOTICES.md,README.md -Destination artifacts/windows
    Copy-Item -LiteralPath src/UniversalCam.MediaSource/MICROSOFT-LICENSE.txt -Destination artifacts/windows
    Copy-Item -LiteralPath docs -Destination artifacts/windows -Recurse -Force
} finally { Pop-Location }

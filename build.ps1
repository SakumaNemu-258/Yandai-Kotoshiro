param([string]$Destination = (Join-Path $PSScriptRoot 'artifacts'))
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
dotnet publish (Join-Path $PSScriptRoot 'Kotoshiro.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $Destination
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
$engineCache = Join-Path $PSScriptRoot 'engine-cache'
New-Item -ItemType Directory -Force -Path $engineCache | Out-Null
$archive = Join-Path $engineCache 'whisper-v1.8.2.zip'
& curl.exe -L --fail --retry 2 -o $archive 'https://github.com/ggml-org/whisper.cpp/releases/download/v1.8.2/whisper-bin-x64.zip'
if ($LASTEXITCODE -ne 0) { throw 'Engine download failed' }
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne 'B1514EBC099765E39FA37EB780B92A140A94C86BB0B3B3D98226B38825979732') { throw 'Engine checksum mismatch' }
Expand-Archive -LiteralPath $archive -DestinationPath $engineCache -Force
$engineOutput = Join-Path $Destination 'engine'
New-Item -ItemType Directory -Force -Path $engineOutput | Out-Null
Copy-Item -LiteralPath (Join-Path $engineCache 'Release\whisper-server.exe') -Destination $engineOutput
Get-ChildItem -LiteralPath (Join-Path $engineCache 'Release') -Filter '*.dll' | Where-Object Name -ne 'SDL2.dll' | Copy-Item -Destination $engineOutput
Get-ChildItem -LiteralPath $PSScriptRoot -Filter 'LICENSE-*.txt' | Copy-Item -Destination $Destination
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '使用说明.txt') -Destination $Destination
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '播放器组件来源.txt') -Destination $Destination
# Use the same checksum-pinned player binaries as the tested release.
$playerOutput = Join-Path $Destination 'player'
New-Item -ItemType Directory -Force -Path $playerOutput | Out-Null
$extractor = Join-Path $engineCache '7zr.exe'
if (-not (Test-Path -LiteralPath $extractor)) {
    & curl.exe -L --fail --retry 2 -o $extractor 'https://www.7-zip.org/a/7zr.exe'
    if ($LASTEXITCODE -ne 0) { throw '7-Zip download failed' }
}
if ((Get-FileHash -LiteralPath $extractor).Hash -ne '256FECA8E274E5DA655E2A284FABAFD9F554365EB164862089DACD4E8276D282') { throw '7-Zip checksum mismatch; check the official download before updating the pinned hash' }
$playerAssets = @(
    @{ Name='mpv'; Url='https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/20261013/mpv-x86_64-20261013-git-9d144e57b6.7z'; Hash='551C3EC2E18DB657D4F13775902145A72FB1900478719B9004EC446465FD93B9' },
    @{ Name='ffmpeg'; Url='https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/20261013/ffmpeg-x86_64-git-0eeb79fcc.7z'; Hash='3E6409F6257AF66F2DF88AA0E0FF4DC093AE42E034CB3B5D2D1B32F2D9BC2D8B' }
)
foreach ($asset in $playerAssets) {
    $assetArchive = Join-Path $engineCache ($asset.Name + '.7z')
    if (-not (Test-Path -LiteralPath $assetArchive)) { & curl.exe -L --fail --retry 2 -o $assetArchive $asset.Url; if ($LASTEXITCODE -ne 0) { throw 'Player download failed' } }
    if ((Get-FileHash -LiteralPath $assetArchive).Hash -ne $asset.Hash) { throw 'Player checksum mismatch' }
    $unpacked = Join-Path $engineCache $asset.Name
    & $extractor x $assetArchive ('-o' + $unpacked) -aoa -bso0 -bsp0
    if ($LASTEXITCODE -ne 0) { throw 'Player extraction failed' }
    Copy-Item -LiteralPath (Join-Path $unpacked ($asset.Name + '.exe')) -Destination $playerOutput
    if ($asset.Name -eq 'mpv') { Copy-Item -LiteralPath (Join-Path $unpacked 'd3dcompiler_43.dll') -Destination $playerOutput }
}
Write-Output "Build completed: $Destination"

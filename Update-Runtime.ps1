[CmdletBinding()]
param([string]$Destination = '')

$ErrorActionPreference = 'Stop'
$stage = $null
$mutex = $null
$ownsMutex = $false
$repo = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$keepStage = $false

function Assert-RuntimeClosed([string]$RuntimeRoot) {
    $busy = @(Get-Process | ForEach-Object {
        try {
            if ($_.Path -and $_.Path.StartsWith($RuntimeRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
                $_.ProcessName
            }
        } catch { }
    })
    if ($busy.Count -gt 0) {
        throw '言代Kotoshiro或其播放器正在运行。请先关闭软件，再双击“编译运行版.bat”。'
    }
}

try {
    $defaultRuntime = Join-Path $repo '运行版'
    if ([string]::IsNullOrWhiteSpace($Destination)) { $Destination = $defaultRuntime }
    $runtime = [IO.Path]::GetFullPath($Destination).TrimEnd('\')
    if ($runtime -eq [IO.Path]::GetPathRoot($runtime).TrimEnd('\') -or
        $runtime.Equals($repo, [StringComparison]::OrdinalIgnoreCase) -or
        ($runtime.StartsWith($repo + '\', [StringComparison]::OrdinalIgnoreCase) -and
            -not $runtime.Equals($defaultRuntime, [StringComparison]::OrdinalIgnoreCase))) {
        throw '运行版输出目录无效。仓库内只能使用专用的“运行版”文件夹。'
    }
    $projectPath = Join-Path $repo 'Kotoshiro.csproj'
    [xml]$project = Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8
    $versionNodes = @($project.SelectNodes('/Project/PropertyGroup/Version'))
    if ($versionNodes.Count -ne 1) {
        throw 'Kotoshiro.csproj 必须包含一个明确的 Version 版本号。'
    }
    $version = $versionNodes[0].InnerText.Trim()
    if ($version.Length -gt 128 -or
        $version -notmatch '\A[0-9]+(?:\.[0-9]+){1,3}(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?(?:\+[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?\z') {
        throw 'Kotoshiro.csproj 的 Version 无效。请使用如 0.9.10 的版本号，不要包含路径字符。'
    }
    $parent = [IO.Path]::GetDirectoryName($runtime)
    $archiveName = 'Kotoshiro-' + $version + '-Windows-x64.zip'
    $archive = Join-Path $parent $archiveName
    foreach ($existing in @($runtime, $archive)) {
        if (Test-Path -LiteralPath $existing) {
            if ((Get-Item -LiteralPath $existing -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw '输出路径不能是链接或联接目录。'
            }
        }
    }
    if ((Test-Path -LiteralPath $runtime) -and -not (Test-Path -LiteralPath $runtime -PathType Container)) {
        throw '“运行版”路径已被同名文件占用。'
    }
    if ((Test-Path -LiteralPath $archive) -and -not (Test-Path -LiteralPath $archive -PathType Leaf)) {
        throw 'ZIP 路径已被同名文件夹占用。'
    }
    Assert-RuntimeClosed $runtime

    $hash = [Security.Cryptography.SHA256]::Create()
    try { $key = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($repo.ToLowerInvariant()))).Replace('-', '') }
    finally { $hash.Dispose() }
    $mutex = [Threading.Mutex]::new($false, 'Local\KotoshiroBuild-' + $key)
    try { $ownsMutex = $mutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (-not $ownsMutex) { throw '已有一个编译窗口正在工作，请等待它完成。' }

    $sdkCandidates = @('G:\Yandai-Kotoshiro-Tools\dotnet\dotnet.exe')
    $installed = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue
    if ($installed) { $sdkCandidates += $installed.Source }
    $sdk = $null
    foreach ($candidate in ($sdkCandidates | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $candidate)) { continue }
        $versions = @(& $candidate --list-sdks 2>$null)
        if ($LASTEXITCODE -eq 0 -and @($versions | Where-Object { $_ -match '^(\d+)\.' -and [int]$matches[1] -ge 8 }).Count -gt 0) {
            $sdk = $candidate
            break
        }
    }
    if (-not $sdk) { throw '未找到 .NET 8 或更新的 SDK。请保留 G:\Yandai-Kotoshiro-Tools\dotnet，或安装 .NET 8 SDK。' }
    Set-Alias -Name dotnet -Value $sdk

    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $stage = Join-Path $parent ('.kotoshiro-build-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage | Out-Null
    $outputName = [IO.Path]::GetFileName($runtime)
    $payload = Join-Path $stage $outputName
    $stagedArchive = Join-Path $stage $archiveName
    Write-Host '正在编译言代Kotoshiro，请等待……' -ForegroundColor Cyan
    & (Join-Path $repo 'build.ps1') -Destination $payload

    foreach ($required in @('Kotoshiro.exe', 'engine\whisper-server.exe', 'player\mpv.exe', 'player\ffmpeg.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $payload $required))) { throw ('构建缺少文件：' + $required) }
    }
    $icon = Join-Path $repo 'Assets\AppIcon.ico'
    $stagedIcon = Join-Path $payload 'AppIcon.White.ico'
    Copy-Item -LiteralPath $icon -Destination $stagedIcon
    if ((Get-FileHash -LiteralPath $icon).Hash -ne (Get-FileHash -LiteralPath $stagedIcon).Hash) {
        throw '快捷方式图标校验失败。'
    }

    Write-Host '正在生成并校验 ZIP 压缩包……' -ForegroundColor Cyan
    $payloadFiles = @(Get-ChildItem -LiteralPath $payload -File -Recurse -Force)
    $expectedFiles = @{}
    foreach ($file in $payloadFiles) {
        $relative = $file.FullName.Substring($payload.Length + 1).Replace('\', '/')
        if ($relative -match '(?i)(^|/)(settings[^/]*\.json|chatgpt\.dat|bilibili\.dat|\.env[^/]*|models|player-cache)(/|$)|\.(cs|csproj|ps1|bat|pdb|pem|key|pfx|part)$|(^|/)ggml-[^/]*\.bin$') {
            throw ('构建输出包含不应打包的文件：' + $relative)
        }
        $expectedFiles[$outputName + '/' + $relative] = (Get-FileHash -LiteralPath $file.FullName).Hash
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $stagedArchive, [IO.Compression.CompressionLevel]::Optimal, $true)
    $zip = [IO.Compression.ZipFile]::OpenRead($stagedArchive)
    try {
        $fileEntries = @($zip.Entries | Where-Object { -not $_.FullName.EndsWith('/') })
        if ($fileEntries.Count -ne $expectedFiles.Count) { throw 'ZIP 文件数量不匹配。' }
        foreach ($entry in $fileEntries) {
            if (-not $expectedFiles.ContainsKey($entry.FullName)) { throw 'ZIP 内容与运行版不一致。' }
            $stream = $entry.Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $entryHash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
            finally { $sha.Dispose(); $stream.Dispose() }
            if ($entryHash -ne $expectedFiles[$entry.FullName]) { throw ('ZIP 文件校验失败：' + $entry.FullName) }
        }
    } finally { $zip.Dispose() }

    Assert-RuntimeClosed $runtime
    $previousRuntime = Join-Path $stage 'previous-runtime'
    $previousArchive = Join-Path $stage 'previous-runtime.zip'
    $installedRuntime = $false
    $installedArchive = $false
    try {
        if (Test-Path -LiteralPath $runtime) { Move-Item -LiteralPath $runtime -Destination $previousRuntime }
        if (Test-Path -LiteralPath $archive) { Move-Item -LiteralPath $archive -Destination $previousArchive }
        Move-Item -LiteralPath $payload -Destination $runtime
        $installedRuntime = $true
        Move-Item -LiteralPath $stagedArchive -Destination $archive
        $installedArchive = $true
    } catch {
        $installError = $_
        try {
            if ($installedArchive) { Move-Item -LiteralPath $archive -Destination (Join-Path $stage 'failed-runtime.zip') }
            if ($installedRuntime) { Move-Item -LiteralPath $runtime -Destination (Join-Path $stage 'failed-runtime') }
            if (Test-Path -LiteralPath $previousRuntime) { Move-Item -LiteralPath $previousRuntime -Destination $runtime }
            if (Test-Path -LiteralPath $previousArchive) { Move-Item -LiteralPath $previousArchive -Destination $archive }
        } catch {
            $keepStage = $true
            Write-Host ('原输出保留在：' + $stage) -ForegroundColor Yellow
        }
        throw $installError
    }
    Write-Host ''
    Write-Host '编译打包成功！' -ForegroundColor Green
    Write-Host ('运行版文件夹：' + $runtime)
    Write-Host ('运行版压缩包：' + $archive)
    Write-Host '解压 ZIP 后，打开其中的 Kotoshiro.exe 即可运行。'
} catch {
    Write-Host ''
    Write-Host ('编译未完成：' + $_.Exception.Message) -ForegroundColor Red
    exit 1
} finally {
    if (-not $keepStage -and $stage -and (Test-Path -LiteralPath $stage)) {
        $resolvedStage = (Resolve-Path -LiteralPath $stage).Path
        $stageParent = [IO.Path]::GetDirectoryName($resolvedStage)
        if ($stageParent.Equals($parent, [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($resolvedStage) -match '^\.kotoshiro-build-[0-9a-f]{32}$') {
            try { Remove-Item -LiteralPath $resolvedStage -Recurse -Force }
            catch { Write-Host ('临时构建文件保留在：' + $resolvedStage) -ForegroundColor Yellow }
        }
    }
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
}

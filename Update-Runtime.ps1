[CmdletBinding()]
param(
    # The installer is written here. The application payload stays temporary.
    [string]$Destination = ''
)

$ErrorActionPreference = 'Stop'
$stage = $null
$publishStage = $null
$mutex = $null
$ownsMutex = $false
$keepPublishStage = $false
$repo = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$outputParent = $null

function Assert-OrdinaryPath([string]$Path) {
    if ($Path -match '[\x00-\x1f]' -or $Path -match '\$(?:\{|%)') {
        throw '输出路径不能包含控制字符或 NSIS 宏标记。'
    }
    $cursor = $Path
    while (-not [string]::IsNullOrEmpty($cursor)) {
        if ((Test-Path -LiteralPath $cursor) -and
            ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw '构建输入和输出路径不能经过链接或联接目录。'
        }
        $ancestor = [IO.Path]::GetDirectoryName($cursor.TrimEnd('\'))
        if ([string]::IsNullOrEmpty($ancestor) -or $ancestor -eq $cursor) { break }
        $cursor = $ancestor
    }
}

function Assert-InstallerClosed([string]$InstallerFile) {
    $busy = @(Get-Process | ForEach-Object {
        try {
            if ($_.Path -and $_.Path.Equals($InstallerFile, [StringComparison]::OrdinalIgnoreCase)) { $_.ProcessName }
        } catch { }
    })
    if ($busy.Count -gt 0) { throw '正在运行的同名安装器需要先关闭，之后再双击“编译运行版.bat”。' }
}

function Remove-OwnedStage([string]$StagePath, [string]$ExpectedParent, [string]$NamePattern) {
    if ([string]::IsNullOrEmpty($StagePath) -or -not (Test-Path -LiteralPath $StagePath -PathType Container)) { return }
    $resolvedStage = (Resolve-Path -LiteralPath $StagePath).Path
    if ([IO.Path]::GetDirectoryName($resolvedStage).Equals($ExpectedParent, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedStage) -match $NamePattern -and
        -not ((Get-Item -LiteralPath $resolvedStage -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        Assert-OrdinaryPath $resolvedStage
        try { Remove-Item -LiteralPath $resolvedStage -Recurse -Force }
        catch { Write-Host ('临时构建文件保留在：' + $resolvedStage) -ForegroundColor Yellow }
    }
}

try {
    if ([string]::IsNullOrWhiteSpace($Destination)) { $Destination = $repo }
    $outputParent = [IO.Path]::GetFullPath($Destination).TrimEnd('\')
    if ($outputParent -eq [IO.Path]::GetPathRoot($outputParent).TrimEnd('\') -or
        ($outputParent.StartsWith($repo + '\', [StringComparison]::OrdinalIgnoreCase))) {
        throw '安装器输出目录无效。请选择仓库根目录或仓库之外的专用文件夹。'
    }
    Assert-OrdinaryPath $repo
    Assert-OrdinaryPath $outputParent
    if ((Test-Path -LiteralPath $outputParent) -and -not (Test-Path -LiteralPath $outputParent -PathType Container)) {
        throw '安装器输出文件夹已被同名文件占用。'
    }
    [xml]$project = Get-Content -LiteralPath (Join-Path $repo 'Kotoshiro.csproj') -Raw -Encoding UTF8
    $versionNodes = @($project.SelectNodes('/Project/PropertyGroup/Version'))
    if ($versionNodes.Count -ne 1) { throw 'Kotoshiro.csproj 必须包含一个明确的 Version 版本号。' }
    $version = $versionNodes[0].InnerText.Trim()
    if ($version.Length -gt 128 -or
        $version -notmatch '\A[0-9]+(?:\.[0-9]+){1,3}(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?(?:\+[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?\z') {
        throw 'Kotoshiro.csproj 的 Version 无效。请使用如 0.9.10 的版本号，不要包含路径字符。'
    }
    $installerName = 'Kotoshiro-' + $version + '-Windows-x64-Setup.exe'
    $installer = Join-Path $outputParent $installerName
    Assert-OrdinaryPath $installer
    if ((Test-Path -LiteralPath $installer) -and -not (Test-Path -LiteralPath $installer -PathType Leaf)) {
        throw '安装器路径已被同名文件夹占用。'
    }
    $installerScript = Join-Path $repo 'Build-Installer.ps1'
    if (-not (Test-Path -LiteralPath $installerScript -PathType Leaf)) { throw '仓库缺少 Build-Installer.ps1 安装器编译脚本。' }
    Assert-InstallerClosed $installer

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
        if ($LASTEXITCODE -eq 0 -and @($versions | Where-Object { $_ -match '^(\d+)\.' -and [int]$matches[1] -ge 8 }).Count -gt 0) { $sdk = $candidate; break }
    }
    if (-not $sdk) { throw '未找到 .NET 8 或更新的 SDK。请保留 G:\Yandai-Kotoshiro-Tools\dotnet，或安装 .NET 8 SDK。' }
    Set-Alias -Name dotnet -Value $sdk

    New-Item -ItemType Directory -Path $outputParent -Force | Out-Null
    $stage = Join-Path $repo ('.kotoshiro-build-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage | Out-Null
    $payload = Join-Path $stage 'payload'
    # Keep the final candidate on the output volume so replacement stays atomic.
    $publishStage = Join-Path $outputParent ('.kotoshiro-installer-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $publishStage | Out-Null
    $stagedInstaller = Join-Path $publishStage $installerName
    Write-Host '正在编译言代Kotoshiro安装器，请等待……' -ForegroundColor Cyan
    & (Join-Path $repo 'build.ps1') -Destination $payload
    foreach ($required in @('Kotoshiro.exe', 'engine\whisper-server.exe', 'player\mpv.exe', 'player\ffmpeg.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $payload $required) -PathType Leaf)) { throw ('构建缺少文件：' + $required) }
    }
    $icon = Join-Path $repo 'Assets\AppIcon.ico'
    $stagedIcon = Join-Path $payload 'AppIcon.White.ico'
    Copy-Item -LiteralPath $icon -Destination $stagedIcon
    if ((Get-FileHash -LiteralPath $icon).Hash -ne (Get-FileHash -LiteralPath $stagedIcon).Hash) { throw '快捷方式图标校验失败。' }
    foreach ($file in @(Get-ChildItem -LiteralPath $payload -File -Recurse -Force)) {
        $relative = $file.FullName.Substring($payload.Length + 1).Replace('\', '/')
        if ($relative -match '(?i)(^|/)(settings[^/]*\.json|chatgpt\.dat|bilibili\.dat|\.env[^/]*|models|player-cache)(/|$)|\.(cs|csproj|ps1|bat|pdb|pem|key|pfx|part)$|(^|/)ggml-[^/]*\.bin$') {
            throw ('构建输出包含不应打包的文件：' + $relative)
        }
    }
    & $installerScript -PayloadDirectory $payload -OutputFile $stagedInstaller -Version $version | Out-Null
    if (-not (Test-Path -LiteralPath $stagedInstaller -PathType Leaf)) { throw '安装器编译完成后没有生成 EXE 文件。' }
    $installerStream = [IO.File]::OpenRead($stagedInstaller)
    try {
        if ($installerStream.Length -lt 1024 -or $installerStream.ReadByte() -ne 0x4D -or $installerStream.ReadByte() -ne 0x5A) { throw '安装器 EXE 文件校验失败。' }
    } finally { $installerStream.Dispose() }
    $installerHash = (Get-FileHash -LiteralPath $stagedInstaller -Algorithm SHA256).Hash

    Assert-OrdinaryPath $installer
    Assert-InstallerClosed $installer
    $previousInstaller = Join-Path $publishStage 'previous-installer.exe'
    $hadPreviousInstaller = Test-Path -LiteralPath $installer -PathType Leaf
    $publishedInstaller = $false
    try {
        if ($hadPreviousInstaller) { [IO.File]::Replace($stagedInstaller, $installer, $previousInstaller, $true) }
        else { [IO.File]::Move($stagedInstaller, $installer) }
        $publishedInstaller = $true
        if ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ne $installerHash) { throw '安装器输出校验失败。' }
    } catch {
        $publishError = $_
        try {
            if (Test-Path -LiteralPath $previousInstaller -PathType Leaf) {
                if (Test-Path -LiteralPath $installer -PathType Leaf) { [IO.File]::Replace($previousInstaller, $installer, (Join-Path $publishStage 'failed-installer.exe'), $true) }
                else { [IO.File]::Move($previousInstaller, $installer) }
            } elseif (-not $hadPreviousInstaller -and $publishedInstaller -and (Test-Path -LiteralPath $installer -PathType Leaf)) {
                Move-Item -LiteralPath $installer -Destination (Join-Path $publishStage 'failed-installer.exe')
            }
        } catch {
            $keepPublishStage = $true
            Write-Host ('原安装器备份保留在：' + $publishStage) -ForegroundColor Yellow
        }
        throw $publishError
    }
    Write-Host ''
    Write-Host '安装器编译成功！' -ForegroundColor Green
    Write-Host ('EXE 安装器：' + $installer)
    Write-Host '打开安装器可选择安装文件夹，以及是否创建桌面快捷方式。'
} catch {
    Write-Host ''
    Write-Host ('编译未完成：' + $_.Exception.Message) -ForegroundColor Red
    exit 1
} finally {
    Remove-OwnedStage $stage $repo '^\.kotoshiro-build-[0-9a-f]{32}$'
    if (-not $keepPublishStage) { Remove-OwnedStage $publishStage $outputParent '^\.kotoshiro-installer-[0-9a-f]{32}$' }
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
}

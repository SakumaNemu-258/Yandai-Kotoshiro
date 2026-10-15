[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PayloadDirectory,
    [string]$OutputFile = '',
    [string]$Version = '',
    [string]$CompilerPath = '',
    [ValidateSet('', 'Kotoshiro-Installer-Test')][string]$TestIdentity = '',
    [ValidateSet('', 'missing-success', 'old-success', 'equal-skip', 'newer-skip', 'missing-dll-success',
        'cancelled', 'launch-failed', 'install-failed', 'reboot-required', 'already-installed',
        'already-installed-invalid', 'hresult-already-installed', 'hresult-already-installed-invalid',
        'success-but-invalid')][string]$TestVcScenario = ''
)

# Package an existing runtime. This script never rebuilds the application.
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$stage = $null
$outputParent = $null
$mutex = $null
$ownsMutex = $false
$keepStage = $false

function Assert-OrdinaryPath([string]$Path) {
    if ($Path -match '[\x00-\x1f]') { throw '路径不能包含换行或其他控制字符。' }
    if ($Path -match '\$(?:\{|%)') { throw '路径不能包含“${”或“$%”这类 NSIS 宏标记，请将该文件或文件夹改名后重试。' }
    $cursor = $Path
    while (-not [string]::IsNullOrEmpty($cursor)) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw ('安装器输入或输出路径不能经过链接或联接目录：' + $cursor)
            }
        }
        $parentPath = [IO.Path]::GetDirectoryName($cursor.TrimEnd('\'))
        if ([string]::IsNullOrEmpty($parentPath) -or $parentPath -eq $cursor) { break }
        $cursor = $parentPath
    }
}

function ConvertTo-NsisString([string]$Value) {
    if ($Value -match '[\x00-\x1f]') { throw '安装器文件名不能包含控制字符。' }
    if ($Value -match '\$(?:\{|%)') { throw '文件名不能包含“${”或“$%”这类 NSIS 宏标记，请改名后重试。' }
    # NSIS may re-expand strings in nested macros. Macro tokens are rejected;
    # ordinary dollar signs become $$ after character-code expansion.
    return $Value.Replace('$', '$${U+24}').Replace('"', '$\"')
}

function ConvertTo-NsisLiteral([string]$Value) {
    if ($Value -match '[\x00-\x1f]') { throw '安装器文件名不能包含控制字符。' }
    if ($Value -match '\$(?:\{|%)') { throw '文件名不能包含“${”或“$%”这类 NSIS 宏标记，请改名后重试。' }
    # File, Icon, OutFile and !include are compile-time paths, without runtime $$.
    return $Value.Replace('$', '${U+24}').Replace('"', '$\"')
}

function Get-VcRedistributableMetadata {
    $metadataPath = Join-Path $repo 'installer\vc-redist-x64.json'
    Assert-OrdinaryPath $metadataPath
    $metadata = Get-Content -LiteralPath $metadataPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($metadata.formatVersion -ne 1 -or $metadata.version -notmatch '\A[0-9]+(?:\.[0-9]+){3}\z' -or
        $metadata.sha256 -notmatch '\A[0-9A-Fa-f]{64}\z' -or [long]$metadata.sizeBytes -le 0) {
        throw 'VC++ x64 运行库元数据无效，请检查 installer\vc-redist-x64.json。'
    }
    foreach ($part in $metadata.version.Split('.')) {
        $number = 0
        if (-not [int]::TryParse($part, [ref]$number) -or $number -gt 65535) { throw 'VC++ 运行库版本号必须是四段 0 到 65535 的数字。' }
    }
    $downloadUri = $null
    if (-not [Uri]::TryCreate([string]$metadata.downloadUrl, [UriKind]::Absolute, [ref]$downloadUri) -or
        $downloadUri.Scheme -cne 'https' -or $downloadUri.Host -ine 'download.visualstudio.microsoft.com' -or
        -not [string]::IsNullOrEmpty($downloadUri.UserInfo) -or -not [string]::IsNullOrEmpty($downloadUri.Query) -or
        -not [string]::IsNullOrEmpty($downloadUri.Fragment) -or
        $downloadUri.AbsolutePath -notmatch '\A/download/pr/[0-9a-fA-F-]{36}/([0-9a-fA-F]{64})/VC_redist\.x64\.exe\z' -or
        $matches[1] -ine $metadata.sha256) {
        throw 'VC++ 运行库必须使用匹配 SHA256 的微软官方固定 HTTPS 下载地址。'
    }
    return $metadata
}

function Assert-VcRedistributable([string]$Path, $Metadata) {
    Assert-OrdinaryPath $Path
    Assert-Executable $Path 'VC++ x64 运行库'
    if ((Get-Item -LiteralPath $Path).Length -ne [long]$Metadata.sizeBytes -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ine $Metadata.sha256) {
        throw ('VC++ x64 运行库 SHA256 或大小校验失败，未使用此文件：' + $Path)
    }
    $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    if ($fileVersion.ProductVersion -cne $Metadata.version -or $fileVersion.FileVersion -cne $Metadata.version) {
        throw 'VC++ x64 运行库文件版本与固定版本不一致。'
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        -not $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -notmatch '(?:^|,\s*)CN=Microsoft Corporation(?:,|$)' -or
        $signature.SignerCertificate.Subject -notmatch '(?:^|,\s*)O=Microsoft Corporation(?:,|$)') {
        throw ('VC++ x64 运行库微软数字签名无效，未使用此文件：' + $Path)
    }
}

function Get-VcRedistributable {
    $metadata = Get-VcRedistributableMetadata
    $cacheRoot = [IO.Path]::GetFullPath((Join-Path $repo ('engine-cache\vc-redist\' + $metadata.version)))
    $cachedFile = Join-Path $cacheRoot 'VC_redist.x64.exe'
    $downloadStage = $null
    $downloadMutex = $null
    $ownsDownloadMutex = $false
    Assert-OrdinaryPath $cacheRoot
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $cacheKey = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($cacheRoot.ToLowerInvariant()))).Replace('-', '') }
    finally { $hash.Dispose() }
    try {
        $downloadMutex = [Threading.Mutex]::new($false, 'Local\KotoshiroVcRedist-' + $cacheKey)
        try { $ownsDownloadMutex = $downloadMutex.WaitOne(0) }
        catch [Threading.AbandonedMutexException] { $ownsDownloadMutex = $true }
        if (-not $ownsDownloadMutex) { throw '另一个编译窗口正在准备 VC++ 运行库，请等待它完成。' }
        if (Test-Path -LiteralPath $cachedFile -PathType Leaf) {
            Assert-VcRedistributable $cachedFile $metadata
            return [pscustomobject]@{ Path = $cachedFile; Version = $metadata.version; Hash = $metadata.sha256 }
        }
        if (Test-Path -LiteralPath $cachedFile) { throw ('VC++ 缓存文件路径被文件夹占用：' + $cachedFile) }
        New-Item -ItemType Directory -Path $cacheRoot -Force | Out-Null
        Assert-OrdinaryPath $cacheRoot
        $downloadStage = Join-Path $cacheRoot ('.kotoshiro-installer-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $downloadStage | Out-Null
        $downloadFile = Join-Path $downloadStage 'VC_redist.x64.exe'
        Write-Host ('首次生成含运行库的安装器，正在下载微软 VC++ x64 ' + $metadata.version + '（约 18 MB）……') -ForegroundColor Cyan
        $oldProtocol = [Net.ServicePointManager]::SecurityProtocol
        try {
            [Net.ServicePointManager]::SecurityProtocol = $oldProtocol -bor [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest -Uri $metadata.downloadUrl -OutFile $downloadFile -UseBasicParsing -TimeoutSec 120
        } catch {
            throw ('微软 VC++ x64 运行库下载失败。请联网重试；已生成的运行版与安装器会保留。详细原因：' + $_.Exception.Message)
        } finally { [Net.ServicePointManager]::SecurityProtocol = $oldProtocol }
        Assert-VcRedistributable $downloadFile $metadata
        [IO.File]::Move($downloadFile, $cachedFile)
        return [pscustomobject]@{ Path = $cachedFile; Version = $metadata.version; Hash = $metadata.sha256 }
    } finally {
        if ($downloadStage -and (Test-Path -LiteralPath $downloadStage -PathType Container)) {
            $resolvedDownloadStage = (Resolve-Path -LiteralPath $downloadStage).Path
            if ([IO.Path]::GetDirectoryName($resolvedDownloadStage).Equals($cacheRoot, [StringComparison]::OrdinalIgnoreCase) -and
                [IO.Path]::GetFileName($resolvedDownloadStage) -match '^\.kotoshiro-installer-[0-9a-f]{32}$' -and
                -not ((Get-Item -LiteralPath $resolvedDownloadStage -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                try { Remove-Item -LiteralPath $resolvedDownloadStage -Recurse -Force }
                catch { Write-Host ('临时 VC++ 运行库文件保留在：' + $resolvedDownloadStage) -ForegroundColor Yellow }
            }
        }
        if ($ownsDownloadMutex) { $downloadMutex.ReleaseMutex() }
        if ($downloadMutex) { $downloadMutex.Dispose() }
    }
}

function Get-PortableCompiler {
    $cacheRoot = [IO.Path]::GetFullPath((Join-Path $repo 'engine-cache\nsis'))
    $targetDirectory = Join-Path $cacheRoot 'nsis-3.13'
    $targetCompiler = Join-Path $targetDirectory 'makensis.exe'
    $downloadStage = $null
    $downloadMutex = $null
    $ownsDownloadMutex = $false
    Assert-OrdinaryPath $cacheRoot
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $cacheKey = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($cacheRoot.ToLowerInvariant()))).Replace('-', '') }
    finally { $hash.Dispose() }
    try {
        $downloadMutex = [Threading.Mutex]::new($false, 'Local\KotoshiroNsis-' + $cacheKey)
        try { $ownsDownloadMutex = $downloadMutex.WaitOne(0) }
        catch [Threading.AbandonedMutexException] { $ownsDownloadMutex = $true }
        if (-not $ownsDownloadMutex) { throw '另一个编译窗口正在准备 NSIS，请等待它完成。' }
        if (Test-Path -LiteralPath $targetCompiler -PathType Leaf) {
            Assert-OrdinaryPath $targetCompiler
            return $targetCompiler
        }
        if (Test-Path -LiteralPath $targetDirectory) { throw ('NSIS 缓存不完整，请将此文件夹移开后重试：' + $targetDirectory) }
        New-Item -ItemType Directory -Path $cacheRoot -Force | Out-Null
        Assert-OrdinaryPath $cacheRoot
        $downloadStage = Join-Path $cacheRoot ('.kotoshiro-installer-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $downloadStage | Out-Null
        $archive = Join-Path $downloadStage 'nsis-3.13.zip'
        Write-Host '首次生成安装器，正在下载官方 NSIS 编译工具（约 2.3 MB）……' -ForegroundColor Cyan
        $oldProtocol = [Net.ServicePointManager]::SecurityProtocol
        try {
            [Net.ServicePointManager]::SecurityProtocol = $oldProtocol -bor [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest -Uri 'https://downloads.sourceforge.net/project/nsis/NSIS%203/3.13/nsis-3.13.zip' -OutFile $archive -UseBasicParsing -TimeoutSec 120
        } catch {
            throw ('NSIS 官方工具下载失败。请联网重试，或通过 CompilerPath / KOTOSHIRO_NSIS 指定已安装的 makensis.exe。详细原因：' + $_.Exception.Message)
        } finally { [Net.ServicePointManager]::SecurityProtocol = $oldProtocol }
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne 'BA63DFFC4410EE89193E1CB5A41989991BD77C61068DA17E3156D136B7B0B3D8') {
            throw 'NSIS 官方工具校验失败，未使用下载内容。请联网重试或指定已有编译器。'
        }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $expanded = Join-Path $downloadStage 'expanded'
        $zip = [IO.Compression.ZipFile]::OpenRead($archive)
        try {
            foreach ($entry in $zip.Entries) {
                $entryPath = $entry.FullName.Replace('/', '\')
                if ([IO.Path]::IsPathRooted($entryPath) -or $entryPath -match '[\x00-\x1f:]' -or
                    @($entryPath.Split('\') | Where-Object { $_ -eq '..' }).Count -gt 0 -or
                    (($entry.ExternalAttributes -shr 16) -band 0xf000) -eq 0xa000) {
                    throw ('NSIS 压缩包包含不安全路径：' + $entry.FullName)
                }
                $entryDestination = [IO.Path]::GetFullPath((Join-Path $expanded $entryPath))
                if (-not $entryDestination.StartsWith($expanded + '\', [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'NSIS 压缩包包含超出缓存目录的路径。'
                }
            }
        } finally { $zip.Dispose() }
        [IO.Compression.ZipFile]::ExtractToDirectory($archive, $expanded)
        $extractedDirectory = Join-Path $expanded 'nsis-3.13'
        Assert-Executable (Join-Path $extractedDirectory 'makensis.exe') 'NSIS 编译器'
        Move-Item -LiteralPath $extractedDirectory -Destination $targetDirectory
        return $targetCompiler
    } finally {
        if ($downloadStage -and (Test-Path -LiteralPath $downloadStage -PathType Container)) {
            $resolvedDownloadStage = (Resolve-Path -LiteralPath $downloadStage).Path
            if ([IO.Path]::GetDirectoryName($resolvedDownloadStage).Equals($cacheRoot, [StringComparison]::OrdinalIgnoreCase) -and
                [IO.Path]::GetFileName($resolvedDownloadStage) -match '^\.kotoshiro-installer-[0-9a-f]{32}$' -and
                -not ((Get-Item -LiteralPath $resolvedDownloadStage -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                try { Remove-Item -LiteralPath $resolvedDownloadStage -Recurse -Force }
                catch { Write-Host ('临时 NSIS 文件保留在：' + $resolvedDownloadStage) -ForegroundColor Yellow }
            }
        }
        if ($ownsDownloadMutex) { $downloadMutex.ReleaseMutex() }
        if ($downloadMutex) { $downloadMutex.Dispose() }
    }
}

function Resolve-Compiler([string]$RequestedPath) {
    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $candidates += $RequestedPath
    } else {
        if (-not [string]::IsNullOrWhiteSpace($env:KOTOSHIRO_NSIS)) { $candidates += $env:KOTOSHIRO_NSIS }
        $installed = Get-Command makensis.exe -CommandType Application -ErrorAction SilentlyContinue
        if ($installed) { $candidates += $installed.Source }
        foreach ($programFilesRoot in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
            if (-not [string]::IsNullOrWhiteSpace($programFilesRoot)) { $candidates += (Join-Path $programFilesRoot 'NSIS\makensis.exe') }
        }
        $toolRoots = @(
            'G:\Yandai-Kotoshiro-Tools\nsis',
            (Join-Path $repo 'engine-cache\nsis')
        )
        foreach ($toolRoot in $toolRoots) {
            $candidates += (Join-Path $toolRoot 'makensis.exe')
            if (Test-Path -LiteralPath $toolRoot -PathType Container) {
                foreach ($directory in @(Get-ChildItem -LiteralPath $toolRoot -Directory -Force | Sort-Object Name)) {
                    if (-not ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                        $candidates += (Join-Path $directory.FullName 'makensis.exe')
                    }
                }
            }
        }
    }
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (Test-Path -LiteralPath $candidate -PathType Container) { $candidate = Join-Path $candidate 'makensis.exe' }
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $resolved = [IO.Path]::GetFullPath($candidate)
            Assert-OrdinaryPath $resolved
            return $resolved
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) { throw ('指定的 NSIS 编译器不存在：' + $RequestedPath) }
    return Get-PortableCompiler
}

function Assert-Executable([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path).Length -lt 2) {
        throw ($Label + '不存在或为空：' + $Path)
    }
    $stream = [IO.File]::OpenRead($Path)
    try {
        if ($stream.ReadByte() -ne 0x4d -or $stream.ReadByte() -ne 0x5a) { throw ($Label + '不是有效的 Windows EXE：' + $Path) }
    } finally { $stream.Dispose() }
}

try {
    if ($TestVcScenario -and $TestIdentity -ne 'Kotoshiro-Installer-Test') {
        throw 'VC++ 模拟测试仅允许使用 Kotoshiro-Installer-Test 测试身份，不能用于正式安装器。'
    }
    if ([string]::IsNullOrWhiteSpace($Version)) {
        [xml]$project = Get-Content -LiteralPath (Join-Path $repo 'Kotoshiro.csproj') -Raw -Encoding UTF8
        $versionNodes = @($project.SelectNodes('/Project/PropertyGroup/Version'))
        if ($versionNodes.Count -ne 1) { throw 'Kotoshiro.csproj 必须包含一个明确的 Version 版本号。' }
        $Version = $versionNodes[0].InnerText.Trim()
    }
    if ($Version.Length -gt 128 -or
        $Version -notmatch '\A[0-9]+(?:\.[0-9]+){1,3}(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?(?:\+[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?\z') {
        throw '版本号无效。请使用如 0.9.10 或 0.9.10-beta.1 的版本号，不要包含路径字符。'
    }
    $numericParts = @(($Version -split '[-+]', 2)[0].Split('.'))
    $productParts = @()
    foreach ($part in $numericParts) {
        $number = 0
        if (-not [int]::TryParse($part, [ref]$number) -or $number -gt 65535) {
            throw '安装器版本号的每段数字必须在 0 到 65535 之间。'
        }
        $productParts += $number.ToString([Globalization.CultureInfo]::InvariantCulture)
    }
    while ($productParts.Count -lt 4) { $productParts += '0' }
    $productVersion = $productParts -join '.'

    $payload = [IO.Path]::GetFullPath($PayloadDirectory).TrimEnd('\')
    Assert-OrdinaryPath $payload
    if (-not (Test-Path -LiteralPath $payload -PathType Container) -or
        $payload -eq [IO.Path]::GetPathRoot($payload).TrimEnd('\') -or
        $payload.Equals($repo, [StringComparison]::OrdinalIgnoreCase)) {
        throw '请指定独立的运行版文件夹，不能使用磁盘根目录或源码仓库。'
    }
    if ([string]::IsNullOrWhiteSpace($OutputFile)) {
        $OutputFile = Join-Path ([IO.Path]::GetDirectoryName($payload)) ('Kotoshiro-' + $Version + '-Windows-x64-Setup.exe')
    }
    $output = [IO.Path]::GetFullPath($OutputFile)
    Assert-OrdinaryPath $output
    if ([IO.Path]::GetExtension($output) -ine '.exe' -or
        $output.StartsWith($payload + '\', [StringComparison]::OrdinalIgnoreCase) -or
        (Test-Path -LiteralPath $output -PathType Container)) {
        throw '安装器输出必须是运行版文件夹外的独立 .exe 文件。'
    }
    $outputParent = [IO.Path]::GetDirectoryName($output)
    $installerScript = Join-Path $repo 'installer\Kotoshiro.nsi'
    Assert-OrdinaryPath $installerScript
    if (-not (Test-Path -LiteralPath $installerScript -PathType Leaf)) { throw ('缺少安装器配置：' + $installerScript) }

    foreach ($required in @('Kotoshiro.exe', 'engine\whisper-server.exe', 'player\mpv.exe', 'player\ffmpeg.exe')) {
        Assert-Executable (Join-Path $payload $required) ('运行版必需文件 ' + $required)
    }
    $icon = Join-Path $payload 'AppIcon.White.ico'
    if (-not (Test-Path -LiteralPath $icon -PathType Leaf) -or (Get-Item -LiteralPath $icon).Length -lt 6) {
        throw '运行版缺少 AppIcon.White.ico。请使用“编译运行版.bat”生成完整运行版。'
    }
    $iconStream = [IO.File]::OpenRead($icon)
    try {
        if ($iconStream.ReadByte() -ne 0 -or $iconStream.ReadByte() -ne 0 -or
            $iconStream.ReadByte() -ne 1 -or $iconStream.ReadByte() -ne 0) { throw '白色图标不是有效的 ICO 文件。' }
    } finally { $iconStream.Dispose() }

    # Walk one directory at a time so reparse points are rejected before traversal.
    $pending = New-Object 'Collections.Generic.Stack[string]'
    $pending.Push($payload)
    $payloadFiles = New-Object 'Collections.Generic.List[object]'
    $directories = @{}
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($item in @(Get-ChildItem -LiteralPath $directory -Force)) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw ('运行版不能包含链接或联接：' + $item.FullName) }
            $relative = $item.FullName.Substring($payload.Length + 1).Replace('/', '\')
            if ($relative -match '\$(?:\{|%)') { throw ('运行版文件名不能包含“${”或“$%”这类 NSIS 宏标记：' + $relative) }
            if ($relative -match '[\x00-\x1f]' -or @($relative.Split('\') | Where-Object { $_ -eq '..' -or $_ -eq '.' -or $_ -eq '' }).Count -gt 0) {
                throw ('运行版包含无效文件路径：' + $relative)
            }
            if ($relative -match '(?i)(^|\\)(settings[^\\]*\.json|chatgpt\.dat|bilibili\.dat|\.env[^\\]*|models?|player-cache|engine-cache|\.git|obj|bin|node_modules)(\\|$)|\.(cs|csproj|sln|slnx|ps1|bat|cmd|nsi|nsh|js|cjs|ts|pdb|pem|key|pfx|p12|part|gguf|safetensors|onnx|pt|pth|tflite)$|(^|\\)ggml-[^\\]*\.bin$') {
                throw ('运行版包含不应进入安装包的源码、模型或用户文件：' + $relative)
            }
            if ($item.PSIsContainer) {
                $directories[$relative] = $true
                $pending.Push($item.FullName)
            } else {
                $payloadFiles.Add([pscustomobject]@{ Path = $item.FullName; Relative = $relative; Hash = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash })
            }
        }
    }
    $files = @($payloadFiles | Sort-Object Relative)
    if ($files.Count -eq 0) { throw '运行版文件夹为空。' }
    $compiler = Resolve-Compiler $CompilerPath
    $vcRedistributable = Get-VcRedistributable

    $hash = [Security.Cryptography.SHA256]::Create()
    try { $key = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($output.ToLowerInvariant()))).Replace('-', '') }
    finally { $hash.Dispose() }
    $mutex = [Threading.Mutex]::new($false, 'Local\KotoshiroInstaller-' + $key)
    try { $ownsMutex = $mutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (-not $ownsMutex) { throw '已有一个窗口正在生成此安装器，请等待它完成。' }

    New-Item -ItemType Directory -Path $outputParent -Force | Out-Null
    Assert-OrdinaryPath $outputParent
    $stage = Join-Path $outputParent ('.kotoshiro-installer-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage | Out-Null
    $manifest = Join-Path $stage 'PayloadManifest.nsh'
    $stagedOutput = Join-Path $stage ([IO.Path]::GetFileName($output))
    $lines = New-Object 'Collections.Generic.List[string]'
    $lines.Add('; Generated from the validated runtime. Delete only these exact files.')
    $lines.Add('!macro KOTOSHIRO_INSTALL_PAYLOAD')
    $lines.Add('  ClearErrors')
    $lastDirectory = $null
    foreach ($file in $files) {
        $relativeDirectory = [IO.Path]::GetDirectoryName($file.Relative)
        if ($relativeDirectory -ne $lastDirectory) {
            $targetDirectory = '$INSTDIR'
            if ($relativeDirectory) { $targetDirectory += '\' + (ConvertTo-NsisString $relativeDirectory) }
            $lines.Add('  SetOutPath "' + $targetDirectory + '"')
            $lines.Add('  IfErrors 0 +2')
            $lines.Add('    Abort')
            $lastDirectory = $relativeDirectory
        }
        $fileName = [IO.Path]::GetFileName($file.Relative)
        $lines.Add('  File "/oname=' + (ConvertTo-NsisString $fileName) + '" "' + (ConvertTo-NsisLiteral $file.Path) + '"')
        $lines.Add('  IfErrors 0 +2')
        $lines.Add('    Abort')
    }
    $lines.Add('!macroend')
    $lines.Add('')
    $lines.Add('!macro KOTOSHIRO_CHECK_PAYLOAD_WRITABLE PREFIX')
    $lines.Add('  ClearErrors')
    foreach ($file in $files) {
        $lines.Add('  Push "$INSTDIR\' + (ConvertTo-NsisString $file.Relative) + '"')
        $lines.Add('  Call ${PREFIX}CheckPayloadFileWritable')
        $lines.Add('  IfErrors 0 +2')
        $lines.Add('    Abort')
    }
    $lines.Add('!macroend')
    $lines.Add('')
    $lines.Add('!macro KOTOSHIRO_REMOVE_PAYLOAD')
    $lines.Add('  ClearErrors')
    foreach ($file in $files) {
        $lines.Add('  IfFileExists "$INSTDIR\' + (ConvertTo-NsisString $file.Relative) + '" 0 +4')
        $lines.Add('  Delete "$INSTDIR\' + (ConvertTo-NsisString $file.Relative) + '"')
        $lines.Add('  IfErrors 0 +2')
        $lines.Add('    Abort')
    }
    foreach ($directory in @($directories.Keys | Sort-Object @{ Expression = { ($_ -split '\\').Count }; Descending = $true }, @{ Expression = { $_ }; Descending = $true })) {
        $lines.Add('  RMDir "$INSTDIR\' + (ConvertTo-NsisString $directory) + '"')
    }
    $lines.Add('  ClearErrors')
    $lines.Add('!macroend')
    [IO.File]::WriteAllLines($manifest, $lines, (New-Object Text.UTF8Encoding($true)))

    $identity = 'Kotoshiro'
    if ($TestIdentity) { $identity = $TestIdentity }
    $arguments = @(
        '/V2',
        '/INPUTCHARSET', 'UTF8',
        ('/DAPP_VERSION=' + $Version),
        ('/DAPP_PRODUCT_VERSION=' + $productVersion),
        ('/DPAYLOAD_DIR=' + (ConvertTo-NsisLiteral $payload)),
        ('/DOUTPUT_FILE=' + (ConvertTo-NsisLiteral $stagedOutput)),
        ('/DAPP_ICON=' + (ConvertTo-NsisLiteral $icon)),
        ('/DMANIFEST_INCLUDE=' + (ConvertTo-NsisLiteral $manifest)),
        ('/DAPP_IDENTITY=' + $identity),
        ('/DVC_REDIST_FILE=' + (ConvertTo-NsisLiteral $vcRedistributable.Path)),
        ('/DVC_REDIST_VERSION=' + $vcRedistributable.Version)
    )
    if ($TestVcScenario) {
        $arguments += '/DVC_RUNTIME_TEST_MODE'
        $arguments += ('/DVC_RUNTIME_TEST_SCENARIO=' + $TestVcScenario)
    }
    $arguments += $installerScript
    Write-Host '正在生成 EXE 安装器……' -ForegroundColor Cyan
    & $compiler @arguments | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw ('NSIS 编译失败，退出代码：' + $LASTEXITCODE) }
    Assert-Executable $stagedOutput '安装器'
    if ((Get-Item -LiteralPath $stagedOutput).Length -lt 1024) { throw '生成的安装器过小，可能并未正确打包。' }
    Assert-VcRedistributable $vcRedistributable.Path (Get-VcRedistributableMetadata)
    foreach ($file in $files) {
        if (-not (Test-Path -LiteralPath $file.Path -PathType Leaf) -or (Get-FileHash -LiteralPath $file.Path -Algorithm SHA256).Hash -ne $file.Hash) {
            throw ('打包期间运行版文件发生变化，请重新生成安装包：' + $file.Relative)
        }
    }
    $installerHash = (Get-FileHash -LiteralPath $stagedOutput -Algorithm SHA256).Hash
    $previousOutput = Join-Path $stage 'previous-installer.exe'
    $hadPreviousOutput = Test-Path -LiteralPath $output -PathType Leaf
    $publishedOutput = $false
    try {
        Assert-OrdinaryPath $output
        if ($hadPreviousOutput) {
            [IO.File]::Replace($stagedOutput, $output, $previousOutput, $true)
        } else {
            [IO.File]::Move($stagedOutput, $output)
        }
        $publishedOutput = $true
        if ((Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash -ne $installerHash) { throw '安装器输出校验失败。' }
    } catch {
        $verifyError = $_
        if (Test-Path -LiteralPath $previousOutput -PathType Leaf) {
            try {
                if (Test-Path -LiteralPath $output -PathType Leaf) {
                    [IO.File]::Replace($previousOutput, $output, (Join-Path $stage 'failed-installer.exe'), $true)
                } else {
                    [IO.File]::Move($previousOutput, $output)
                }
            }
            catch { $keepStage = $true; Write-Host ('原安装器保留在：' + $previousOutput) -ForegroundColor Yellow }
        } elseif (-not $hadPreviousOutput -and $publishedOutput -and (Test-Path -LiteralPath $output -PathType Leaf)) {
            try { Move-Item -LiteralPath $output -Destination (Join-Path $stage 'failed-installer.exe') }
            catch { $keepStage = $true }
        }
        throw $verifyError
    }
    Write-Host ('安装器已生成：' + $output) -ForegroundColor Green
    Write-Output $output
} finally {
    if (-not $keepStage -and $stage -and (Test-Path -LiteralPath $stage -PathType Container)) {
        $resolvedStage = (Resolve-Path -LiteralPath $stage).Path
        if ([IO.Path]::GetDirectoryName($resolvedStage).Equals($outputParent, [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($resolvedStage) -match '^\.kotoshiro-installer-[0-9a-f]{32}$' -and
            -not ((Get-Item -LiteralPath $resolvedStage -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            try { Remove-Item -LiteralPath $resolvedStage -Recurse -Force }
            catch { Write-Host ('临时安装器文件保留在：' + $resolvedStage) -ForegroundColor Yellow }
        }
    }
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
}

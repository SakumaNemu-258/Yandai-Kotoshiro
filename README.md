# 言代Kotoshiro

面向 Windows 的日语直播中文字幕工具，可识别电脑播放的声音，也可在独立播放器中观看 B 站直播，为识别和翻译预留视频延迟。

当前版本：**v0.9.10**。这是持续开发中的原型，不同配置电脑和长时间直播仍需要实测。

本项目免费开源，原创部分采用 **GPL-3.0-only**，详见 [LICENSE](LICENSE) 和 [许可与版权说明](NOTICE.md)。分发基于本项目的修改版时，需要按 GPL 提供对应源码并保留相同许可。第三方组件沿用各自的许可证。语音识别与翻译 API、订阅等服务的费用由相应服务商收取。

![白色主题界面](docs/main-light.png)

## 下载与使用

当前构建输出 Windows x64 EXE 安装器；已发布的下载文件见 [Releases](https://github.com/SakumaNemu-258/Yandai-Kotoshiro/releases)。历史版本的便携 ZIP 仍需完整解压后运行 `Kotoshiro.exe`，并保留同目录的 `engine` 和 `player` 文件夹。

安装版文件名为 `Kotoshiro-版本号-Windows-x64-Setup.exe`。运行安装向导后可自行选择安装文件夹，并勾选是否创建桌面快捷方式。安装仅针对当前 Windows 用户，提供卸载入口；卸载保留用户设置、本地识别模型及另行放入的文件。

所选文件夹已经安装言代Kotoshiro，或包含可识别的旧版便携程序时，安装器会提供“更新安装”确认。更新仅替换软件程序文件，保留用户设置、术语表、密钥、登录信息、模型和另外放入的文件；更新前需关闭软件及其播放器。

安装器内置微软官方 VC++ x64 运行库，可离线安装。安装时检查已安装的运行库版本及所需文件，缺少或版本较旧时自动补装；补装时需要允许 Windows 的权限确认。安装器不会自动重启电脑，若运行库要求重启会在完成时提示。卸载言代Kotoshiro时保留系统共享运行库。

适用于 Windows 10 / 11 的 64 位电脑。

1. 打开设置，配置文字翻译的服务商、API 地址、密钥和模型。
2. 选择本地语音识别或 API 识别。识别服务和文字翻译服务分别配置；API 识别无需下载本地识别模型。
3. 本地识别可自选模型存放文件夹，并提供显卡加速设置。
4. 在“实时翻译”中选择声音来源并开始翻译；或在“延时翻译”中输入 B 站直播间地址。

详细操作见 [使用说明](使用说明.txt)。软件设置、密钥和账号凭证留在各自电脑上，不包含在仓库和下载包中。

使用 API 识别时会向所选服务上传音频，翻译时会发送原文、上下文和术语表；网络请求与本地存储范围详见 [隐私说明](PRIVACY.md)。

## 功能

- 实时翻译、B 站延时翻译、翻译记录三个入口。
- 翻译记录页可“导出 TXT”，保存当前已保留的时间、日语原文及中文译文，使用 UTF-8 编码；无记录时按钮禁用，导出不影响继续翻译。
- 本地语音识别和兼容语音 API 的识别方式；API 模型旁可“获取模型”，只列出服务返回且已确认支持日语转写的型号，仍可手动填写。
- 多种文字翻译 API，可自填服务地址、密钥和模型。
- 可调整的翻译上下文：默认 3 段，范围 0～30 段。
- 自定义术语表，支持 TXT 导入、导出及默认示例。
- 黑白主题、透明线条标题标志、多行字幕与可滚动历史。
- B 站直播清晰度切换、刷新、全屏、可收起的评论区，以及扫码登录和发送弹幕。
- 识别、翻译和排队等环节的耗时记录。

![黑色主题界面](docs/main-dark.png)

## 从源码构建

双击仓库根目录的 `编译运行版.bat`，只在 BAT 所在文件夹生成 `Kotoshiro-版本号-Windows-x64-Setup.exe`，例如 `Kotoshiro-0.9.10-Windows-x64-Setup.exe`。文件名自动读取 `Kotoshiro.csproj` 中的 `Version`。主程序、识别引擎、播放器、白色图标和许可说明会先发布到临时目录，嵌入安装器后清理，不再另外生成运行版文件夹或 ZIP。再次执行会更新同版本安装器，之前其他版本的安装包会保留；编译或打包失败时显示原因并保留原有安装器。

安装器采用 NSIS 3.13。构建脚本会优先使用已有编译器；首次找不到时自动下载约 2.3 MB 的官方便携编译器并校验 SHA-256，缓存于 `engine-cache`，不需要安装系统工具。安装器源文件位于 `installer/Kotoshiro.nsi`，也可用 `Build-Installer.ps1 -PayloadDirectory <运行版目录> -OutputFile <安装器路径>` 单独打包已有运行版。

构建时同时携带主项目的 `LICENSE`、`NOTICE.md`、`PRIVACY.md` 和第三方许可文件。发布安装器时，应同时提供该构建对应提交的源码及第三方组件所需的对应源码、构建资料，不能用另一个版本的源码代替。

首次构建安装器还会下载约 18 MB 的官方 VC++ x64 安装包，核对固定 SHA-256、微软数字签名和版本后缓存至 `engine-cache`，后续可使用缓存打包。固定下载地址、版本和校验值见 `installer/vc-redist-x64.json`。运行库随 Setup EXE 内置。运行库来源及安装参数依据 [Microsoft 官方说明](https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files)。

需要 Windows、.NET 8 SDK 和网络连接。在仓库根目录打开 PowerShell：

```powershell
.\build.ps1
```

脚本发布自包含 Windows x64 程序，并下载经过 SHA-256 校验的 whisper.cpp、mpv 和 FFmpeg 运行组件，输出到 `artifacts`。也可通过 `-Destination` 指定输出目录。

仅构建主程序：

```powershell
dotnet publish .\Kotoshiro.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o .\artifacts
```

单独构建的主程序需要补齐运行组件，才能使用本地识别和直播播放器。

## 验证

v0.9.10 发布构建完成，85 项自动检查通过；黑白主题均已进行实际窗口截图检查。检查记录在 [docs/verification-v0.9.10.txt](docs/verification-v0.9.10.txt)。本次版本验证没有调用付费识别或翻译 API，也没有重新测试实际直播；账号授权及实际订阅翻译尚未实测。

当前开发版已验证翻译记录 TXT 导出、黑白主题界面、实际 BAT 仅生成安装器，以及旧便携版和登记安装版的更新安装；更新后的发布程序通过 93 项自检。更新和卸载保留设置、术语表、账号、模型等自定义数据，具体范围见 [当前验证记录](docs/verification-history-update-v0.9.10.txt)。内置运行库此前的检测、跳过和隔离分支检查见 [运行库安装器验证记录](docs/verification-installer-v0.9.10.txt)。

用自己的构建运行自动检查：

```powershell
$test = Start-Process -FilePath .\artifacts\Kotoshiro.exe -ArgumentList '--self-test', 'self-test-output' -WindowStyle Hidden -Wait -PassThru
Get-Content .\self-test-output\self-test.txt
```

检查输出使用独立目录。`--preview` 模式也可生成界面预览：

```powershell
dotnet .\bin\Release\net8.0-windows\win-x64\Kotoshiro.dll --preview .\preview-output light
```

## Code signing policy

本仓库正在准备接入 SignPath Foundation 的免费开源签名计划，尚未配置签名服务。当前本地编译默认生成未签名安装器，不能把提交开源许可证等同于已经取得签名。

维护者、代码审查与签名审批负责人为 [SakumaNemu-258](https://github.com/SakumaNemu-258)。拟采用的构建来源、审批与签名范围见 [Code signing policy](SIGNING.md)，申请资料与接入步骤见 [SignPath 申请准备](docs/signpath-application.md)。免费资格需要 SignPath 审核；以其实际审核结果和下载文件的数字签名为准。

## 第三方组件

NAudio、QRCoder、.NET、whisper.cpp、mpv、FFmpeg 等组件的许可证随仓库提供，具体来源与版本见 [播放器组件来源](播放器组件来源.txt)。`player-upstream` 保留对应播放器构建和源码归档；这些归档沿用其各自许可证。

标题栏与应用图标资源位于 `Assets`。当前应用图标采用白底黑线版本，标题栏透明线条仍随黑白主题切换。

`AppIcon.ico` 由白色版 SVG 导出，包含 16～256 像素的七个尺寸，16 像素使用专门的小图形。需要重新导出图标时，可在安装了 `sharp` 的 Node.js 环境执行 `node Assets/export-app-icon.cjs`；正常构建直接使用已保存的 ICO。

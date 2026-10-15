# SignPath 免费开源签名申请准备

更新日期：2026-10-15。

官方申请入口：https://signpath.org/apply

本文件提供填写资料和接入顺序，申请尚未由本次工作提交。免费签名需要 SignPath 审核，公开源码和选择 GPL 并不保证获批。官方要求见 https://signpath.org/terms 。

## 已准备的仓库内容

- 主项目采用 `GPL-3.0-only`：根目录 `LICENSE` 为 GNU 发布的 GPL 第 3 版原文；许可范围、版权及第三方例外见 `NOTICE.md`。
- 隐私说明：`PRIVACY.md`，列出音频、译文、上下文、术语表、账号、下载、缓存和本地存储范围。
- 公开签名政策：README 的 `Code signing policy` 入口和 `SIGNING.md`，当前明确标注尚未接入签名。
- GitHub Actions 手动构建入口：`.github/workflows/windows-build.yml`。仅在 GitHub 托管 Windows 运行器上构建未签名安装器和构建记录，不创建 Release，不请求签名。
- 本地编译仍沿用 `编译运行版.bat`，仅生成安装器；主项目许可证、版权和隐私说明会一同打包。

主项目的 GPL 许可证、隐私说明、签名政策和工作流已随提交 `6faff70dc42f04d5a4b3d37478feef31bde8ecd4` 公开到仓库。后续申请链接应使用实际已推送的内容；个人申请姓名和邮箱不应写入公开仓库。

## 申请表可用资料

以下名称依据申请页当前表单；表单更新时以实际页面为准。英文内容可以复制，个人身份、同意选项和项目声誉不能代填或虚构。

| 字段 | 建议填写内容 |
| --- | --- |
| Project Name | Yandai-Kotoshiro |
| Repository URL | https://github.com/SakumaNemu-258/Yandai-Kotoshiro |
| Homepage URL | https://github.com/SakumaNemu-258/Yandai-Kotoshiro |
| Download URL | https://github.com/SakumaNemu-258/Yandai-Kotoshiro/releases |
| Privacy Policy URL | https://github.com/SakumaNemu-258/Yandai-Kotoshiro/blob/HEAD/PRIVACY.md （推送后再填写；表单说明涉及用户数据时需提供） |
| Tagline | A free, open-source Windows app that adds Chinese subtitles to Japanese livestreams. |
| Category | Audio；如页面分类更改，也可选择相符的 AI Tools 类别 |
| Build System | GitHub Actions |
| Maintainer Type | Individual maintainer(s) |
| Company | 无公司主体时留空 |
| Primary Discovery Channel | AI / LLM tools |
| Exact discovery source | ChatGPT |
| Wikipedia URL | 没有项目条目则留空 |

Description：

> Yandai-Kotoshiro is a free, open-source Windows desktop application for Japanese-to-Chinese livestream subtitles. It captures system audio, supports local whisper.cpp or user-configured speech recognition APIs, and sends recognized text to a user-selected translation service. It also provides a delayed Bilibili livestream player, subtitle history, and TXT export. Original project code is licensed under GPL-3.0-only; third-party components retain their own licenses. We are preparing a GitHub Actions build and would like to apply for the SignPath Foundation open-source signing program.

Reputation 是必填项：应如实说明项目阶段，并提供公开仓库、发布记录、用户反馈、下载量或其他可核验链接。不要填写编造的用户数、星标数、下载数、合作机构或已取得的认可。可先准备这段事实性说明，再按实际公开记录补充：

> This is an early-stage project maintained at https://github.com/SakumaNemu-258/Yandai-Kotoshiro. Public release history is available at https://github.com/SakumaNemu-258/Yandai-Kotoshiro/releases. The repository documents the application's features and validation limits. We do not claim a large user base or endorsements. Please evaluate the available public project history and any additional evidence we provide.

维护者还需亲自填写 First Name、Last Name、Email。GitHub 昵称不应自动当作真实姓名填写；联系邮箱应是能够接收审核邮件的地址。

提交前由维护者阅读 Code of Conduct 和个人数据处理说明，再自行勾选相应同意项并完成验证码。营销订阅为可选项，不是免费签名资格的必要条件。

## 在线构建的使用方式

1. 把审阅后的源码、许可证、说明和工作流提交到 GitHub。工作流需先在默认分支中出现，才能在 Actions 中手动运行。
2. 打开仓库的 Actions，选择 `Build Windows installer (unsigned)`，再选择要构建的分支并运行。
3. 构建完成后，从该次运行的 Artifacts 下载产物；其中只有安装器和 `build-provenance.txt`。记录包含对应提交、工作流链接、SDK 版本和 SHA-256，不是密码学签名证明。
4. 检查运行记录与产物，并提供给审核者。当前工作流尚未在 GitHub 实际运行；本地静态检查不代表在线构建已通过。

工作流只接受手动触发，使用只读仓库权限和固定提交版本的官方 Actions，不读取签名密钥。GitHub 中的构建产物保留 14 天；它不是永久发布的下载地址。公开仓库使用 GitHub 标准托管运行器的计费规则以 GitHub 当时规定为准。

## 申请与正式发布前仍需核对

- 核对 mpv、FFmpeg 及其静态依赖的完整对应源码、补丁和构建资料，并在发布下载旁提供清晰入口。本轮已记录三份归档的 SHA-256、完整源码提交和上游 x64 构建日志证据，详见 [第三方源码核查](third-party-source-audit.md)；上游日志未记录全部缓存依赖的提交，仍不能证明完整覆盖。根目录主项目 LICENSE 不会替代第三方分发义务。
- 安装器包含微软 VC++ 运行库和播放器随附的微软系统组件，应在申请中如实说明，由 SignPath 确认其 System Libraries 例外与可接受范围；保留微软原有签名和许可。
- 使用 API 和账号的部分功能依赖第三方服务；README 中列出的真实账号授权、订阅翻译和接口验证限制应保留，不能在申请时写成已经全部实测或获得平台背书。
- 为 GitHub 和 SignPath 启用多因素认证，确认维护者、审查者及审批者与 `SIGNING.md` 一致。
- 正式安装器和所提供源码必须对应同一提交；每次发布保留版本、提交、依赖来源及构建记录。

## 获批后的接入顺序

审核通过后，按 SignPath 实际分配的组织、项目、签名策略和产物配置接入。签名 API token 存在 GitHub Actions Secret 中，不放进仓库或安装包。

当前 NSIS 安装器不能被假定为可由 SignPath 自动解包并逐个签名的容器。接入时需要在同一可信构建链路中先构建并提交本项目主程序签名，取回已签名主程序后打包安装器，再提交外层安装器签名并验证。现有一键编译在清理前一次完成发布和打包，正式接入时需调整构建阶段；当前未加入含无效项目 ID 或 token 的占位签名步骤。

第三方 whisper.cpp、mpv、FFmpeg 和微软文件不使用本项目证书重新签名。每次正式签名均按 SignPath 要求手动审批。成功接入后再更新 README / 发布页的免费签名资助说明与签名状态。

官方资料：[申请入口](https://signpath.org/apply)、[资格与政策](https://signpath.org/terms)、[GitHub 构建来源验证](https://docs.signpath.io/trusted-build-systems/github)、[产物配置](https://docs.signpath.io/artifact-configuration)。

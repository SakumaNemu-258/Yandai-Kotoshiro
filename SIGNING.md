# Code signing policy

## 接入状态

本仓库正在准备申请 SignPath Foundation 的免费开源签名计划，尚未配置签名服务。本文说明准备采用的政策，不表示已经获得免费签名资格、证书或 SignPath 的认可。当前本地编译脚本默认生成未签名文件。

## 负责人

| 角色 | 负责人 |
| --- | --- |
| 源码与构建脚本维护者（committers / authors） | [SakumaNemu-258](https://github.com/SakumaNemu-258) |
| 代码审查者（reviewers） | [SakumaNemu-258](https://github.com/SakumaNemu-258) |
| 签名审批者（approvers） | [SakumaNemu-258](https://github.com/SakumaNemu-258) |

启用服务前，维护者须按 SignPath 要求为 GitHub 和 SignPath 账号启用多因素认证。新增角色时应同步更新本表；签名凭证和 API token 不进入源码仓库。

## 构建与审批

拟签名的正式版本应从公开仓库的确定提交，在 SignPath 支持的 GitHub 托管运行器上构建，并保留对应工作流、提交和产物记录。SignPath 的免费开源计划要求可验证的构建来源；本机 BAT 生成的文件不能因此自动视为满足其签名来源要求。

每次正式签名由维护者检查版本、源码、依赖和产物后手动批准。签名服务未接入或签名验证失败时，不将文件标注为“已签名”，也不发布为已签名版本。

## 签名范围

拟签名对象为本项目从自身源码生成的 `Kotoshiro.exe` 和正式安装器。主程序应先完成签名，再嵌入安装器；安装器完成后再签名并验证，签名后的文件不得再次修改。

whisper.cpp、mpv、FFmpeg、微软运行库及其他上游程序沿用各自上游身份与许可证，不以本项目的签名身份重新签名。第三方程序随包分发的范围及例外须经 SignPath 审核确认。运行库保持微软原有签名。

正式接入后的安装器结构、文件元数据和签名范围须与 SignPath 审核通过的配置一致；是否可以签署嵌入的文件，以实际支持的产物配置为准。

## 隐私

数据处理、用户主动触发的 API 与账号网络请求、本地凭证和缓存存储说明见 [PRIVACY.md](PRIVACY.md)。软件不会因取得签名而改变这些数据用途；隐私说明须随功能变动更新。

## 申请获批后

只有实际获得免费服务并启用签名后，才在项目首页及发布页加入 SignPath 要求的资助说明：

> Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

此处引用是接入后的说明模板，不是当前签名状态声明。

参考：[SignPath Foundation conditions](https://signpath.org/terms)、[GitHub trusted build system](https://docs.signpath.io/trusted-build-systems/github)。

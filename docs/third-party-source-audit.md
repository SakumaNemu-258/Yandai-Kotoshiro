# 播放器第三方源码核查

核查日期：2026-10-15。范围：当前构建脚本固定下载的 mpv、FFmpeg，以及仓库内 `player-upstream` 的三份上游归档。本文记录已经找到的证据与缺口，不表示完整对应源码已经齐备、许可证义务已经全部满足或 SignPath 已经认可这些组件。

## 当前结论

已经保存 mpv 与 FFmpeg 的对应主项目源码快照、构建项目配方和补丁；尚未找到能够把两个发布二进制与全部静态依赖的实际提交、源码和补丁一一对应的清单。不能用“构建仓库标签固定”代替“全部依赖提交固定”，也不能用更换主项目许可证解决这个缺口。

本轮没有重新编译播放器、修改固定下载地址、下载大型依赖集合、联系上游或提交第三方签名。直接访问 GitHub 公共 REST API 曾返回限流；随后通过已连接的 GitHub 工具读到了上游运行任务、产物清单和 x64 任务日志，未把限流当作资源不存在的证据。

## 已固定的二进制与主项目源码

上游构建发布：[shinchiro/mpv-winbuild-cmake 20261013](https://github.com/shinchiro/mpv-winbuild-cmake/releases/tag/20261013)。发布页面指向 [构建运行 38325024655](https://github.com/shinchiro/mpv-winbuild-cmake/actions/runs/38325024655)。该页面上的 GitHub 提交签名不是所下载 EXE 的 Authenticode 签名。

| 对象 | 当前固定内容 | 许可与源码入口 |
| --- | --- | --- |
| mpv Windows x64 | `mpv-x86_64-20261013-git-9d144e57b6.7z`；版本记录 `v0.41.0-1151-g9d144e57b` | [mpv 提交 9d144e57b6](https://github.com/mpv-player/mpv/tree/9d144e57b6)；源码归档 [mpv-source.tar.gz](../player-upstream/mpv-source.tar.gz) |
| FFmpeg Windows x64 | `ffmpeg-x86_64-git-0eeb79fcc.7z`；版本记录 `N-127491-g0eeb79fcc` | [FFmpeg 提交 0eeb79fcc](https://github.com/FFmpeg/FFmpeg/tree/0eeb79fcc)；源码归档 [ffmpeg-source.tar.gz](../player-upstream/ffmpeg-source.tar.gz) |
| 播放器构建配方、补丁及工作流 | 构建项目标签 `20261013`，归档顶层 `mpv-winbuild-cmake-20261013/` | [构建项目](https://github.com/shinchiro/mpv-winbuild-cmake/tree/20261013)；归档 [build-sources.tar.gz](../player-upstream/build-sources.tar.gz) |

固定二进制归档的 SHA-256（与 `build.ps1` 一致）：

```text
mpv.7z     551C3EC2E18DB657D4F13775902145A72FB1900478719B9004EC446465FD93B9
ffmpeg.7z  3E6409F6257AF66F2DF88AA0E0FF4DC093AE42E034CB3B5D2D1B32F2D9BC2D8B
```

本轮重新读取的源码归档 SHA-256：

```text
player-upstream/build-sources.tar.gz   3DC2F048D4E95A265749662D4F6D31099D83B92EDA2E89A7FE1B921401E629C0
player-upstream/ffmpeg-source.tar.gz  EE2A86FDB9892E56D1E4464502DF6DDE215D1A2BDEEB65D22E6C2CDF626E9303
player-upstream/mpv-source.tar.gz     D271FF8D7E5B743110C328F1036F65ADB04582089E94F40016A6C44833814929
```

三份文件均在本地 Git 跟踪范围内。mpv 与 FFmpeg 归档顶层分别是 `mpv-9d144e57b6/`、`FFmpeg-0eeb79fcc/`。本轮从归档 PAX 全局头的 `comment` 字段读取到以下完整提交；构建项目提交还与 GitHub 产物元数据的 `head_sha` 相符：

```text
mpv      9d144e57b60ef4ca543d5b8dd1550d96aa520c59
FFmpeg   0eeb79fcc5b2761e8fdd3d0cfc5e5ba0e786645a
构建项目 1f8d2e76255abc48f0456d9be99b4cab9c6566e6
```

这能识别主项目快照，不足以识别链接进去的所有依赖版本；完整提交并非依据构建日期猜测。

## 许可事实与适用范围

- mpv 的上游 [Copyright](https://github.com/mpv-player/mpv/blob/9d144e57b6/Copyright) 说明默认整体按 GPLv2 或更新版本提供；不使用 GPL 专用文件的构建才可能采用 LGPLv2.1 或更新版本，链接库还会影响最终许可。当前构建不能仅凭 mpv 支持 LGPL 构建便标注为 LGPL。
- 已归档的 `packages/ffmpeg.cmake` 明确使用 `--enable-gpl`、`--enable-version3`、`--pkg-config-flags=--static`，并启用 x264、x265 等依赖；核查的配方未出现 `--enable-nonfree`。当前来源说明按 GPL/version3 记录，与 [FFmpeg 官方许可说明](https://ffmpeg.org/legal.html) 相符；具体二进制许可仍须结合真实配置和全部依赖确认。
- Kotoshiro 通过独立进程和 mpv 的 JSON IPC 控制播放器，没有把 libmpv 或 FFmpeg API 直接链接进主程序。无论主项目如何授权，分发这些二进制仍需分别保留其许可、版权及所需对应源码。
- 原创部分的 `GPL-3.0-only` 不会把第三方原有的“或更新版本”授权改成 only，也不会把微软文件改成 GPL。

许可文件入口：[mpv 说明](../LICENSE-mpv.txt)、[mpv GPL](../LICENSE-mpv-GPL.txt)、[mpv LGPL](../LICENSE-mpv-LGPL.txt)、[FFmpeg GPLv3 文本](../LICENSE-ffmpeg.txt)。各依赖应以实际编译提交中的许可文件为准；本表没有把未知提交的许可推定成已经核验。

## 静态依赖来源索引与版本缺口

下表从已归档的 `packages/*.cmake` 提取来源。`未锁定` 表示对应配方没有不可变的 Git 提交；`main`、`default`、`SDL2`、`v2.1-agentzh` 等分支名也不能识别 20261013 二进制实际使用的提交。下列是核查入口，不是完整的软件物料清单，也不证明每个配方里的可选项都进入了当前 EXE。

每一行的构建参数、补丁和直接依赖可在 [20261013 packages 目录](https://github.com/shinchiro/mpv-winbuild-cmake/tree/20261013/packages) 及本地 `build-sources.tar.gz` 中的同名 `.cmake` 文件核对。

| 配方 / 源码上游 | 配方中的版本状态 |
| --- | --- |
| [x264](https://code.videolan.org/videolan/x264)、[x265](https://github.com/Multicorewareinc/x265) | 未锁定 |
| [rubberband](https://github.com/breakfastquay/rubberband) | `default` 分支 |
| [libass](https://github.com/libass/libass)、[FreeType](https://github.com/freetype/freetype)、[FriBidi](https://github.com/fribidi/fribidi) | 未锁定 |
| [HarfBuzz](https://github.com/harfbuzz/harfbuzz)、[Fontconfig](https://gitlab.freedesktop.org/fontconfig/fontconfig) | `main` 分支 |
| [libplacebo](https://github.com/haasn/libplacebo) | 未锁定 |
| [AOM](https://aomedia.googlesource.com/aom) | `main` 分支 |
| [dav1d](https://code.videolan.org/videolan/dav1d)、[SVT-AV1](https://gitlab.com/AOMediaCodec/SVT-AV1) | 未锁定 |
| [libvpx](https://chromium.googlesource.com/webm/libvpx)、[libwebp](https://chromium.googlesource.com/webm/libwebp)、[Opus](https://github.com/xiph/opus) | `main` 分支 |
| [Vorbis](https://github.com/xiph/vorbis)、[LAME 构建配方使用的镜像](https://gitlab.com/shinchiro/lame) | 未锁定 |
| [libopenmpt](https://lib.openmpt.org/files/libopenmpt/src/libopenmpt-0.7.12+release.autotools.tar.gz) | 固定 `0.7.12+release.autotools`，SHA-256 `79AB3CE3672601E525B5CC944F026C80C03032F37D39CAA84C8CA3FDD75E0C98`；尚未保存该依赖归档 |
| [libjxl](https://github.com/libjxl/libjxl) | `main` 分支；还需核对其依赖 / 子模块 |
| [libarchive](https://github.com/libarchive/libarchive)、[MuJS](https://codeberg.org/ccxvii/mujs) | 未锁定 |
| [OpenResty LuaJIT](https://github.com/openresty/luajit2) | `v2.1-agentzh` 分支 |
| [curl](https://github.com/curl/curl)、[OpenSSL](https://github.com/openssl/openssl)、[libssh](https://gitlab.com/libssh/libssh-mirror)、[SRT](https://github.com/Haivision/srt) | 未锁定 |
| [Vulkan Loader](https://github.com/KhronosGroup/Vulkan-Loader)、[shaderc](https://github.com/google/shaderc)、[SPIRV-Cross](https://github.com/KhronosGroup/SPIRV-Cross)、[SPIRV-Tools](https://github.com/KhronosGroup/SPIRV-Tools) | `main` 分支；还需核对子模块及传递依赖 |
| [libdvdread](https://code.videolan.org/videolan/libdvdread)、[libdvdnav](https://code.videolan.org/videolan/libdvdnav)、[libbluray](https://code.videolan.org/videolan/libbluray) | 未锁定 |
| [SDL](https://github.com/libsdl-org/SDL) | `SDL2` 分支 |
| [OpenAL Soft](https://github.com/kcat/openal-soft)、[libaribcaption](https://github.com/xqq/libaribcaption)、[libsixel](https://github.com/saitoha/libsixel) | 未锁定 |
| [zvbi](https://github.com/zapping-vbi/zvbi) | `main` 分支 |

仍须沿 `DEPENDS` 递归核查其他实际使用的包，例如 ogg、speex、bzip2、zlib、zstd、libjpeg、libpng、lcms2、libiconv、expat、libudfread、libsoxr、libzimg / graphengine、libmysofa、libbs2b、libxml2、libvpl、libva、libunibreak、VapourSynth、glslang、Vulkan/SPIR-V 头文件、highway、brotli，以及 curl 的具体 TLS / HTTP 依赖。需先确认哪些进入二进制，不能把配方全集直接当作实际分发清单。

归档工作流使用滚动更新的 Git 源和缓存，包含 `ninja ... update`；日志上传步骤的保留时间为 1 天。当前归档没有构建时每个依赖的 `git rev-parse HEAD` 结果，也没有缓存中的完整依赖源码。构建配方能说明“从哪里下载”，不能证明“该 EXE 当时用了哪个提交”。

### 上游在线构建记录的补充核查

通过 GitHub 工具读取了 [x64 构建任务 115061602201](https://github.com/shinchiro/mpv-winbuild-cmake/actions/runs/38325024655/job/115061602201) 的完整任务日志（1242 行），确认本次普通 x64 构建使用 clang、先恢复源码 / 工具链缓存，再执行更新和构建；mpv 与 FFmpeg 的归档名称与本项目固定资产相符。日志记录了部分本次发生更新的依赖短提交，例如 x265 `87f5f70`、libpng `33d0150`、HarfBuzz `23293846`、libwebp `528e7a4`，但未打印全部未变化缓存依赖的实际 HEAD，也没有完整子模块和依赖源码清单，不能用这些片段填满完整映射。

该运行当前返回的产物清单共 8 项，名称为四种架构各自的 mpv 主程序和 debug 产物；没有源码集合产物。任务日志记录曾上传 `mpv-x86_64-logs`，产物 ID 为 `11781908760`，约 220 KB；本轮尝试下载该精确产物返回 GitHub 404，未取得其内部配置日志。不能因此断言上游从未保留其他资料，但当前这条公开工作流路径尚未补齐上述缺口。debug 文件和可用的二进制产物也不能代替对应源码。

## 补齐方式与完成条件

保留当前二进制时，应取得对应上游构建的依赖提交清单、子模块提交、所有实际使用的源码、补丁、生成代码所需资料及构建参数。可以先检查上游该次运行已有的公开日志和产物；缺少的部分需向构建发布者取得。此文不表示已经发出请求或得到答复。使用当前 HEAD 重新下载全部仓库不能补成旧二进制的对应源码。

若已无法取得旧二进制的完整对应资料，可靠的替代路径是从新的、完整锁定的源码集合自行构建所需 mpv / FFmpeg：在更新任何发布二进制之前，保存每个实际依赖的完整提交、子模块、源码归档、SHA-256、许可、补丁与构建参数；核对生成程序的版本 / 配置和所需功能，再更新固定二进制及其来源说明。这会改变已测试组件，需独立测试，不能作为本次文档整理时的静默替换。

正式发布下载旁应明确提供对应源码入口，保留与安装器同一版本的原创源码及所分发 GPL 组件所需的完整对应源码和构建资料。原始源码可以是单独下载，不要求所有用户安装源码。不要仅写“请自行去上游找最新代码”。[GPLv3 第 6 节](https://www.gnu.org/licenses/gpl-3.0.html#section6)和 [GNU 关于源码与二进制分开托管的说明](https://www.gnu.org/licenses/gpl-faq.en.html#SourceAndBinaryOnDifferentSites)说明了相应分发要求。

源码核查的完成证据应包括：

1. 二进制 SHA-256 与主项目、全部实际依赖的不可变源码标识的映射。
2. 可取得的对应源码、子模块、补丁、构建和安装所需脚本，以及各自版权 / 许可文件。
3. 下载页面旁清楚且持续可用的源码说明；清单与每次发布同步。
4. 对尚不能归为 OSS 的微软 VC++ 运行库、`d3dcompiler_43.dll` 等单独列明来源与许可，由 SignPath 确认其 System Libraries 例外范围。保留微软原有签名，不以本项目身份重签。

[SignPath 条件](https://signpath.org/terms.html)允许上游 OSS 二进制随包，但要求遵守其来源、许可和签名范围规则。本文补充了申请前的证据边界；不能把此核查文件当作签名批准书。

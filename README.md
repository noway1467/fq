# 飞Q

[![GitHub Release](https://img.shields.io/github/v/release/noway1467/fq?label=release&color=blue)](https://github.com/noway1467/fq/releases/latest)

轻量级 Windows 与 Android 局域网聊天及文件传输工具，支持电脑与手机自由互通，无需注册账号或服务器。

| Windows | Android |
| --- | --- |
| [![浅色：电脑端会话列表、聊天和文件卡片](docs/preview.png)](docs/preview.png) | [<img src="docs/preview-android.png" width="230" alt="浅色：手机端接收同一份路线文件">](docs/preview-android.png) |
| [![炭灰深色：电脑端聊天](docs/preview-dark.png)](docs/preview-dark.png) | [<img src="docs/preview-android-dark.png" width="230" alt="炭灰深色：手机端按来源查看传输任务">](docs/preview-android-dark.png) |

## 主要功能

- **即时聊天**：自动发现同局域网设备（支持 `IPv4:端口` 手动添加），支持文字、彩色表情、图片与超长文本自动转 TXT 发送。
- **消息与记录**：支持送达确认、失败重发、转发、多选删消息或批量删本机文件、置顶备注、分页跳页与全文/文件名搜索定位。
- **传文件与文件夹**：双端任意互传文件与文件夹（保留目录结构，单批最多 1000 个），支持断点续传（7 天）、SHA-256 校验与同名自动重命名。
- **传输任务**：按来源头像横排、状态和关键词筛选收发记录，查看速度与剩余时间，支持暂停/续传、拒收、取消、打开/删本机文件与定位原消息。
- **临时浏览器传输**：生成 15 分钟局域网链接和二维码，对方无需安装即可下载；开启接收后访客单次最多上传 100 个文件、合计 10 GiB（HTTP 明文，仅限可信局域网）。
- **系统集成与清理**：支持 Windows 拖拽/右键“发送到飞Q”与 Android 系统分享；安卓接收仅落盘单份正式文件，双端「设置 → 文件」可清理已登记的接收文件。
- **共享文件夹**：双端均可共享最多 20 个本地目录，凭 6 位连接码只读浏览、播放音视频与断点下载。
- **远程控制**：凭 6 位连接码和本机确认控制对方屏幕（支持控 Windows 桌面与 Android 手机），支持断线恢复、缩放、多屏切换与信任设备。
- **安全与网络**：新版默认 TLS 1.2 并核对配对码，适配热点、USB 共享与单向 VPN；默认兼容旧版 IPMSG 明文，可在设置关闭。
- **个性外观**：双端统一浅色/深色（可跟随系统）、6 套配色、8 组预设、聊天背景、字号与 24 枚内置/本地头像。

## 使用

1. 从 [GitHub Releases](https://github.com/noway1467/fq/releases) 下载对应包（Windows 需 .NET Framework 4.8，Android 需 8.0 及以上），更新直接覆盖安装即可保留配置与记录。
2. 接入同一 Wi-Fi、以太网、热点或 USB 共享，Windows 运行 EXE、Android 点“连接”；首次连接新版设备在两端核对配对码后信任。
3. 安卓开启 VPN 提示「广播受限」时，点右上角「＋」输入对方局域网 IPv4；顶栏可直达「传输任务」，会话列表下拉可刷新联系人。

## 开发

Windows 使用 .NET Framework 4.8，Android 使用 JDK 17 与 Android SDK 36；PowerShell 7 执行 `./build.ps1 -Test`、`./build-android.ps1 -Test`，安全与互通回归见 `./tests/secure-upgrade.ps1`、`./tests/android-interop.ps1 -Secure -LargeFiles`。

本项目源码以 [MIT 许可证](LICENSE) 发布。表情与头像素材来自 [Twemoji](https://github.com/jdecked/twemoji)（[CC BY 4.0](docs/Twemoji-LICENSE-GRAPHICS.txt)）；二维码使用内置 [Project Nayuki QR Code generator](https://www.nayuki.io/page/qr-code-generator-library)（MIT）。

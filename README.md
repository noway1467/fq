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
- **传文件与文件夹**：双端任意互传文件与文件夹，保留目录结构；单批默认 5000 个、待处理默认 10000 个，可在「设置 → 文件」调整至 100–50000，完成文件不占待处理额度。支持断点续传（7 天）、SHA-256 校验与同名自动重命名。
- **传输任务**：按来源头像横排、状态和关键词筛选收发记录，查看速度与剩余时间，支持暂停/续传、拒收、取消、打开/删本机文件与定位原消息。
- **临时浏览器传输**：生成局域网链接和二维码，默认有效 1 小时，可设为 1–1440 分钟并保存；可选择文件或文件夹分享，浏览器按文件下载；浏览器上传文件夹保留目录路径（不发送空目录），单次最多 1000 个文件、合计 100 GiB。Android 选定保存文件夹后按目录落盘，未选时保存到应用接收区（HTTP 明文，仅限可信局域网）。
- **系统集成与清理**：支持 Windows 拖拽/右键“发送到飞Q”与 Android 系统分享；安卓接收仅落盘单份正式文件，双端「设置 → 文件」可清理已登记的接收文件。
- **共享文件夹**：双端均可共享最多 20 个本地目录，凭 6 位连接码只读浏览；图片、音视频、PDF 与文本可在浏览器新页直接打开，支持断点下载；增减目录保持连接并自动刷新。
- **远程控制**：凭 6 位连接码和本机确认控制 Windows 桌面或 Android 手机，支持增量画面、缩放、多屏切换、断线恢复与信任设备；Windows 支持组合键，双端支持手动推送剪贴板文字，读取电脑剪贴板需另行开启权限。
- **安全与网络**：聊天和文件传输仅使用 TLS 1.2，首次连接核对配对码。旧设备仍可被发现，但必须升级后才能通信。支持热点、USB 共享与单向 VPN。
- **个性外观**：双端统一浅色/深色（可跟随系统）、6 套配色、8 组预设、聊天背景、字号与 24 枚内置/本地头像。

## 使用

1. 从 [GitHub Releases](https://github.com/noway1467/fq/releases) 下载对应包（Windows 需 .NET Framework 4.8，Android 需 8.0 及以上），更新直接覆盖安装即可保留配置与记录。
2. 接入同一 Wi-Fi、以太网、热点或 USB 共享，Windows 运行 EXE、Android 点“连接”；首次连接新版设备在两端核对配对码后信任。
3. 安卓开启 VPN 提示「广播受限」时，点右上角「＋」输入对方局域网 IPv4；顶栏可直达「传输任务」，会话列表下拉可刷新联系人。

安卓「设置 → 通用」默认开启「息屏保持连接」；若手机仍限制后台运行，可点「电池与后台权限」，将飞Q的电池策略设为不限制，并允许系统提供的后台运行权限。

端口默认为 `2425`，一般无需修改；两端均可在「设置 → 通用 → 连接」更改，Windows 重启程序后生效，Android 断开再连接后生效。

## 开发

Windows 使用 .NET Framework 4.8，Android 使用 JDK 17 与 Android SDK 36；PowerShell 7 执行 `./build.ps1 -Test`、`./build-android.ps1 -Test`，安全与互通回归见 `./tests/secure-upgrade.ps1`、`./tests/android-interop.ps1 -Secure -LargeFiles`。

本机验证：`pwsh -NoProfile -File tools/verify.ps1 -Mode Fast` 检查协议和 TLS 互通；`Full` 增加隔离构建、单测、lint、安全契约及定向回归，`Release` 另查候选包摘要与访客浏览器。三种模式均不发布；报告写入 `build/verify`，候选包仅在 `build/tmp`。退出码 0/1/2 分别表示通过、失败、环境阻塞。

本项目源码以 [MIT 许可证](LICENSE) 发布。表情与头像素材来自 [Twemoji](https://github.com/jdecked/twemoji)（[CC BY 4.0](docs/Twemoji-LICENSE-GRAPHICS.txt)）；二维码使用内置 [Project Nayuki QR Code generator](https://www.nayuki.io/page/qr-code-generator-library)（MIT）。

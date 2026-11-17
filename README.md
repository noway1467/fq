# 飞Q

轻量级 Windows 与 Android 局域网聊天及文件传输工具，支持电脑与手机、手机与手机、电脑与电脑自由互通，无需注册账号或服务器。

![飞Q 双端界面预览（经典浅色）](docs/preview.png)

![飞Q 双端界面预览（暮紫深色）](docs/preview-dark.png)

## 主要功能

- **即时聊天**：自动发现同局域网设备（也支持通过 `IPv4:端口` 手动添加）；支持文字、彩色表情与图片（Windows 粘贴剪贴板图片、Android 输入法图片），超长文本自动转 TXT 附件发送。
- **消息与记录**：支持送达确认、失败重发、转发、全文查看与复制、多选删除、草稿保留、置顶与备注，以及聊天记录搜索定位。
- **传文件与文件夹**：支持 **Windows ↔ Android、Android ↔ Android、Windows ↔ Windows** 互传文件与文件夹（保留目录结构，单批最多 1000 个）；支持断点续传（邀请保留 7 天）、SHA-256 校验与同名自动重命名。
- **系统分享与附件**：支持 Windows 拖拽收发和右键“发送到飞Q”、Android 系统分享；支持自动/手动接收，删消息保留文件，已接收附件可单独删除本机文件。
- **共享文件夹**：Windows（最多 20 个目录）与 Android 均可共享本地目录，凭 6 位连接码只读浏览、播放音视频与断点下载。
- **远程控制**：凭 6 位连接码与本机确认操作对方屏幕（支持 Android 控 Windows 桌面、Windows / Android 控 Android 手机），支持缩放、多屏切换（Windows）与“始终允许此设备”。
- **安全与网络**：新版默认 TLS 1.2 并核对配对码，适配热点、USB 共享及单向 VPN 局域网；默认兼容旧版 IPMSG 明文，可在设置中关闭。
- **个性外观**：双端统一浅色/深色（可跟随系统）、6 套主题配色、8 组快捷预设、聊天背景与字号调节，支持 24 枚内置或本地图片头像。

## 使用

1. 从 [GitHub Releases](https://github.com/noway1467/fq/releases) 下载对应版本（Windows 需 .NET Framework 4.8，Android 需 8.0 及以上）。
2. Windows 运行 EXE，Android 安装 APK 后点击“连接”；更新时直接覆盖安装即可，原有设置与聊天记录会保留。
3. 将设备接入同一 Wi-Fi、以太网、手机热点或 USB 共享网络，选择对方即可聊天或互传文件；首次与新版设备连接时在两端核对配对码一致后信任，共享文件夹与远程控制可从菜单打开。

## 开发

Windows 使用 .NET Framework 4.8，Android 使用 JDK 17、Android SDK 36。用 PowerShell 7 执行 `./build.ps1 -Test`、`./build-android.ps1 -Test`；加密与恢复回归执行 `./tests/secure-upgrade.ps1`，双端加密互通执行 `./tests/android-interop.ps1 -Secure -LargeFiles`。

表情与头像素材来自 [Twemoji](https://github.com/jdecked/twemoji)，按 [CC BY 4.0](docs/Twemoji-LICENSE-GRAPHICS.txt) 使用。

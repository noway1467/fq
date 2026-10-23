# 飞Q

[![release](https://img.shields.io/github/v/release/noway1467/fq?display_name=tag&label=release&style=flat-square)](https://github.com/noway1467/fq/releases/latest)
[![GitHub Repository](https://img.shields.io/badge/GitHub-Repository-181717?style=flat-square&logo=github&logoColor=white)](https://github.com/noway1467/fq)

轻量的 Windows / Android 局域网聊天与文件传输工具。功能方向参考飞秋，界面布局参考 Telegram。

当前版本：**Windows / Android 0.3.27**。

下载独立安装/运行包：[GitHub Releases](https://github.com/noway1467/fq/releases)。选择 Windows EXE 或 Android APK，无需下载源码。

## 功能

- **局域网直连**：自动发现同网设备，也可手动添加 IPv4 地址；支持中文、表情、历史消息和草稿。
- **文件互传**：一次最多选择 20 个文件，逐文件显示进度，可取消和重试；默认自动接收，也可改为手动确认。
- **会话管理**：支持备注、置顶、移除与恢复；在线会话优先显示，同名设备会带上本机编号，方便区分。
- **常用设置**：支持夜间模式、字号、聊天背景、文件保存目录和自动接收开关。
- **长消息查看**：消息过长时显示摘要，可打开全文并复制；特别长的内容会自动作为 TXT 附件发送。
- **Windows 图片与文件操作**：Ctrl+V、Shift+Insert 或右键粘贴图片加入待发附件，点击发送后传输 PNG；已发文件或已保存附件可拖到外部复制，不会移动原文件。
- **双端历史搜索**：会话菜单进入聊天记录，消息及文件名关键词高亮，显示结果数量；Windows 使用独立标题和强调色边框区分记录窗口。

## 使用

1. 让两台设备连接同一个可信局域网。Windows 需要 **.NET Framework 4.8**，Android 需要 **8.0 或更高版本**。
2. 从 [GitHub Releases](https://github.com/noway1467/fq/releases) 下载并打开对应版本。Android 首次使用点击「连接」。
3. 通常会自动发现对方。没有出现时，用「+」添加对方的 IPv4 地址；默认端口为 **UDP/TCP 2425**。

**连不上时：**检查两台设备是否在同一网段、防火墙是否放行 UDP/TCP 2425，以及路由器是否开启了客户端隔离。若正在使用 VPN，请允许局域网访问或将飞Q排除。

Windows 粘贴图片先排队，不受“拖入文件自动发送”开关影响。未发送副本在移除附件或销毁会话时清理；已发副本保留在资料目录 `pasted-images`，用于历史打开、重发和外拖，总量限制 128 MiB，超过时请保存为文件发送。接收中的附件、已移动或删除的文件不能外拖。历史搜索范围：Windows 最近 500 条、Android 最近 200 条可读取记录，不是全库搜索。Android 保持原有文件选择、输入法图片和系统分享权限机制，本轮未新增跨应用拖出。

## 构建与测试

Windows 使用系统 C# 编译器；Android 使用 JDK 17+、SDK platform 36 / build-tools 35.0.0、Gradle 8.14.3。Android 构建依赖只用于开发，不打入 APK。

```powershell
# Windows 编译与回归
.\build.ps1 -Test
# Android 编译、lint 与回归；本机路径仅写入忽略提交的配置
.\build-android.ps1 -SdkRoot '你的 Android SDK 目录' -JavaHome '你的 JDK 目录' -Test
# 双端真实 UDP/TCP 回环、文件校验与大文件续传
.\tests\android-interop.ps1 -LargeFiles
# 可选：用本机已有 JDK 11 验证与 Android 相同的阻塞接收锁行为
.\tests\android-idle-latency.ps1 -JavaHome '你的 JDK 11 目录'
# Windows 便携包及包内程序验证
.\package.ps1
.\tests\package-smoke.ps1
```

构建输出位于 `dist/`，便携 ZIP、测试日志和渲染预览位于 `build/`；临时文件与新依赖缓存也留在项目内。只维护根目录这份 README，便携包直接包含它，不再维护 `dist/README.md` 旧副本。已有 Gradle 可用 `-GradleHome` 复用，缓存齐全后可加 `-Offline`。

仓库只包含源码、测试、构建入口、这份说明和必要素材许可，不提交代理指令、个人记录、本机配置、密钥或安装包。新环境生成的 Android 调试签名与其他机器不一定相同；覆盖安装必须使用兼容签名，不要为升级而随意卸载清空数据。

## 素材许可

Windows 的 144 枚彩色聊天表情及双端的 24 枚默认头像来自 [Twemoji v16.0.1](https://github.com/jdecked/twemoji/tree/v16.0.1)，版权归 Twitter, Inc. 及贡献者，按 [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) 使用。原图仅合并为本地图集；完整许可见 [Twemoji-LICENSE-GRAPHICS.txt](docs/Twemoji-LICENSE-GRAPHICS.txt)，并嵌入 EXE / APK。运行时不联网下载表情。双端头像共用 assets/avatars 的目录、图集和许可；tools/build-avatar-atlas.ps1 可从固定版本重新生成素材。

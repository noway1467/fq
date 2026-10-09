# 飞Q

轻量的 Windows / Android 局域网聊天与文件传输工具。功能方向参考飞秋，界面布局参考 Telegram；独立实现，非官方产品。

当前版本：**Windows 0.3.19 / Android 0.2.20 预览版**。

下载独立安装/运行包：[GitHub Releases](https://github.com/noway1467/fq/releases)。选择 Windows EXE 或 Android APK，无需下载源码。

## 功能

- 自动发现和手动 IPv4 连接，中文、表情、消息确认、历史与草稿。
- 多文件收发，每批最多 20 个文件；**发送端和接收端均在每个附件下显示独立进度条、百分比与状态**。
- 双端气泡按内容收紧高度，减少文件信息、进度条和时间之间的空白；进度更新不抢旧消息的阅读位置。
- 默认自动接收，可关闭；自定义目录、同名避让、取消、失败重试和有效邀请内的断点续传。
- 文件打开、分享/定位与逐条转发，图片/视频预览取决于系统解码能力，不自动播放附件。
- 会话备注、置顶、移除/恢复、夜间模式、字号和聊天背景。
- 同名会话自动显示固定本机编号（如 phone · #1、phone · #2），搜索、重启、上下线不重排编号；列表、聊天标题、通知及转发入口一致，历史不会合并。编号仅供本机区分，不代表在线状态或升级前会话的真实先后；也可设置独有备注。
- Android 优先将发现、消息和下载套接字绑定到非 VPN 的 Wi-Fi/以太网，避免跟随 TUN 默认路由；不修改系统代理设置。
- Android 发送立即显示处理中，发现刷新不阻塞文字发送；文件先安全暂存并显示本地准备进度，再发送邀请。
- Android 0.2.19 修复空闲发送被阻塞接收锁卡住的问题，避免等到下一次广播才发出消息。

## 使用

1. 双端连接同一个可互访的可信局域网。Windows 需要 **.NET Framework 4.8**；Android 需要 **8.0 或更高版本**。
2. 下载后，Windows 直接运行 EXE；Android 安装 APK，首次点击「连接」。本地构建包也位于 `dist/`；二进制包作为 Release 附件提供，不纳入 Git 源码历史。
3. 未自动发现时，用「+」添加对方 IPv4 地址。默认端口为 **UDP/TCP 2425**，检查防火墙及路由器的客户端隔离。
4. 用回形针选文件。接收中的附件可点击其进度提示取消，失败可重试。Windows 也保留文件抽屉；Android 保存目录通过系统文件选择器授权。
5. Windows 关闭窗口默认收起到托盘，从菜单「退出」结束。更新前正常退出旧程序，不必删除聊天数据。

**代理提示：**局域网流量仍可能受 TUN/VPN 路由或系统策略影响。VPN 若禁止绕过、开启「阻止不经过 VPN 的连接」，请在其设置中允许局域网、排除飞Q或调整该限制；应用不能保证突破系统强制策略。Android 热点和不同厂商 VPN 组合仍需设备实测。

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

构建输出位于 `dist/`，便携 ZIP、测试日志和渲染预览位于 `build/`；临时文件与新依赖缓存也留在项目内。已有 Gradle 可用 `-GradleHome` 复用，缓存齐全后可加 `-Offline`。

仓库只包含源码、测试、构建入口、这份说明和必要素材许可，不提交代理指令、个人记录、本机配置、密钥或安装包。新环境生成的 Android 调试签名与其他机器不一定相同；覆盖安装必须使用兼容签名，不要为升级而随意卸载清空数据。

## 安全与边界

- **IPMSG 基础协议明文且无可靠身份认证**，只用于可信局域网，不映射端口到公网。默认自动接收不等于文件可信。
- ACK 只表示客户端确认，不代表已读；发送进度 100% 表示数据已写入连接，不等于对方已经保存。接收端以自己的「已保存」为准。
- 网络失败保留断点，主动取消/拒绝清理未完成部分；已保存文件不回滚、不覆盖。邀请空闲 30 分钟过期；发送端退出、文件改变或缓存清理后通常需要重新发送。
- 进度不是持久任务中心：Android 发送进度仅本次服务有效；Windows 退出后不重建未完成接收任务。未实现群聊、文件夹传输、端到端加密和音视频通话。
- JVM 真实回环和 Robolectric 控件验证不等于手机安装、后台权限、实际 TUN/VPN 或原版飞秋互通验收。

## 素材许可

Windows 的 144 枚彩色表情来自 [Twemoji v16.0.1](https://github.com/jdecked/twemoji/tree/v16.0.1)，版权归 Twitter, Inc. 及贡献者，按 [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) 使用。原图仅合并为本地图集；完整许可见 [Twemoji-LICENSE-GRAPHICS.txt](docs/Twemoji-LICENSE-GRAPHICS.txt)，并嵌入 EXE。运行时不联网下载表情。

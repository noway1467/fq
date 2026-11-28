# WP-5 小文件基准与协议

基准直接编译 Windows `LanService` 与 Android `LanNode` 的生产核心，使用真实 TCP、双向证书认证和 TLS 1.2。Android 侧运行在 JDK 17，测量内部文件写入、刷盘、SHA-256 和不覆盖提交；不包含手机硬件或 SAF provider 导出的性能。

## 复现

在项目根目录用 PowerShell 7 执行。每次使用不同标签，脚本拒绝覆盖已有报告。

```powershell
pwsh -NoProfile -File tests/small-file-bench.ps1 -Count 300 -Size 4096 -Label before -WindowsRevision 7673b60 -AndroidRevision 7673b60
pwsh -NoProfile -File tests/small-file-bench.ps1 -Count 300 -Size 4096 -Label after -BaselineReport build/tmp/wp5/bench/before/result.json
```

`-OutputDirectory` 可指定当前项目 `build/tmp/` 下的其他目录；默认 `build/tmp/wp5/bench/`。`-JavaHome` 可指定既有 JDK。`-WindowsRevision` / `-AndroidRevision` 分别只读编译指定 Git 版本，便于验证新旧端混连。省略版本参数时使用当前工作区源码。

每个标签生成 `result.json`、`result.md` 和双端日志。指定 `-BaselineReport` 时生成 `comparison.md`。文件数、大小和方向必须与基线相同。数据包含首件、总耗时、件/秒、P50/P95、文件握手与全部握手、完整快照次数、确认日志次数、分段耗时及进程资源。

首次配对、邀请发布和文件准备不计入传输耗时。每件延迟测量实际 `receiveFile` 调用；分段计时只注入临时源码副本，EXE/APK 不带基准计时代码。资源按约 25 ms 采样，峰值包含准备阶段，CPU 百分比按逻辑核心总数归一。两端分段可能重叠，耗时占比不能相加。

## 选择优化的依据

先记录未经优化的双向数据，再修改对应热路径。本轮首次 300 × 4 KiB 基线中，Windows→Android 的完整邀请快照耗时占总耗时 67.9%，Android→Windows 占 95.9%；TLS 等待与快照持锁重叠。Android 每次快照重复解析每个源文件的 canonical path，是其中的主要成本。

先合并快照并复用邀请发布时固定的来源标识。复测后，两方向的建连/TLS 仍占约 53% 和 56%，因此追加直连串行复用。接收调度、全局串行暂停、SAF 保存和同名保护保持原有实现。

## 独立批量能力

仅在已经固定证书与设备身份的 TLS Hello 中附加 `FQBATCH=1\0`。`FQREV=1` 继续只表示反向连接能力。发现报文、历史记录或另一条连接的能力不能授予复用资格。

新端直连 `F` 的每件顺序：

1. 请求方发送原有帧 `F\n<监听端口>` 和带设备 ID、指纹、登录名的 `GET_FILE` 报文；正文仍为 `<邀请号hex>:<文件号hex>:<实际偏移hex>:`。
2. 发送方逐件复核身份、当前信任、邀请、来源元数据和偏移，返回 `OK\n<总长度>\n<sha256>` 帧及剩余正文。
3. 接收方刷盘、校验完整 SHA-256、不覆盖提交，发送原有 `STORED\n<sha256>` 帧。
4. 发送方同步保存该件确认日志，再返回独立边界帧 `NEXT`；第 64 件返回 `DONE` 并关闭连接。接收方验证边界后才可复用。

每条连接最多 64 件；接收节点最多保留一条闲置直连，1.5 秒后关闭；发送方下一条完整请求的读取期限为 3 秒。连接仍计入节点原有 8 连接限额，绑定设备 ID、证书指纹、实际端点与网络代次。

缺少批量能力的旧端、以及反向 `P`，继续原来的单件路径，不读写 `NEXT/DONE`。取消、暂停、撤信、切网和异常帧会丢弃对应连接。Android 在网络提交后的 SAF 保存阶段取消时，也关闭该接收任务的闲置批量连接。

仅在借用的闲置 `F` 连接出现 EOF/连接重置、尚未收到该件正文、且信任和网络代次未变时，允许换新连接重试一次。取消、撤信、身份拒绝、错误响应格式和摘要不匹配均不重放。

## 确认持久化

完整快照格式保持不变。新增本机文件 `transfer-offer-completions.log`，每行六项：邀请号、文件号、有效期、SHA-256、接收设备 ID、证书指纹。Windows 有效期使用 UTC ticks，Android 使用 epoch milliseconds；该文件不用于双端交换。

每件先同步写入小日志，随后才发布已校验保存状态。完整快照每累计 32 件或最多等待 250 ms 合并一次；全部邀请完成、连接批次结束、服务断开/停止、切网、撤信，以及发布/撤销新邀请等关键边界强制写入。日志硬上限 256 KiB，完整快照原子提交成功后才清日志，保存失败保留恢复数据并报告错误。

启动时先校验日志并合入匹配的设备、指纹、摘要与有效期，再判断源文件、授权和过期状态。只忽略末尾没有换行的半条；完整坏记录报错。追加前也修剪上次 IO 失败留下的半条，避免损坏下一条。完成态不因晚到反向请求或失败重收而退回待处理；历史“已发送（非保存确认）”同样保留完成额度语义。完成项仍单独保留最近 1000 份，后续发送压力下淘汰，不占待处理额度。

## 定向回归

```powershell
pwsh -NoProfile -File tests/small-file-transfer.ps1
```

覆盖独立能力、80 件跨越连接上限、逐件授权、坏下一帧、立即撤信、实际子进程崩溃、确认日志/续期/半条恢复、完整坏日志拒绝，以及 1105 份完成项淘汰后保留 100 份待处理并跨重启恢复。可用 `-Case batch|boundary|recovery|eviction` 定向运行。日志默认在 `build/tmp/wp5/regress/logs/`。

恢复与兼容还需运行 `android-interop.ps1`、`partial-interop.ps1`、`reverse-interop.ps1`、`secure-upgrade.ps1`、`transfer-features.ps1`、Windows `build.ps1 -Test`、Android `FileFeaturesTest` / `TransferFeatureTest` / `ReliabilityTest` 与 lint。新增批量行为不替代原有暂停、断点、单份保存或 SAF 回归。

`NetworkRecoveryTest` 中的新端接收断言已改为：完成单项不再持有取消 socket，空闲连接仍绑定第二次正确建连的 socket，关闭已完成任务不会取消后续文件。旧端单件路径的关闭与清理断言保留。

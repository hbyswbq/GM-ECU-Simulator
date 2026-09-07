# GM ECU 模拟器（中文版）

一个独立的 Windows 应用程序，模拟一个或多个 GM（GMLAN / GMW3110-2010）ECU，并**将自身注册为真实的 J2534 PassThru 设备**。任何支持 J2534 的主机——Tech 2 Win、GDS、MDI、您自己的记录器——都会通过标准注册表路径加载它，并像连接 Tactrix OpenPort 或 MongoosePro 一样连接到它。

它提供**三种连接方式**：上述 J2534 路径（默认，用于真实诊断主机）；另一种**原始 CAN TCP 连接**——一个 localhost 套接字，允许另一个程序（例如单独开发的仪表或数据记录器模拟器）直接加入同一个虚拟总线，而无需通过 J2534；或者一个**物理硬件桥接**，通过 IXXAT USB-to-CAN 或 OBDX Pro 适配器将虚拟总线镜像到真实 CAN 线上，这样真实硬件（例如 ESP32 CAN 显示器）就可以加入总线。您可以从模式下拉菜单中选择连接方式；请参阅下面的[支持的协议](#支持的协议)。

**免责声明：** 此代码库 100% 由 AI（Claude Code）编写。它可以构建、运行并针对真实 J2534 主机正确注册，但每一行源代码——协议处理器、IPC 层、原生 shim、UI——都是由模型生成的。请相应对待：在信任它处理任何重要事务之前，请先阅读代码。

## 支持的协议

`PassThruConnect` 接受两种协议：

- **`ProtocolID.CAN` (5)** - 原始 CAN 帧转发。主机自行构建 ISO-TP（PCI 字节在前，然后是有效载荷）；模拟器通过 `IsoTpReassembler` 和 `IsoTpFragmenter` 处理 ECU 端的一半，遵守来自所选 ECU 检查器的每个 ECU 的 FC.BS / FC.STmin。

- **`ProtocolID.ISO15765` (6)** - shim 在 `Iso15765Channel` 中自行运行 ISO 15765-2：分段、流控握手、重组。主机读取和写入完整的 USDT 有效载荷。`PassThruIoctl SET_CONFIG` 接受标准的 BS / STmin / WFT_MAX 参数；`PassThruStartMsgFilter` 接受用于寻址对的 `FLOW_CONTROL_FILTER`。

其他协议（J1850、ISO9141、KWP2000）返回 `ERR_INVALID_PROTOCOL_ID`。拒绝会记录在 J2534 调用窗格中，并作为命名被拒绝协议的状态栏消息显示。

现代 GM 诊断栈（Tech 2 Win、GDS、SPS）通常使用 ISO15765；已经实现自己 ISO-TP 的传统或手工测试器可以保持在原始 CAN 上。

**三种传输方式。** 与上述协议正交的是*对等方如何到达总线*，在模式下拉菜单中选择：

- **J2534** - 原生 `PassThruShim` DLL 通过命名管道 `\\.\pipe\GmEcuSim.PassThru`（真实 J2534 主机使用的注册表发现路径）转发每个 PassThru 调用。

- **TCP** - localhost TCP 监听器（`RawCanTcpServer`）承载原始 CAN 帧，因此单独开发的仪表/数据记录器模拟器可以像总线上的另一个节点一样加入同一个虚拟总线。线路仅承载单个 CAN 帧（从不重组的 USDT）；ISO-TP 在两端运行。

- **Hardware** - `HardwareCanServer` 通过 `ICanAdapter`（通过 VCI4 API 的 IXXAT USB-to-CAN V2，或通过 USB/WiFi 的 OBDX Pro 扫描工具）将虚拟总线桥接到**物理** CAN 线，因此真实硬件——例如 ESP32 CAN 显示器——可以看到 ECU 的广播+诊断流量并可以向总线传输。以 GM 正常模式速率（500 kbit/s，11 位）打开；设备选择器选择适配器。DBC 应用广播通过此链路流动（以及通过 TCP）。OBDX BLE 尚未启用。`Shim/Hardware/` 适配器层是从同级 CAN-Tool 项目复制的。

## 功能

- 实现真实测试器需要的 GMW3110-2010 服务：`$10` InitiateDiagnosticOperation、`$1A` ReadDataByIdentifier (DID)、`$20` ReturnToNormalMode、`$22` ReadDataByParameterIdentifier、`$27` SecurityAccess、`$28` DisableNormalCommunication、`$2C` DynamicallyDefineDataIdentifier、`$2D` DefinePidByAddress、`$34` RequestDownload、`$36` TransferData、`$3B` WriteDataByIdentifier、`$3E` TesterPresent、`$A2` ReportProgrammedState、`$A5` ProgrammingMode、`$AA` ReadDataByPacketIdentifier（周期性 UUDT 推送，慢/中/快频段）、`$AE` RequestDeviceControl。

- OBD-II / J1979 `$01` ShowCurrentData。在真实 GM 芯片上，`$01` 位于单独的 UDS 栈调度器上，因此它在 OBD CAN ID（`$7DF` / `$7E0`）上应答，而纯 GMW3110 请求得到 NRC `$11`。值是通过信号层（见下文）的法定 J1979 投影——支持列表 PID（`$00` / `$20` / ...）是从每个 ECU 的广告子集计算的，从不存储。

- **每个 ECU 的协议栈绑定**（`Core/Protocol/`）。每个 ECU 将入站 `(CAN id, SID)` 解析为栈绑定并在那里调度：GM ECU 绑定 J1979 + GMW3110（`Gmw3110Dispatch`）；在成功的 `$36` 子 `$80` DownloadAndExecute 之后，瞬态 UDS (ISO 14229) 内核绑定（`UdsKernelDispatch`）接管，呈现真实 GM SPS 内核暴露的服务（`$31` RoutineControl - EraseMemory、CheckMemoryByAddress、最终确定 - 加上 `$3E` / `$20` / 第二阶段 `$34`/`$36`），并在 `$20` 或 P3C 超时时拆除。Ford ECU 绑定单个通用 UDS 捕获栈（`FordUdsDispatch`），用于重放捕获的 Ford 栈流量。每个绑定的服务允许列表可在 ECU 的高级选项卡中编辑，并作为增量持久化（`EcuDto.Stacks`）。

- 真实的 ISO-TP 分段/重组、流控帧、P3C 超时处理（标称 5000 ms）、空闲总线检测。

- 虚拟 CAN 总线上的 N 个并发虚拟 ECU，按目标 CAN ID 路由。

- **以信号为中心的 PID 值。** 每个 ECU 都有一个实时 `EngineModel`，它将选定的场景（Key-On Engine-Off / Idle / Cruise / Accel-Decel Sweep）转换为任何信号的连续可读、时间驱动的值——主信号（RPM / 速度 / 节气门 / 负载）以每个信号的时间常数向其场景目标缓和，派生信号（MAP / MAF / 火花 / 燃油修正 / O2）在每次读取时从主信号重新计算，因此它们保持相互一致。模型特定的一半是可插拔的**引擎特性**（通过 `EngineCharacterRegistry` 的 `IEngineCharacter`）："自然吸气 V8"（`na-gas-v8`，默认）和"增压 V8"（`boosted-gas-v8`）在 MAP / 气流 / 供油方面读取不同。在编辑器中交换特性以交换引擎。非模拟状态（`MIL`、存储的 DTC 计数、燃油系统状态）来自每个 ECU 的 `DiscreteState`。

- 每个 PID 行，一个**值来源**（`PidValueSource`）选择线路值的来源：`Signal`（由命名的 `EngineModel` 信号驱动，使用行的标量/偏移/数据类型编码）、`Waveform`（它自己的正弦/三角/方波/锯齿/常量生成器，或 `.bin` 日志重放），或 `None`（平 0，除非该行携带静态有效载荷）。

- **多模式 PID 存储。** 单个 PID 行通过 `PidMode` 选择器路由到三个服务之一：`$22` ReadDataByParameterIdentifier（2 字节线路 id）、`$1A` ReadDataByIdentifier（1 字节 DID，静态有效载荷），或 `$2D` DefinePidByAddress（32 位地址在启动时镜像到 `$F000` 范围的线路 id）。

- **DBC 驱动的 CAN 广播。** 每个 ECU，导入 `.dbc` 以发出非请求应用帧——被动记录器看到的背景动力总成流量，而不是请求/响应诊断。导入是有范围的（选择发送模块，然后勾选要广播的消息）。每个信号在其 DBC 起始位/长度/字节顺序处用 DBC 比例+偏移进行位打包，并映射到实时 `EngineModel` 信号（因此它跟踪活动场景）或常量；消息在 J2534 主机会话打开时以其 `GenMsgCycleTime`（可编辑，自由格式 ms）发出。在 ECU 编辑器的 **CAN 广播** 部分编辑它，并将广播集保存/加载为独立的 `*.dbc.json`。

- 标识 DID（`$90` VIN、`$92`/`$98` 供应商硬件、`$C1`/`$C2` 部件号、`$CC` ECU 诊断地址）每个 ECU 可编辑，并可以通过跟踪真实闪存映像中的 `$1A` 处理器自动填充。

- **两种应用模式。** "ECU 模拟器"（多个 ECU，完整编辑器，状态持久化）和"DPS 模拟器"（单 ECU 编程会话工作流 - 从 DPS 存档准备并驱动目标 ECU 完成闪存）。模式下拉菜单还选择传输方式（上述支持的协议下的传输方式）："ECU 模拟器 - J2534"、"ECU 模拟器 - TCP"，或"ECU 模拟器 - 硬件"。

- 引导加载程序捕获切换：捕获开启时，`$36` 有效载荷在会话结束时写入磁盘，因此可以提取和检查真实的 SPS 内核（或任何其他下载的代码）。

- 默认为 OBD-II 约定（`$7E0` 请求，`$7E8` USDT 响应，`$5E8` UUDT 响应）。每个 ECU 的 ID 可编辑。

## 安全 ($27)

`$27` 在两层插件接口后面实现，因此不同的 GM 种子密钥风格可以按 ECU 插入，而无需接触调度器：

- **`ISecurityAccessModule`** - 拥有整个 `$27` 交换步骤。捆绑的 `Gmw3110_2010_Generic` 模块涵盖 GMW3110-2010 协议信封（长度验证、子功能奇偶校验、待处理种子跟踪、带有 10 秒截止时间戳恢复的 3 次锁定、NRC `$12` / `$22` / `$35` / `$36` / `$37` 路径）。模块的 `SecurityModuleBehaviour`（`Strict` 或 `BypassAll`）在构造时设置，与密码正交——同一个密码类可以连接到任何一个。

- **`ISeedKeyAlgorithm`** - 您通常编写的小策略。约 30 行。`Gmw3110_2010_Generic` 包装一个并处理其他所有事情。

开箱即用地注册了六个模块，位于 `gm-{ecmFamily}-{width}` / `gm-bypass-{width}` 轴上。严格条目以它们目标的 ECM 系列命名——社区的"Algo 92"/"Algo 89"归属太缺乏来源，无法在 ID 中承担责任：

| 模块 ID | 种子/密钥 | 密码 | 行为 |
|---|---|---|---|
| `gm-e38-2byte` | 2/2 | 通过非 DPS 测试器（HPT、EFILive、jakka351）的 E38 ECM。`k = ~(bswap(s)+0x7D58)+0x8001`。社区标记为"GMLAN 0x92"，但算法编号归属缺乏来源。 | Strict |
| `gm-e92-5byte` | 5/5 | 通过 DPS 的 E92 系列 ECM。于 2026-05-17 通过日志记录代理（`tools/sa015bcr_hook/`）逆向工程，并针对 7 个已知种子/密钥对进行了验证。"92"基于此系列的 DPS 实用文件携带的实际 `algoId` 字节。默认为从 2026-05 DPS 4.52 运行捕获的 E92 密码；在 `SecurityModuleConfig` 中用 `password` / `algoId` / `familyByte` / `fixedSeed` 覆盖。 | Strict |
| `gm-e67-2byte` | 2/2 | E67 ECM。从 PowerPCM_Flasher 的 `KeyAlgoGm_$89`（RVA 0x6670）提取；尽管两者都被社区标记为"GMLAN"，但在所有 65536 个种子上与 `gm-e38-2byte` 暴力区分。 | Strict |
| `gm-t43-2byte` | 2/2 | T43 TCM（6T70 系列）`gett43key`，从 6Speed.T43 FOSS 源代码移植。GM 算法编号尚未记录。 | Strict |
| `gm-bypass-2byte` | 2/2 | `RandomSeedCipher(2)`。发出非零随机种子（或 `fixedSeed` 配置）并接受任何密钥。 | BypassAll |
| `gm-bypass-5byte` | 5/5 | `RandomSeedCipher(5)`。相同，5 字节宽度用于我们尚未捕获算法的 DPS Enhanced 5 字节实用文件。 | BypassAll |

对于存根安全 ECU（T43 引导块、"让任何测试器通过"测试场景），选择一个 `gm-bypass-*` 模块。它们无条件短路 `$27`——没有会话状态门控——因此 requestSeed 返回种子 `00 00`（DPS / CCRT"已解锁"约定）并且任何 sendKey 都被接受。

每个 ECU 选择的模块 ID + 模块配置 blob 持久化到每个模式的配置文件（ECU 模拟器模式下的 `ecu_simulator.mode.json`）。来自每个先前命名传递的旧 ID（原始系列名称 `gm-e38` / `gm-e67` / `gm-t43` / `gm-e92`，简短的算法轴名称 `gm-algo92-2byte` / `gm-algo89-2byte` / `gm-algo92-5byte` / `gm-algo-92`，以及行为命名的绕过条目 `gm-programming-bypass` / `gm-permissive-5byte` / `gmw3110-2010-not-implemented`）在加载时通过 `SecurityModuleRegistry.NormaliseLegacyId` 重新映射到它们当前的等效项。架构当前处于**版本 17**，**最低支持版本 16**——v16 是以信号为中心的重新设计的干净突破基线，因此在它之前写入的配置**被拒绝并显示版本错误，而不是静默迁移**。在 v16+ 范围内，缺失字段回退到文档化的默认值（FC.BS / FC.STmin = 0，`$36` 地址字节数 = 4，BootloaderCapture 禁用，无安全模块 -> `$27` 返回 NRC `$11`；v16 文件加载时带有空的实时磁贴仪表板）。

有关编写新算法的分步演练，请参阅 `docs/Adding a Security Access ($27) Module.md`，使用 E38 算法作为工作示例。

## 架构（一段话）

J2534 主机期望一个带有 SAE J2534-1 v04.04 定义的 14 个 C 导出的原生 DLL。C# 不能以这种方式加载，因此 `PassThruShim/` 是一个薄的原生 C++ DLL（构建为**32 位和 64 位**），其唯一工作是通过 Windows 命名管道（`\\.\pipe\GmEcuSim.PassThru`）将每个 PassThru 调用作为长度前缀的二进制帧转发。`GmEcuSimulator/` 中的 C# WPF 应用托管管道服务器，通过 `RequestDispatcher` 将帧调度到 `VirtualBus`，后者按目标 CAN ID 路由到 N 个 `EcuNode` 实例之一。每个 ECU 对入站帧运行 ISO-TP 重组，并将组装的 USDT 有效载荷交给拥有请求的 `(CAN id, SID)` 的协议栈绑定（GM ECU 为 `Gmw3110Dispatch`，内核切换后为瞬态 `UdsKernelDispatch`，Ford 捕获 ECU 为 `FordUdsDispatch`）。响应被排队回到通道的 RX 队列。`Core/` 是模拟器引擎（总线、ECU 模型、服务处理器、协议栈、ISO-TP、DPID 调度器）；`Common/` 是纯类型、协议常量和信号层（`EngineModel` + `Common/Signals/` 下的可插拔 `IEngineCharacter` 集）；`Shim/` 托管当主机将通道打开为 `ProtocolID.ISO15765` 时运行 ISO 15765-2 的每通道 `Iso15765Channel`，加上替代的 `RawCanTcpServer` 传输和物理硬件桥接（`HardwareCanServer` 通过 `Shim/Hardware/` 中的 `ICanAdapter` 层 - IXXAT VCI4 / OBDX Pro）。命名管道是默认传输；在模式下拉菜单中选择 TCP 或 Hardware 会将其交换为 localhost CAN 帧监听器或物理 CAN 桥接。

## 一致性

**仅 J2534-1 v04.04。** shim 精确导出 v04.04 定义的 14 个函数，并从 `PassThruReadVersion` 报告 `04.04`。v05.00 新增功能（`ScanForDevices`、`GetNextDevice`、`LogicalConnect`、...）和 Drew Tech 专有的 `PassThruGetNextCarDAQ` **故意不导出**。从 J2534-Sharp 主机，调用 `api.GetDevice("")` 获取默认设备 - `api.GetDeviceList()` 返回空，因为该路径仅限 v05.00。

## 构建

```powershell
# .NET 项目 (Common, Core, Shim, GmEcuSimulator)
dotnet build "GM ECU Simulator.sln" -c Debug

# 原生 shim - 两种位数（J2534 主机可以是任何一种）
$msbuild = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
& $msbuild "PassThruShim\PassThruShim.vcxproj" /p:Configuration=Debug /p:Platform=x64
& $msbuild "PassThruShim\PassThruShim.vcxproj" /p:Configuration=Debug /p:Platform=Win32

# 或者一次性完成所有三个（需要提升权限）：
.\ShimInstaller\Register.ps1 -Build
```

输出：

- `PassThruShim\x64\Debug\PassThruShim64.dll`
- `PassThruShim\Debug\PassThruShim32.dll`
- `GmEcuSimulator\bin\Debug\net9.0-windows\GmEcuSimulator.exe`

## 注册为 J2534 设备

在应用的菜单栏中点击 **J2534 -> 注册为 J2534 设备...**（UAC 提示；底层脚本以提升权限运行并退出），或直接从提升的 PowerShell 运行 `.\ShimInstaller\Register.ps1`。两者都写入相同的标准 v04.04 注册表项（`HKLM\SOFTWARE\PassThruSupport.04.04\GmEcuSim` 和 `WOW6432Node` 镜像，平面布局 - 所有值直接在 `GmEcuSim` 子键上）。

**两种位数都是必需的。** Windows 进程只能 `LoadLibrary` 自己位数的 DLL——64 位主机加载 `PassThruShim64.dll`，32 位主机加载 `PassThruShim32.dll`，从不混合。`Register.ps1` 写入两个注册表视图，每个都指向匹配的 shim。应用中的标题栏药丸反映当前状态（当两种位数都存在时为"Shim 已注册"/当只有一种位数注册时为"32 位 Shim 故障"或"64 位 Shim 故障"/当都没有时为"Shim 未注册"），在每次注册/取消注册点击后。

**诊断对话框：** **J2534 -> 显示已注册设备...** 运行 `ShimInstaller\List.ps1`（只读，无 UAC）并显示机器上每个 J2534 设备，跨两个注册表视图，带有 DLL 存在检查。用于验证更改了什么以及分类"设备不在主机中显示"的报告。

## 使用

1. 运行 `GmEcuSimulator.exe`。命名管道服务器开始监听；标题栏药丸显示 J2534 注册状态。

2. 主窗口是所选 ECU 的实时 PID 磁贴仪表板，加上菜单栏、模式/连接下拉菜单，以及两个标题栏药丸（所选 ECU 的 `$27` 安全状态、J2534 注册状态）。主窗口上没有 ECU 列表——所有 ECU 和 PID 编辑都存在于 ECU 编辑器中（下一步）。

3. 通过 **ECU -> 打开 ECU 设置...**（或 **Ctrl+Shift+P**）打开无模式 **ECU 编辑器**。它有自己的 ECU 列表（添加/删除/保存/加载 ECU）、每个 ECU 的设置（名称、CAN ID、FC.BS / FC.STmin、`$27` 安全模块、引擎特性、场景），以及 PID 表。每个 PID 行有一个**模式**（`$22` / `$1A` / `$2D`）、一个选择值来源的**信号**列（命名的 `EngineModel` 信号、行自己的波形，或无）、标量/偏移、单位字符串和大小（字节/字/双字）；**实时**列显示当前合成值。设置引擎特性（自然吸气 V8 / 增压 V8）和场景（怠速/巡航/加速-减速扫描/Key-On Engine-Off）以同时驱动每个信号支持的 PID。

4. 启动您的 J2534 主机。"GM ECU 模拟器"出现在其设备下拉菜单中。shim 被 `LoadLibrary` 到主机进程中，并将每个 PassThru 调用转发到模拟器。

5. 工作区选项卡：**总线日志** 在左侧显示实时 CAN 帧（Tx/Rx），在右侧显示 J2534 控制平面调用（打开/连接/筛选/ReadMsgs/...）；**Bin 回放**（ECU 模拟器）通过 ECU 的 PID 重放 `.bin` 捕获；**故障注入设置**（ECU 模拟器）是故障注入 UI（仅配置，尚未接入总线）；**捕获文件**（DPS 模拟器）浏览编程会话期间写入的 `$36` 下载捕获。

6. **日志** 菜单启用流式文件日志接收器，在后台线程上写入 `%LOCALAPPDATA%\GmEcuSimulator\logs\bus logs\bus_*.csv`。用于长时间捕获——窗口内文本框不虚拟化，在高消息速率下可能会冻结 GUI。

## Bin 回放

加载 `.bin` 数据记录器捕获文件（或内置的 4 通道合成演示），模拟器将通过您定义的 PID 重放记录的值。ECU 和通道按节点类型和 PID 地址自动映射。循环模式（HoldLast / Loop / Stop）可配置，加载的路径可以在下次启动时自动恢复。

## 文档

完整的用户手册位于 `docs/User Manual.pdf`（源文件：`docs/User Manual.docx`）。

## 许可证

本项目采用**双重许可**。选择符合您用途的选项：

### 爱好者和发烧友免费 (AGPL-3.0)

如果您是在自己的车辆上工作的个人、学生、研究人员，或任何将其用于**个人、非商业目的**的人，您可以根据 GNU Affero General Public License v3.0 免费使用模拟器。这就是它为之构建的整个社区——尽情使用、修改、分享、构建。AGPL 下唯一的要求是，如果您发布或网络托管修改版本，您也发布修改后的源代码。

免费使用 AGPL 的示例：

- 调校、诊断或记录您自己的汽车或朋友的汽车
- 学习 J2534、GMLAN 或 GMW3110 的工作原理
- 学术研究、课程作业或教学
- 爱好项目、博客文章、YouTube 视频、会议演讲
- 非营利开源分支（保持在 AGPL 下）

### 商业用途需要单独的许可证

任何**商业或营利性**用途都需要**付费商业许可证**。这包括（但不限于）：

- 由营利性企业或实体使用或在其内部使用，包括其员工、承包商或关联方的内部使用
- 将模拟器（或任何衍生作品）捆绑到商业产品或付费服务中
- 使用模拟器向客户提供付费诊断、调校、校准、维修或编程服务
- 将其作为 SaaS 或网络产品的一部分托管
- AGPL-3.0 的源代码披露要求与您的发布方式不兼容的任何用途

如果您不确定自己属于哪一边，只需询问——默认是爱好者，涉及金钱就是商业。

## 状态 / 范围

- 仅 v04.04 一致性——设计如此。
- `ProtocolID.CAN` 和 `ProtocolID.ISO15765` 都针对捆绑的 `$27` 流程和完整的 `$34`/`$36` 下载路径进行了端到端验证。
- 故障注入（每服务 NRC / 丢弃 / 损坏字节 / 随机）有 UI 和配置管道，但运行时尚未被调度器查询。
- 此 README 中的屏幕截图正在重新捕获以匹配当前 UI；

#!/usr/bin/env python3
"""
GM ECU Simulator - XAML Chinese Localization Script
Translates static UI strings in XAML files while preserving bindings, code, and technical terms.
"""
import re
import os
import sys

# Comprehensive translation dictionary for XAML static strings
# Key: exact English string, Value: Chinese translation
TRANSLATIONS = {
    # Window titles
    "GM ECU Simulator": "GM ECU 模拟器",
    "ECU Editor": "ECU 编辑器",
    "Import DBC": "导入 DBC",
    "Registered J2534 Devices": "已注册的 J2534 设备",
    "Supported protocols": "支持的协议",
    "Select CAN hardware": "选择 CAN 硬件",
    "Resolve CAN ID conflicts": "解决 CAN ID 冲突",
    "Message": "消息",

    # Menu items
    "_File": "文件(_F)",
    "_New": "新建(_N)",
    "_Open…": "打开…(_O)",
    "_Save": "保存(_S)",
    "Save _As…": "另存为…(_A)",
    "C_lear primed archive": "清除已加载的存档(_L)",
    "E_xit": "退出(_X)",
    "_J2534": "J2534(_J)",
    "_Register as J2534 device…": "注册为 J2534 设备…(_R)",
    "_Unregister": "取消注册(_U)",
    "_Show registered devices…": "显示已注册设备…(_S)",
    "Reset _IPC pipe": "重置 IPC 管道(_I)",
    "Drive HW _$3E keepalives": "驱动硬件 $3E 保活(_H)",
    "_ECU": "ECU(_E)",
    "Open ECU _settings…": "打开 ECU 设置…(_S)",
    "Reset _ECU State": "重置 ECU 状态(_R)",
    "_Tools": "工具(_T)",
    "_Developer": "开发者(_D)",
    "Add _blank ECU": "添加空白 ECU(_B)",
    "_Log": "日志(_L)",
    "_Log to file": "记录到文件(_L)",
    "_Open folder…": "打开文件夹…(_O)",
    "Include _J2534 calls": "包含 J2534 调用(_J)",
    "Include _bus traffic": "包含总线流量(_B)",
    "_Append description tag": "附加描述标签(_A)",
    "_Collapse bulk transfers": "折叠批量传输(_C)",
    "_Theme": "主题(_T)",
    "_About": "关于(_A)",
    "_Protocols…": "协议…(_P)",
    "View _releases on GitHub…": "在 GitHub 上查看发布…(_V)",

    # Mode / connection
    "Mode": "模式",

    # Status bar
    "Pipe": "管道",

    # Tabs
    "Bus log": "总线日志",
    "Bin Replay": "Bin 回放",
    "Glitch settings": "故障注入设置",
    "Captures": "捕获文件",

    # Toolbar buttons and labels
    "Log traffic": "记录流量",
    "Clear": "清除",
    "Maximize": "最大化",
    "Hide $3E": "隐藏 $3E",
    "Hide broadcasts": "隐藏广播",
    "Not logging": "未记录",

    # Log pane headers
    "CAN frames": "CAN 帧",
    "Tx / Rx": "发送 / 接收",
    "J2534 calls": "J2534 调用",
    "control plane": "控制平面",

    # PID dashboard
    "PIDs": "PID 列表",
    "pinned": "已固定",
    "Add": "添加",
    "Delete": "删除",
    "Pin a PID to the dashboard": "将 PID 固定到仪表板",
    "Filter by diagnostic mode": "按诊断模式筛选",
    "Filter by PID id or name": "按 PID ID 或名称筛选",

    # Glitch settings
    "ECU": "ECU",
    "(none selected)": "(未选择)",
    "Enable glitch injection": "启用故障注入",
    "prep - not yet wired into bus logic": "准备中 - 尚未接入总线逻辑",
    "Service": "服务",
    "Probability %": "概率 %",
    "Action": "动作",
    "NRC pool": "NRC 池",
    "When EmitNrc fires, an NRC is randomly chosen from the ticked entries.": "当触发 EmitNrc 时，从勾选的条目中随机选择一个 NRC。",

    # Captures tab
    "Open folder": "打开文件夹",
    "Refresh": "刷新",
    "CAPTURE DIRECTORY": "捕获目录",
    "CAPTURED FILES": "已捕获文件",
    "Each row is one programming session (a $34/$36 sequence ending in $20 ReturnToNormalMode or a P3C timeout). Filename embeds ECU name, UTC timestamp, base $36 address, and byte count.": "每一行代表一个编程会话（$34/$36 序列，以 $20 返回正常模式或 P3C 超时结束）。文件名包含 ECU 名称、UTC 时间戳、$36 基地址和字节数。",
    "File": "文件",
    "Size": "大小",
    "Modified": "修改时间",

    # EcuSetupWindow - ECU sidebar
    "ECUs": "ECU 列表",
    "configured": "已配置",
    "Save the selected ECU (CAN IDs, security module, PIDs, waveforms) to a *.ecu.json file.": "将所选 ECU（CAN ID、安全模块、PID、波形）保存到 *.ecu.json 文件。",
    "Load an ECU from a *.ecu.json file and add it to the bus.": "从 *.ecu.json 文件加载 ECU 并添加到总线。",
    "Add a blank ECU at the next free OBD-II ID.": "在下一个空闲 OBD-II ID 处添加空白 ECU。",
    "Remove the selected ECU from the bus. Prompts for confirmation.": "从总线移除所选 ECU。将提示确认。",

    # EcuSetupWindow - Selected ECU header
    "Selected ECU": "所选 ECU",

    # Sort/Filter popup
    "Sort Ascending": "升序排序",
    "Sort Descending": "降序排序",
    "Clear Sort": "清除排序",
    "Filter": "筛选",
    "Clear Filter": "清除筛选",
    "Double-click to sort (toggles ascending/descending); right-click to sort or filter this column.": "双击排序（切换升序/降序）；右键排序或筛选此列。",
    "This column is part of the sort order.": "此列是排序顺序的一部分。",
    "Sort priority (1 = primary key).": "排序优先级（1 = 主键）。",
    "A filter is active on this column.": "此列上有活动筛选器。",

    # Bin Replay
    "Load…": "加载…",
    "Load synthetic demo": "加载合成演示",
    "Unload": "卸载",
    "File": "文件",
    "Loop mode": "循环模式",
    "Auto-load on app start": "应用启动时自动加载",
    "Open a .bin produced by the sibling GM DataLogger. Channel headers carry Name, Unit, Address, NodeType, Size, DataType, Scalar, Offset.": "打开由配套 GM DataLogger 生成的 .bin 文件。通道头包含名称、单位、地址、节点类型、大小、数据类型、标量、偏移量。",
    "Loads a deterministic 4-channel in-memory bin (RPM/TPS/Coolant/TransTemp). For wiring up the start/stop hooks before the real bin loader lands.": "加载确定性的 4 通道内存 bin（RPM/TPS/冷却液/变速箱温度）。用于在真实 bin 加载器完成前连接启动/停止钩子。",
    "HoldLast freezes at the last sample. Loop wraps to the start. Stop transitions to Stopped at end-of-bin.": "HoldLast 在最后一个样本处冻结。Loop 循环到开头。Stop 在 bin 结束时转换为已停止。",
    "Persists the loaded bin path in ecu_config.json and re-loads on next launch.": "将加载的 bin 路径保存在 ecu_config.json 中，并在下次启动时重新加载。",
    "Address": "地址",
    "Name": "名称",
    "Unit": "单位",
    "Size": "大小",
    "Live": "实时值",

    # DBC Import
    "Cancel": "取消",
    "OK": "确定",
    "A DBC describes the whole bus. Pick the sending module, then choose which of its messages to broadcast.": "DBC 描述整个总线。选择发送模块，然后选择要广播的消息。",
    "This ECU already has broadcasts. Ticked rows below are its existing CAN ids found in this DBC - untick one to remove it, tick a new one to add it. Rows whose id isn't in this DBC are left as they are.": "此 ECU 已有广播。下方勾选的行是在此 DBC 中找到的现有 CAN ID - 取消勾选以移除，勾选新行以添加。ID 不在此 DBC 中的行保持不变。",
    "Transmitter": "发送器",
    "Select all": "全选",
    "None": "全不选",

    # Registered Devices
    "Close": "关闭",
    "J2534 device registry": "J2534 设备注册表",
    "Output of ShimInstaller\\List.ps1 - every J2534 PassThru device registered on this machine, both bitnesses. Read-only diagnostic.": "ShimInstaller\\List.ps1 的输出 - 本机注册的每个 J2534 PassThru 设备，包括两种位数。只读诊断。",

    # Protocols Window
    "answered": "已响应",
    "always NRC": "始终 NRC",
    "Supported protocols and services": "支持的协议和服务",
    "The canonical registry of what this simulator answers. Every service each protocol defines is listed; a ticked box means the app has a positive (non-NRC) response path, an empty box means the request is always answered with a negative response. The boxes are read-only.": "此模拟器响应内容的规范注册表。列出每个协议定义的每个服务；勾选框表示应用有正向（非 NRC）响应路径，空框表示请求始终以否定响应回答。框为只读。",

    # Hardware Picker
    "Connect": "连接",
    "Choose the physical CAN adapter that bridges the simulator to a real bus.": "选择将模拟器桥接到真实总线的物理 CAN 适配器。",
    "IXXAT USB-to-CAN needs its VCI driver installed; OBDX Pro lists each USB serial port plus the WiFi SoftAP. The bus opens at the GM rate (500 kbit/s, 11-bit).": "IXXAT USB-to-CAN 需要安装 VCI 驱动；OBDX Pro 列出每个 USB 串口以及 WiFi SoftAP。总线以 GM 速率打开（500 kbit/s，11 位）。",

    # Broadcast Conflict
    "These CAN IDs already exist in the broadcast table, but this DBC defines them with a different shape.": "这些 CAN ID 已存在于广播表中，但此 DBC 以不同的格式定义它们。",
    "An arbitration ID can only carry one frame. Tick Replace to take the imported definition (your existing row and its source mappings are discarded); untick to keep your existing row and ignore this DBC's version.": "仲裁 ID 只能携带一个帧。勾选替换以采用导入的定义（现有行及其源映射将被丢弃）；取消勾选以保留现有行并忽略此 DBC 版本。",
    "Existing: ": "现有：",
    "Imported: ": "导入：",
    "Replace": "替换",

    # Window control tooltips
    "Minimize": "最小化",
    "Maximize": "最大化",

    # Common
    "No ECU primed": "未加载 ECU",

    # EcuSetupWindow - ECU settings form
    "ECU settings": "ECU 设置",
    "Req CAN": "请求 CAN",
    "USDT resp": "USDT 响应",
    "UUDT resp": "UUDT 响应",
    "Diag addr": "诊断地址",
    "Sec module": "安全模块",
    "Engine model": "引擎模型",
    "Scenario": "场景",
    "Edit prime...": "编辑加载...",
    "Re-open the DPS Prime Wizard with this ECU's prior selections pre-filled. Replaces the ECU atomically on Apply.": "使用此 ECU 之前的选择重新打开 DPS 加载向导。应用时原子替换 ECU。",

    # EcuSetupWindow - CAN Broadcast
    "CAN Broadcast": "CAN 广播",
    "Import DBC...": "导入 DBC...",
    "Parse a .dbc file, pick the transmitter + messages, and add them to this ECU's broadcast set.": "解析 .dbc 文件，选择发送器和消息，并添加到此 ECU 的广播集。",
    "Save this ECU's broadcast set to a *.dbc.json file.": "将此 ECU 的广播集保存到 *.dbc.json 文件。",
    "Load a *.dbc.json file and replace this ECU's broadcast set.": "加载 *.dbc.json 文件并替换此 ECU 的广播集。",
    "Add a new broadcast message. Set its CAN ID, period and signals after.": "添加新的广播消息。之后设置其 CAN ID、周期和信号。",
    "Remove the selected broadcast message.": "移除所选广播消息。",
    "Show / hide this message's signals": "显示/隐藏此消息的信号",
    "On": "启用",
    "CAN ID": "CAN ID",
    "DLC": "DLC",
    "Period (ms)": "周期(ms)",
    "Signals": "信号",
    "Signal": "信号",
    "Bit": "位",
    "Len": "长度",
    "Ord": "字节序",
    "Scale": "比例",
    "Offset": "偏移",
    "Source": "来源",
    "Const": "常量",
    "Where this field's value comes from: (none)=0, Constant=the fixed value, or a live engine signal.": "此字段值的来源：(无)=0，常量=固定值，或实时引擎信号。",

    # EcuSetupWindow - Diagnostic PIDs
    "Diagnostic PIDs": "诊断 PID",
    "Seed default PIDs": "植入默认 PID",
    "Add the curated default PID set (baseline $1A identity DIDs + the 13 live $22 PIDs) to this ECU. Opt-in: seeding no longer happens automatically, so deleted rows do not reappear on reload. Existing rows are never overwritten; disabled for DPS-primed ECUs.": "将精选的默认 PID 集（基线 $1A 标识 DID + 13 个实时 $22 PID）添加到此 ECU。可选：植入不再自动发生，因此删除的行不会在重新加载时重新出现。现有行永远不会被覆盖；DPS 加载的 ECU 禁用。",
    "Save this ECU's $1A/$22/$2D PID list (including waveform settings) to a .pids.json file. Does not include ECU settings or CAN broadcasts - use the sidebar's Save ECU for the whole ECU.": "将此 ECU 的 $1A/$22/$2D PID 列表（包括波形设置）保存到 .pids.json 文件。不包括 ECU 设置或 CAN 广播 - 使用侧边栏的保存 ECU 保存整个 ECU。",
    "Load a .pids.json file and replace this ECU's $1A/$22/$2D PID list with its contents.": "加载 .pids.json 文件并用其内容替换此 ECU 的 $1A/$22/$2D PID 列表。",

    # EcuSetupWindow - OBD-II
    "$01 (OBD-II)": "$01 (OBD-II)",
    "PID": "PID",
    "Bytes": "字节",
    "Raw": "原始值",
    "Decoded": "解码值",
    "Advertise this $01 PID. Drives the $00/$20 support bitmask; unchecking makes a tester's request for it return NRC $31.": "广播此 $01 PID。驱动 $00/$20 支持位掩码；取消勾选会使测试仪的请求返回 NRC $31。",

    # EcuSetupWindow - PID grid columns
    "Value": "值",
    "Add a new row to this mode. The address auto-picks the next free byte range; rename and re-point it after.": "在此模式下添加新行。地址自动选择下一个空闲字节范围；之后重命名和重新指向。",
    "Remove the selected row(s) in this section. Ctrl/Shift-click to select multiple rows first.": "移除此部分中所选的行。先按 Ctrl/Shift 点击选择多行。",
    "Response length in bytes (1-99). E.g. a VIN is 17 bytes. Read-only for $1A - the identity value's length drives it.": "响应长度（字节，1-99）。例如 VIN 为 17 字节。$1A 为只读 - 标识值的长度驱动它。",
    "Where this PID's value comes from. '(none)' = a flat 0; 'Waveform' = the row's waveform generator; any signal = the live engine model (encoded with this row's Scalar/Offset/Size).": "此 PID 值的来源。'(无)' = 固定 0；'波形' = 行的波形生成器；任何信号 = 实时引擎模型（使用此行的比例/偏移/大小编码）。",

    # EcuSetupWindow - $A1 DMR
    "$A1 (SetupDataMode)": "$A1 (SetupDataMode)",
    "Map a DMR RAM address (the value PCMTec reads back during a datalog) to an engine-simulator signal. The Ford persona writes the mapped signal as a big-endian float into that slot's 0x6A0 stream; unmapped addresses default to Engine RPM.": "将 DMR RAM 地址（PCMTec 在数据记录期间读回的值）映射到引擎模拟器信号。Ford 角色将映射的信号作为大端浮点数写入该槽的 0x6A0 流；未映射的地址默认为引擎 RPM。",
    "Reject $A1 for addresses not in this grid (NRC $31)": "拒绝不在此网格中的地址的 $A1（NRC $31）",
    "When ticked, a $A1 SETUP_DMR whose RAM address has no row above is answered with NRC $31 RequestOutOfRange instead of binding the slot. Default off (accept any address) keeps PCMTec's datalog progressing; tick only if every address PCMTec polls is already mapped here, or its datalog won't start.": "勾选时，RAM 地址在上方没有行的 $A1 SETUP_DMR 将以 NRC $31 RequestOutOfRange 回答，而不是绑定槽。默认关闭（接受任何地址）保持 PCMTec 数据记录进行；仅当 PCMTec 轮询的每个地址都已在此映射时才勾选，否则其数据记录不会启动。",
    "Add a DMR address -> signal mapping.": "添加 DMR 地址 -> 信号映射。",
    "Remove the selected mapping.": "移除所选映射。",
    "DMR Address": "DMR 地址",
    "Engine Signal": "引擎信号",
    "Encoding": "编码",

    # EcuSetupWindow - Waveform inspector
    "Waveform": "波形",
    "Generator for the selected PID.": "所选 PID 的生成器。",
    "Shape": "形状",
    "Amplitude": "振幅",
    "Freq (Hz)": "频率(Hz)",
    "Phase (°)": "相位(°)",
    "Duty (0..1)": "占空比(0..1)",
    "CSV file": "CSV 文件",
    "Browse...": "浏览...",
    "On end": "结束时",

    # EcuSetupWindow - Advanced
    "Advanced": "高级",
    "Per-ECU diagnostic-stack settings.": "每个 ECU 的诊断栈设置。",
    "Persona": "角色",
    "Diagnostic dispatch table this ECU presents on the wire. GM Gen 4 = the GMW3110 stack; Ford = the Ford UDS (PCMTec) dispatcher. Applies to this ECU only and resets its security state.": "此 ECU 在总线上呈现的诊断调度表。GM Gen 4 = GMW3110 栈；Ford = Ford UDS (PCMTec) 调度器。仅适用于此 ECU 并重置其安全状态。",
    "Seed/key algorithm for this ECU. (none) makes $27 return NRC $11. Picking an entry rebuilds the security module and clears any prior unlock / lockout state.": "此 ECU 的种子/密钥算法。(无) 使 $27 返回 NRC $11。选择条目会重建安全模块并清除任何先前的解锁/锁定状态。",
    "Read as": "读取为",
    "Which flash-READ protocol this ECU answers on $35/$36. E38 / E67 = PowerPCM_Flasher native upload; T43 = the 6Speed.T43 read-kernel ($99 handshake + $35 multi-frame blocks). Applies to this ECU only.": "此 ECU 在 $35/$36 上响应的闪存读取协议。E38 / E67 = PowerPCM_Flasher 原生上传；T43 = 6Speed.T43 读取内核（$99 握手 + $35 多帧块）。仅适用于此 ECU。",
    "Bin": "Bin",
    "Pick a GM ECU flash readback (.bin). Sets it as this ECU's identity source (prompts before overwriting $1A DIDs) and flash bin source (Service $23 reads).": "选择 GM ECU 闪存读回 (.bin)。将其设置为此 ECU 的标识源（覆盖 $1A DID 前提示）和闪存 bin 源（服务 $23 读取）。",
    "Forget the selected bin: clears the flash source and the name shown. Already-loaded identity DIDs are kept.": "忘记所选 bin：清除闪存源和显示的名称。已加载的标识 DID 保留。",
    "Xfer ms": "传输(ms)",
    "Per-block delay (ms) before each $36 TransferData reply, to model real flash-program time. 0 = instant. ~30 ms over ~960 blocks gives a ~30 s write. Keep under ~2 s or the tester read-times-out.": "每个 $36 TransferData 回复前的每块延迟（ms），用于模拟真实闪存编程时间。0 = 即时。~960 块上约 30 ms 给出约 30 秒的写入。保持在约 2 秒以下，否则测试仪读取超时。",
    "Erase ms": "擦除(ms)",
    "Modelled erase time (ms). The erase positive response is simply deferred by this much - the ECU goes quiet then answers when done (no ResponsePending; PCMTec rejects a pending reply to the $B1 erase). 0 = instant.": "模拟擦除时间（ms）。擦除肯定响应只是延迟这么多 - ECU 静默然后在完成时回答（无 ResponsePending；PCMTec 拒绝 $B1 擦除的挂起回复）。0 = 即时。",
    "Resp ms": "响应(ms)",
    "Modelled processing time (ms) before every diagnostic response. 0 = instant. Above the active stack's P2 (GMW3110 150 ms / UDS 50 ms) the ECU emits 7F sid 78 ResponsePending heartbeats to P2* until it answers.": "每个诊断响应前的模拟处理时间（ms）。0 = 即时。超过活动栈的 P2（GMW3110 150 ms / UDS 50 ms）时，ECU 发出 7F sid 78 ResponsePending 心跳到 P2* 直到回答。",
    "Sess ms": "会话(ms)",
    "Override (ms) for the diagnostic session (P3C / S3) timeout. 0 = use the active stack's default (GMW3110 P3Cnom 5000 ms / UDS S3 5000 ms).": "诊断会话（P3C / S3）超时的覆盖（ms）。0 = 使用活动栈的默认值（GMW3110 P3Cnom 5000 ms / UDS S3 5000 ms）。",
    "Emit 78 ResponsePending when slow": "慢时发出 78 ResponsePending",
    "When ticked (default), a response slower than P2 is preceded by 7F sid 78 RequestCorrectlyReceived-ResponsePending to hold the tester open to P2*. Untick to model an ECU that just goes quiet then answers - some hosts reject a pending reply (e.g. PCMTec on the $B1 erase).": "勾选时（默认），慢于 P2 的响应前面会有 7F sid 78 RequestCorrectlyReceived-ResponsePending 以保持测试仪打开到 P2*。取消勾选以模拟一个静默然后回答的 ECU - 某些主机拒绝挂起回复（例如 PCMTec 对 $B1 擦除）。",
    "Answer RAM reads with zeros": "用零回答 RAM 读取",
    "When ticked, a $23 ReadMemoryByAddress for an address past the loaded bin's length (RAM) returns a positive response padded with 0x00 bytes instead of NRC $31 RequestOutOfRange. Applies to every persona; addresses inside the bin still read the real bytes.": "勾选时，超过已加载 bin 长度（RAM）的地址的 $23 ReadMemoryByAddress 返回填充 0x00 字节的肯定响应，而不是 NRC $31 RequestOutOfRange。适用于每个角色；bin 内的地址仍读取真实字节。",

    # PrimeWizard
    "DPS Prime Wizard": "DPS 加载向导",
    "Pick the DPS programming archive (.zip) you want the simulator to honour.": "选择您希望模拟器遵循的 DPS 编程存档 (.zip)。",
    "Browse for archive...": "浏览存档...",
    "Archive:": "存档：",
    "Utility file:": "实用文件：",
    "Calibration files:": "校准文件：",
    "OS Part Number:": "OS 部件号：",
    "(none)": "(无)",
    "DPS will perform these reads after the cal flash completes. The Value column is what the simulator will return for each read. Rows marked \"bytecode\" are pinned by the archive's $53 COMPARE_DATA literals - don't change them or DPS will reject the response. Fill empty rows manually, from a bin, or auto-populated.": "DPS 将在校准闪存完成后执行这些读取。值列是模拟器将为每次读取返回的内容。标记为\"字节码\"的行由存档的 $53 COMPARE_DATA 字面量固定 - 不要更改它们，否则 DPS 将拒绝响应。手动、从 bin 或自动填充空行。",
    "Load from bin...": "从 bin 加载...",
    "Walk a donor / sibling ECU bin and fill non-bytecode rows with the walker's authentic byte sequences.": "遍历供体/同级 ECU bin 并用遍历器的真实字节序列填充非字节码行。",
    "Auto-populate empty rows": "自动填充空行",
    "Fill every Empty row with a sensible default (right length, plausible shape for known DIDs).": "用合理的默认值填充每个空行（正确长度，已知 DID 的合理形状）。",
    "Step": "步骤",
    "Read": "读取",
    "Length": "长度",
    "Value (hex, editable)": "值（十六进制，可编辑）",
    "Review what will be applied. Click Apply Prime to register the ECU, or Back to make changes.": "查看将应用的内容。点击应用加载以注册 ECU，或返回进行更改。",
    "Loaded bin (if any):": "已加载 bin（如有）：",
    "Security module:": "安全模块：",
    "Fixed seed (10 hex):": "固定种子（10 位十六进制）：",
    "10 hex characters (5 bytes), e.g. 438930D306. Leave blank for a random seed each session.": "10 个十六进制字符（5 字节），例如 438930D306。留空则每次会话随机种子。",
    "leave blank → random per session": "留空 → 每次会话随机",
    "Phase 3 reads": "阶段 3 读取",
    "Total reads:": "总读取数：",
    "From $53 bytecode literal:": "来自 $53 字节码字面量：",
    "From bin walker:": "来自 bin 遍历器：",
    "From auto-populate default:": "来自自动填充默认值：",
    "From user override:": "来自用户覆盖：",
    "Empty (zero bytes):": "空（零字节）：",
    "(includes COMPARE_DATA reads - will fail Phase 3!)": "（包括 COMPARE_DATA 读取 - 将失败阶段 3！）",
    "< Back": "< 返回",
    "Apply Prime": "应用加载",

    # Misc
    "Version": "版本",
    "no ECU selected": "未选择 ECU",
    "parameter(s)": "个参数",
    "configured": "已配置",

    # MainWindow.xaml - titlebar pills and menu tooltips (round 3)
    "$27 SecurityAccess state of the selected ECU. Green = unlocked, amber = locked, red = lockout in effect. In DPS modes the pill stays visible before priming so the user knows where the indicator lives.": "$27 安全访问状态。绿色=已解锁，黄色=已锁定，红色=锁定中。",
    "Active connection status: J2534 shim registration (HKLM\\SOFTWARE\\PassThruSupport.04.04\\GmEcuSim) or the raw-CAN TCP gauge link.": "当前连接状态：J2534 shim 注册或原始 CAN TCP 仪表链接。",
    "Forget the persisted prime path. Does not remove ECUs from the current bus.": "清除已保存的加载路径。不会从当前总线移除 ECU。",
    "Writes HKLM\\SOFTWARE\\PassThruSupport.04.04\\GmEcuSim. Triggers a UAC prompt.": "写入 J2534 注册表项。触发 UAC 提示。",
    "Removes only the GmEcuSim entry; never touches other vendors. Triggers a UAC prompt.": "仅移除 GmEcuSim 项；不影响其他厂商。触发 UAC 提示。",
    "Lists every J2534 device on this machine in both registry views. Read-only - no UAC.": "列出本机两个注册表视图中的所有 J2534 设备。只读 - 无需 UAC。",
    "Stop and restart the \\\\.\\pipe\\GmEcuSim.PassThru server. Any connected host sees a broken pipe on its next call; new PassThruOpen calls succeed again. No UAC.": "停止并重启命名管道服务器。已连接的主机下次调用时会看到管道断开；新的 PassThruOpen 调用将再次成功。无需 UAC。",
    "When on, the simulator drives $3E TesterPresent on the host's behalf for any frame registered via PassThruStartPeriodicMsg. Off accepts the registration but skips the tick - delegating hosts have to keep their own P3C session alive.": "开启时，模拟器代表主机为通过 PassThruStartPeriodicMsg 注册的帧驱动 $3E TesterPresent。关闭时接受注册但跳过滴答。",
    "Open the ECU Editor. Add / remove / save / load ECUs from there too.": "打开 ECU 编辑器。也可以在那里添加/移除/保存/加载 ECU。",
    "Power-cycle every ECU: clears the programming session, security unlock, download buffer, and DPID schedules. Equivalent to $20 ReturnToNormalMode + full $27 re-lock applied to each ECU on the bus.": "对每个 ECU 断电重启：清除编程会话、安全解锁、下载缓冲区和 DPID 调度。",
    "Create a fresh ECU with synthetic defaults from EcuIdentitySeeder. This is the same thing the sidebar 'Add ECU' button now does for non-DPS modes; kept here for quick access. Import real identity from a flash readback via the editor's Advanced card ('Load identity from bin...').": "使用合成默认值创建新 ECU。与侧边栏'添加 ECU'按钮在非 DPS 模式下的操作相同。",
    "Stream every bus + diagnostic line to %LOCALAPPDATA%\\GmEcuSimulator\\logs\\bus logs\\bus_*.csv on a dedicated background thread. Independent of the 'Log traffic' textbox gate - use when high-volume downloads make the textbox impractical.": "在专用后台线程上将每条总线+诊断行流式传输到 CSV 文件。独立于'记录流量'文本框门控。",
    "Open the log directory in Explorer.": "在资源管理器中打开日志目录。",
    "When on, pipe-level J2534 calls (Open/Connect/StartMsgFilter/etc.) reach the file. The textbox / Download tab mirror is unaffected.": "开启时，管道级 J2534 调用写入文件。文本框/下载选项卡镜像不受影响。",
    "When on, every CAN frame (Rx/Tx, including HOST FILTERED) reaches the file. The textbox is unaffected.": "开启时，每个 CAN 帧（收发，包括主机过滤的）写入文件。文本框不受影响。",
    "Suffix every bus-frame line with a short human-readable tag (e.g. 'SecurityAccess - RequestSeed', 'ISO-TP FF (3110B) - TransferData @ 003FAFE0'). Tag is produced by UdsAnnotator from the raw bytes.": "为每条总线帧行添加简短的人类可读标签。标签由 UdsAnnotator 从原始字节生成。",
    "Condense long ISO-TP transfers in the bus log: first 3 + last 3 CFs visible, middle replaced with a single '-- bulk transfer collapsed: N frames hidden --' marker. FC/TesterPresent noise inside the collapsed window is suppressed too. Threshold is 10 CFs - shorter transfers log normally.": "压缩总线日志中的长 ISO-TP 传输：显示前3+后3个连续帧，中间替换为折叠标记。阈值为10个连续帧。",
    "Switch the colour palette. Drop your own .xaml palette into %APPDATA%\\GmEcuSimulator\\Palettes\\ to see it listed here.": "切换配色方案。将您自己的 .xaml 调色板放入 Palettes 目录即可在此列出。",
    "Show every protocol and service this app supports, and which ones it answers": "显示此应用支持的每个协议和服务，以及哪些有响应",
    "Show version and project details": "显示版本和项目详情",
    "Open the GitHub releases page in your browser": "在浏览器中打开 GitHub 发布页面",
    "Top-level mode and connection type. Switching clears the current ECU set and re-initialises the bus; you'll be offered a save first when leaving a persistable mode.": "顶层模式和连接类型。切换会清除当前 ECU 集合并重新初始化总线；离开可持久化模式时会先提示保存。",
    "Active Prime-From-Archive state. Set via the ECUs pane's Add button in DPS modes.": "当前从存档加载状态。在 DPS 模式下通过 ECU 面板的添加按钮设置。",
    "Master gate for the bus log AND the Download tab's mirror. Off by default - at $AA Fast 25Hz the bus frames alone can flood the textbox quickly.": "总线日志和下载选项卡镜像的主门控。默认关闭。",
    "File-log status. Toggle 'Log to file' under the Log menu to start/stop the sink.": "文件记录状态。在日志菜单下切换'记录到文件'以启动/停止接收器。",
    "Collapse the ECU/PID editor and fill the window with the workspace tabs. Shared with the Download tab's Maximize toggle.": "折叠 ECU/PID 编辑器并用工作区选项卡填满窗口。",
    "Hide TesterPresent keepalives ($3E requests and $7E positive responses) from this log window only. The file-log capture is not affected.": "仅在此日志窗口中隐藏 TesterPresent 保活。文件记录捕获不受影响。",
    "Hide the periodic DBC broadcast frames (0x12D, 0x207, ...) from this log window only. The file-log capture is not affected.": "仅在此日志窗口中隐藏周期性 DBC 广播帧。文件记录捕获不受影响。",
    "Drag to resize the CAN frames / J2534 calls panes. Double-click to reset to a 50/50 split.": "拖动以调整 CAN 帧/J2534 调用窗格大小。双击重置为 50/50 分割。",

    # EcuSetupWindow.xaml - remaining tooltips (round 3)
    "Re-open the DPS Prime Wizard with this ECU's prior selections pre-filled. Replaces the ECU atomically on Apply.": "使用此 ECU 之前的选择重新打开 DPS 加载向导。应用时原子替换 ECU。",
    "ISO 15765-2 FlowControl BlockSize (FC.BS). When this ECU receives a First Frame, it replies with FC = 30 BS 00. BS tells the tester how many Consecutive Frames to send before waiting for another FC: 0 = send all CFs in one burst (most permissive, works with most hosts); 1 = send 1 CF then wait for another FC, etc. Set BS=1 for the 6Speed.T43 tester - it sniffs the FC tail for the pattern '01' to recognise a T43 TCM and won't start the kernel upload otherwise.": "ISO 15765-2 流控块大小 (FC.BS)。BS 告诉测试仪在等待另一个 FC 之前发送多少个连续帧。6Speed.T43 测试仪设置 BS=1。",
    "GMW3110 8-bit diagnostic address. For SPS type C this is the byte that derives SPS_PrimeReq ($000|addr) and SPS_PrimeRsp ($300|addr). e.g. $11 -> req $011, resp $311. Informational for type A/B (their CAN IDs are explicit).": "GMW3110 8位诊断地址。对于 SPS C 类型，这是派生 SPS_PrimeReq 和 SPS_PrimeRsp 的字节。",
    "Seed/key algorithm picked for this primed ECU. In DPS modes the Prime Wizard owns this choice - re-open the wizard (Edit primed) to change it.": "为此加载的 ECU 选择的种子/密钥算法。在 DPS 模式下，加载向导拥有此选择。",
    "The engine character driving this ECU's derived signals (induction, airflow, fuelling). Naturally Aspirated keeps MAP in vacuum (fuel pressure tops out at the 4 bar base); Boosted V8 drives MAP above barometric under load, so MAF and fuel pressure rise above base. Swaps live - a connected tool sees the change on the next read.": "驱动此 ECU 派生信号的引擎特性。自然吸气保持 MAP 在真空；增压 V8 在负载下驱动 MAP 高于大气压。实时交换。",
    "Operating point driving this ECU's live signals. Switching it ramps RPM, load, MAP, MAF, O2 etc. toward the new state - a connected tool sees Mode $01 and any signal-backed $22 PIDs move.": "驱动此 ECU 实时信号的工作点。切换会使 RPM、负载、MAP 等向新状态渐变。",
    "Save this ECU's broadcast set to a *.dbc.json file.": "将此 ECU 的广播集保存到 *.dbc.json 文件。",
    "Load a *.dbc.json file and replace this ECU's broadcast set.": "加载 *.dbc.json 文件并替换此 ECU 的广播集。",
    "Show / hide this message's signals": "显示/隐藏此消息的信号",
    "Where this field's value comes from: (none)=0, Constant=the fixed value, or a live engine signal.": "此字段值的来源：(无)=0，常量=固定值，或实时引擎信号。",
    "Save this ECU's $1A/$22/$2D PID list (including waveform settings) to a .pids.json file. Does not include ECU settings or CAN broadcasts - use the sidebar's Save ECU for the whole ECU.": "将此 ECU 的 $1A/$22/$2D PID 列表保存到 .pids.json 文件。不包括 ECU 设置或 CAN 广播。",
}

# Attributes that contain user-visible static strings
VISIBLE_ATTRS = ['Header', 'Content', 'Text', 'Title', 'ToolTip', 'FallbackValue']

def is_binding(value):
    """Check if a value is a binding expression or contains one."""
    return value.strip().startswith('{') and value.strip().endswith('}')

def has_binding(value):
    """Check if value contains any binding expression."""
    return '{Binding' in value or '{x:Static' in value or '{DynamicResource' in value or '{StaticResource' in value

def translate_string(value):
    """Translate a static string value. Returns translated string or None if no translation."""
    if not value or not value.strip():
        return None
    if is_binding(value):
        return None
    if has_binding(value):
        # Mixed content like FallbackValue=GM ECU Simulator inside binding
        # Handle FallbackValue specifically
        if 'FallbackValue=' in value:
            for eng, chn in TRANSLATIONS.items():
                if f'FallbackValue={eng}' in value:
                    return value.replace(f'FallbackValue={eng}', f'FallbackValue={chn}')
        return None

    # Direct lookup
    if value in TRANSLATIONS:
        return TRANSLATIONS[value]

    # Try with stripped whitespace
    stripped = value.strip()
    if stripped in TRANSLATIONS:
        return value.replace(stripped, TRANSLATIONS[stripped])

    return None

def translate_xaml_file(filepath):
    """Translate static UI strings in a XAML file."""
    with open(filepath, 'r', encoding='utf-8') as f:
        content = f.read()

    original = content
    changes = []

    # Pattern: Attribute="value" where Attribute is in VISIBLE_ATTRS
    # We need to be careful to only match static values, not bindings
    for attr in VISIBLE_ATTRS:
        # Match attr="value" - value must not start with {
        pattern = rf'({attr}=")([^"{{]*?)(["\'])'
        def replacer(m):
            prefix = m.group(1)
            value = m.group(2)
            suffix = m.group(3)
            translated = translate_string(value)
            if translated and translated != value:
                changes.append(f'{attr}: "{value}" -> "{translated}"')
                return f'{prefix}{translated}{suffix}'
            return m.group(0)
        content = re.sub(pattern, replacer, content)

    # Also handle single-quoted attributes
    for attr in VISIBLE_ATTRS:
        pattern = rf"({attr}=')([^'{{]*?)(')"
        def replacer2(m):
            prefix = m.group(1)
            value = m.group(2)
            suffix = m.group(3)
            translated = translate_string(value)
            if translated and translated != value:
                changes.append(f'{attr}: \'{value}\' -> \'{translated}\'')
                return f'{prefix}{translated}{suffix}'
            return m.group(0)
        content = re.sub(pattern, replacer2, content)

    if content != original:
        with open(filepath, 'w', encoding='utf-8') as f:
            f.write(content)
        return changes
    return []

def main():
    base_dir = sys.argv[1] if len(sys.argv) > 1 else '.'
    xaml_files = []
    for root, dirs, files in os.walk(base_dir):
        for f in files:
            if f.endswith('.xaml'):
                xaml_files.append(os.path.join(root, f))

    total_changes = 0
    for filepath in sorted(xaml_files):
        changes = translate_xaml_file(filepath)
        if changes:
            print(f'\n=== {filepath} ({len(changes)} changes) ===')
            for c in changes:
                print(f'  {c}')
            total_changes += len(changes)

    print(f'\n=== Total: {total_changes} strings translated across {len(xaml_files)} files ===')

if __name__ == '__main__':
    main()

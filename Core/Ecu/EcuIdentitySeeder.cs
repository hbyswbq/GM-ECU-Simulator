using Common.Protocol;

namespace Core.Ecu;

// Gives a freshly-created EcuSimulator ECU a baseline identity so a tester can query it the instant it appears on the
// bus. Without this a brand-new ECU NRC-$31s every $1A read and looks dead to discovery tools that probe for a VIN.
//
// Each seeded DID is materialised as a Mode1A StaticBytes Pid row rather than an entry in EcuNode's identifier
// dictionary. That choice buys three things for free: the row persists through the v15 PidDto/StaticBytes path in
// ecu_simulator.mode.json with no schema change, it shows up in the editor PID grid as a visible, editable row, and
// it is the same row Service3BHandler's write-through updates so a tester's $3B VIN write survives a save/load.
//
// Call this only on genuine new-ECU creation in EcuSimulator mode - the AddEcu button and the first-launch
// DefaultEcuConfig. DPS-primed ECUs derive their identity from the archive / donor bin and must never be seeded with
// these synthetic placeholders; the IsPrimed guard below enforces that even if a future caller forgets the rule.
public static class EcuIdentitySeeder
{
    // The DIDs a brand-new ECU is born with - a curated E38/E67-realistic $1A identity set so a blank ECU presents the
    // same kind of identity block a real Gen-IV/V GM controller answers, not just a lone VIN. Every entry must have a
    // DefaultDidValues.Get placeholder (Seed skips any that return null). Membership mirrors what a stock E38/E67
    // readback surfaces (VIN + partial VIN, supplier/system ids, programming date, ECU config, enable counter, diag
    // address, traceability + broadcast code, the operating-software / model part numbers, and the boot + cal-1 SW
    // alpha codes); grow it as needed.
    public static readonly byte[] SeededDids =
    {
        0x90,  // VIN
        0x28,  // Partial VIN (last 6 of VIN)
        0x92,  // System Supplier ID
        0x97,  // System Name / Engine Type
        0x98,  // Repair Shop Code / SN
        0x99,  // Programming Date
        0x9B,  // ECU Configuration / Coding
        0xA0,  // Manufacturers Enable Counter
        0xB0,  // Diagnostic Address
        0xB4,  // Mfg Traceability Chars
        0xB5,  // Broadcast Code
        0xC0,  // Operating Software ID
        0xC1,  // End Model Part Number
        0xC2,  // Base Model Part Number
        0xC9,  // MCP Processor Identifier 01
        0xCA,  // MCP Processor Identifier 02
        0xCB,  // End Model Number
        0xCC,  // Base Model Number
        0xD0,  // Boot SW Alpha Code
        0xD1,  // SW Alpha Code 1
    };

    // Pre-localisation English DID labels mapped to their Chinese equivalents.
    // Applied to existing Mode1A rows on seed so configs saved before localisation
    // pick up the translated labels; user-edited names are untouched.
    private static readonly Dictionary<string, string> LegacyNameMap = new()
    {
        ["Partial VIN"] = "部分VIN",
        ["VIN"] = "VIN（车辆识别号）",
        ["System Supplier ID"] = "系统供应商ID",
        ["Supplier SW Version Number"] = "供应商软件版本号",
        ["System Name / Engine Type"] = "系统名称 / 发动机类型",
        ["Repair Shop Code / SN"] = "维修站代码 / 序列号",
        ["Programming Date"] = "编程日期",
        ["Diagnostic Data Identifier"] = "诊断数据标识符",
        ["ECU Configuration / Coding"] = "ECU配置 / 编码",
        ["History: RSCOSN"] = "历史：RSCOSN",
        ["Manufacturers Enable Counter"] = "制造商启用计数器",
        ["Diagnostic Address"] = "诊断地址",
        ["Mfg Traceability Chars"] = "制造商可追溯字符",
        ["Broadcast Code"] = "广播代码",
        ["Operating Software ID"] = "运行软件ID",
        ["End Model Part Number"] = "最终型号零件号",
        ["Base Model Part Number"] = "基础型号零件号",
        ["Operating SW P/N (alt)"] = "运行软件零件号（备用）",
        ["Calibration ID 1"] = "标定ID 1",
        ["Calibration ID 2"] = "标定ID 2",
        ["Calibration ID 3"] = "标定ID 3",
        ["Calibration ID 4"] = "标定ID 4",
        ["Calibration ID 5"] = "标定ID 5",
        ["MCP Processor Identifier 01"] = "MCP处理器标识符01",
        ["MCP Processor Identifier 02"] = "MCP处理器标识符02",
        ["End Model Number"] = "最终型号",
        ["Base Model Number"] = "基础型号",
        ["ECU Hardware P/N"] = "ECU硬件零件号",
        ["ECU Hardware Version"] = "ECU硬件版本",
        ["Boot SW Alpha Code"] = "引导软件Alpha代码",
        ["SW Alpha Code 1"] = "软件Alpha代码1",
        ["SW Alpha Code 2"] = "软件Alpha代码2",
        ["SW Alpha Code 3"] = "软件Alpha代码3",
        ["SW Alpha Code 4"] = "软件Alpha代码4",
        ["SW Alpha Code 5"] = "软件Alpha代码5",
        ["SW Alpha Code 6"] = "软件Alpha代码6",
        ["SW Alpha Code 7"] = "软件Alpha代码7",
        ["SW Alpha Code 8"] = "软件Alpha代码8",
        ["SW Alpha Code 9"] = "软件Alpha代码9",
        ["SW Alpha Code 10"] = "软件Alpha代码10",
        ["Software Module Identifier"] = "软件模块标识符",
        ["ECU Specific Data 1"] = "ECU专用数据1",
        ["ECU Specific Data 2"] = "ECU专用数据2",
        ["ECU Specific Data 3"] = "ECU专用数据3",
        ["ECU Specific Data 4"] = "ECU专用数据4",
    };

    // Seeds every entry in SeededDids the ECU does not already carry. Existing Mode1A rows are left untouched, so this
    // is precedence-safe against a loaded config and is a no-op on a re-seed. Existing rows whose Name matches a
    // pre-localisation English label are migrated to the Chinese equivalent (user-edited names are untouched).
    public static void Seed(EcuNode node)
    {
        // Primed ECUs own their identity from the archive - never overwrite it with a synthetic placeholder.
        if (node.IsPrimed) return;

        // Migrate pre-localisation English names on existing Mode1A rows.
        foreach (var pid in node.AllPids.Where(p => p.Mode == PidMode.Mode1A))
        {
            if (LegacyNameMap.TryGetValue(pid.Name, out var zh) && pid.Name != zh)
                pid.Name = zh;
        }

        foreach (var did in SeededDids)
        {
            // A DID already present (loaded-config row, an earlier seed) wins over the synthetic default.
            if (node.GetMode1APid(did) != null) continue;

            var value = DefaultDidValues.Get(did);
            if (value is null || value.Length == 0) continue;

            // Size is informational once LengthBytes is set; DWord matches how PidCatalogue tags any >4-byte row.
            node.AddPid(new Pid
            {
                Mode        = PidMode.Mode1A,
                Address     = did,
                Name        = Gmw3110DidNames.NameOf(did) ?? $"DID {did:X2}",
                LengthBytes = value.Length,
                StaticBytes = value,
                Size        = PidSize.DWord,
                DataType    = PidDataType.Unsigned,
            });
        }
    }
}

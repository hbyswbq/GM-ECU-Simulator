namespace Common.Protocol;

/// <summary>
/// Well-known $1A ReadDataByIdentifier DID labels per GMW3110 §8.3.2 Table 25
/// and Appendix A (the SPS / DPS Get-Controller-Info dialog field set). Used by
/// the editor's Identifiers grid to label rows so the user can see which DID
/// is which without cross-referencing the spec. Unknown DIDs return null and
/// the grid shows just the hex byte.
///
/// <see cref="KnownDids"/> is the full enumerable set in the order the
/// editor's Identifiers grid renders them. Keep it sorted by DID byte so
/// the table is predictable; add new entries to <see cref="NameOf"/> too.
/// </summary>
public static class Gmw3110DidNames
{
    /// <summary>
    /// Every DID the editor pre-populates in the Identifiers grid. Matches the
    /// set DPS Get-Controller-Info reads, plus a few neighbouring identifiers
    /// (DTC counts, hardware part numbers) that other GM testers ask for.
    /// </summary>
    public static readonly byte[] KnownDids = new byte[]
    {
        0x28,
        0x90,
        0x92, 0x95, 0x97, 0x98, 0x99, 0x9A, 0x9B, 0x9F,
        0xA0, 0xB0, 0xB4, 0xB5,
        0xC0, 0xC1, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xCB, 0xCC, 0xCD, 0xCE,
        0xD0, 0xD1, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA,
        0xDD,
        0xF1, 0xF2, 0xF3, 0xF4,
    };

    public static string? NameOf(byte did) => did switch
    {
        0x28 => "部分VIN",
        0x90 => "VIN（车辆识别号）",
        0x92 => "系统供应商ID",
        0x95 => "供应商软件版本号",
        0x97 => "系统名称 / 发动机类型",
        0x98 => "维修站代码 / 序列号",
        0x99 => "编程日期",
        0x9A => "诊断数据标识符",
        0x9B => "ECU配置 / 编码",
        0x9F => "历史：RSCOSN",
        0xA0 => "制造商启用计数器",
        0xB0 => "诊断地址",
        0xB4 => "制造商可追溯字符",
        0xB5 => "广播代码",
        0xC0 => "运行软件ID",
        0xC1 => "最终型号零件号",
        0xC2 => "基础型号零件号",
        0xC3 => "运行软件零件号（备用）",
        0xC4 => "标定ID 1",
        0xC5 => "标定ID 2",
        0xC6 => "标定ID 3",
        0xC7 => "标定ID 4",
        0xC8 => "标定ID 5",
        0xC9 => "MCP处理器标识符01",
        0xCA => "MCP处理器标识符02",
        0xCB => "最终型号",
        0xCC => "基础型号",
        0xCD => "ECU硬件零件号",
        0xCE => "ECU硬件版本",
        // $D0 = boot SW alpha code; $D1..$DA = 2-char design-level suffix
        // (Alpha Code) for the SWMIs at $C1..$CA. GMW3110-2010 §8.3.2.
        0xD0 => "引导软件Alpha代码",
        0xD1 => "软件Alpha代码1",
        0xD2 => "软件Alpha代码2",
        0xD3 => "软件Alpha代码3",
        0xD4 => "软件Alpha代码4",
        0xD5 => "软件Alpha代码5",
        0xD6 => "软件Alpha代码6",
        0xD7 => "软件Alpha代码7",
        0xD8 => "软件Alpha代码8",
        0xD9 => "软件Alpha代码9",
        0xDA => "软件Alpha代码10",
        0xDD => "软件模块标识符",
        0xF1 => "ECU专用数据1",
        0xF2 => "ECU专用数据2",
        0xF3 => "ECU专用数据3",
        0xF4 => "ECU专用数据4",
        _ => null,
    };
}

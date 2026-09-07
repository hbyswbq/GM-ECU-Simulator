namespace Common.Protocol;

// PID engineering data types - controls how raw bytes are interpreted/encoded
// when the simulator returns a sample (big-endian on the wire per GMLAN).
public enum PidDataType
{
    Bool,
    Unsigned,
    Signed,
    Hex,
    Ascii,
}

/// <summary>Chinese display names for PidDataType, used by the editor's Type ComboBox.</summary>
public static class PidDataTypeDisplay
{
    public static readonly IReadOnlyDictionary<PidDataType, string> Names = new Dictionary<PidDataType, string>
    {
        [PidDataType.Bool]     = "布尔",
        [PidDataType.Unsigned] = "无符号",
        [PidDataType.Signed]   = "有符号",
        [PidDataType.Hex]      = "十六进制",
        [PidDataType.Ascii]    = "文本",
    };

    public static string Get(PidDataType t) => Names.TryGetValue(t, out var s) ? s : t.ToString();
    public static IEnumerable<KeyValuePair<PidDataType, string>> All => Names;
}

public enum PidSize : byte
{
    Byte = 1,
    Word = 2,
    DWord = 4,
}

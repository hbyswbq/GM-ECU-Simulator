namespace Common.Waveforms;

/// <summary>
/// Chinese display names for WaveformShape enum values.
/// Key is the enum value, Value is the localized display string.
/// </summary>
public static class WaveformShapeDisplay
{
    public static readonly IReadOnlyList<KeyValuePair<WaveformShape, string>> All = new[]
    {
        new KeyValuePair<WaveformShape, string>(WaveformShape.Sin, "正弦波"),
        new KeyValuePair<WaveformShape, string>(WaveformShape.Triangle, "三角波"),
        new KeyValuePair<WaveformShape, string>(WaveformShape.Square, "方波"),
        new KeyValuePair<WaveformShape, string>(WaveformShape.Sawtooth, "锯齿波"),
        new KeyValuePair<WaveformShape, string>(WaveformShape.CsvFile, "CSV 文件"),
        new KeyValuePair<WaveformShape, string>(WaveformShape.Constant, "常量"),
    };

    public static string For(WaveformShape shape) =>
        All.FirstOrDefault(kvp => kvp.Key == shape).Value ?? shape.ToString();
}

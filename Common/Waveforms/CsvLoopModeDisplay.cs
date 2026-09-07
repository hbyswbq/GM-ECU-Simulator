namespace Common.Waveforms;

/// <summary>
/// Chinese display names for CsvLoopMode enum values.
/// </summary>
public static class CsvLoopModeDisplay
{
    public static readonly IReadOnlyList<KeyValuePair<CsvLoopMode, string>> All = new[]
    {
        new KeyValuePair<CsvLoopMode, string>(CsvLoopMode.HoldLast, "保持最后值"),
        new KeyValuePair<CsvLoopMode, string>(CsvLoopMode.Loop, "循环"),
        new KeyValuePair<CsvLoopMode, string>(CsvLoopMode.Stop, "停止"),
    };

    public static string For(CsvLoopMode mode) =>
        All.FirstOrDefault(kvp => kvp.Key == mode).Value ?? mode.ToString();
}

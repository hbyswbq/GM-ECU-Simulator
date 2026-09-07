namespace Common.Replay;

/// <summary>
/// Chinese display names for BinReplayLoopMode enum values.
/// </summary>
public static class BinReplayLoopModeDisplay
{
    public static readonly IReadOnlyList<KeyValuePair<BinReplayLoopMode, string>> All = new[]
    {
        new KeyValuePair<BinReplayLoopMode, string>(BinReplayLoopMode.HoldLast, "保持最后值"),
        new KeyValuePair<BinReplayLoopMode, string>(BinReplayLoopMode.Loop, "循环"),
        new KeyValuePair<BinReplayLoopMode, string>(BinReplayLoopMode.Stop, "停止"),
    };

    public static string For(BinReplayLoopMode mode) =>
        All.FirstOrDefault(kvp => kvp.Key == mode).Value ?? mode.ToString();
}

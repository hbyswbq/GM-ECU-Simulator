namespace Common.Signals;

/// <summary>
/// Chinese display names for ScenarioId enum values.
/// </summary>
public static class ScenarioDisplay
{
    public static readonly IReadOnlyList<KeyValuePair<ScenarioId, string>> All = new[]
    {
        new KeyValuePair<ScenarioId, string>(ScenarioId.KeyOnEngineOff, "通电未启动"),
        new KeyValuePair<ScenarioId, string>(ScenarioId.Idle, "怠速"),
        new KeyValuePair<ScenarioId, string>(ScenarioId.Cruise, "轻载巡航"),
        new KeyValuePair<ScenarioId, string>(ScenarioId.AccelDecelSweep, "加速/减速扫描"),
    };

    public static string For(ScenarioId id) =>
        All.FirstOrDefault(kvp => kvp.Key == id).Value ?? id.ToString();
}

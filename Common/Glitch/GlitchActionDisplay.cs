namespace Common.Glitch;

/// <summary>
/// Chinese display names for GlitchAction enum values.
/// </summary>
public static class GlitchActionDisplay
{
    public static readonly IReadOnlyList<KeyValuePair<GlitchAction, string>> All = new[]
    {
        new KeyValuePair<GlitchAction, string>(GlitchAction.None, "无"),
        new KeyValuePair<GlitchAction, string>(GlitchAction.EmitNrc, "发送NRC"),
        new KeyValuePair<GlitchAction, string>(GlitchAction.Drop, "丢弃请求"),
        new KeyValuePair<GlitchAction, string>(GlitchAction.CorruptByte, "损坏字节"),
        new KeyValuePair<GlitchAction, string>(GlitchAction.Random, "随机"),
    };

    public static string For(GlitchAction action) =>
        All.FirstOrDefault(kvp => kvp.Key == action).Value ?? action.ToString();
}

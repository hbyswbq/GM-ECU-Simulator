namespace Common.Signals;

// The operating points the engine model can be driven to. The steady points (KOEO/Idle/Cruise) each set the four
// primary signals to a fixed target; AccelDecelSweep is the one dynamic point - a self-looping rev pull whose
// primaries are continuous functions of time (see EngineModel.SweepPrimary). Warm-only for now - there is no
// cold-start point because the thermal axis is deferred.
public enum ScenarioId
{
    KeyOnEngineOff,
    Idle,
    Cruise,
    AccelDecelSweep,
}

// A scenario's primary targets. The engine model eases the live primaries toward these on each signal's own time
// constant; it reads nothing but these four off a scenario. For AccelDecelSweep these are only the bottom-of-cycle
// (off-idle) values - the live sweep is driven by SweepProfile, not by easing toward a constant.
public sealed record Scenario(
    ScenarioId Id,
    string Name,
    double EngineRpm,
    double VehicleSpeed,
    double ThrottlePosition,
    double EngineLoad);

// The tunable shape of the AccelDecelSweep scenario: a continuous rev pull from off-idle up to the rev limiter,
// a brief bounce off the limiter, then a lifted-throttle coast back down to a standstill - looping forever. Times
// are milliseconds on the bus clock. AccelTimeMs / LimiterHoldMs / DecelTimeMs / CrossfadeMs are the knobs the editor
// exposes; the rev band and throttle/load character of each leg have sensible warm-V8 defaults but round-trip through
// config if ever set. The three legs run back-to-back: climb (AccelTimeMs) -> limiter (LimiterHoldMs) -> coast
// (DecelTimeMs).
public sealed record SweepProfile(
    double AccelTimeMs = 8000,
    double DecelTimeMs = 5000)
{
    public static SweepProfile Default { get; } = new();

    // How long the engine sits bouncing off the rev limiter at the top of the pull (foot still flat).
    public double LimiterHoldMs { get; init; } = 2000;

    // Entry cross-fade: on switching into the sweep from another operating point, the primaries blend from whatever
    // value they were at toward the sweep's own value over this long, so the pull eases in instead of teleporting to
    // the off-idle floor. Only affects the first CrossfadeMs of a sweep; the loop itself is untouched. 0 -> no fade.
    public double CrossfadeMs { get; init; } = 500;

    // Rev band the pull sweeps between. RpmHigh is the limiter; the bounce is layered on top of it during the hold.
    public double RpmLow { get; init; } = 800;
    public double RpmHigh { get; init; } = 7000;

    // Rev-limiter texture: a fast fuel/spark-cut stutter around RpmHigh. +/- LimiterBounceRpm at LimiterBounceHz.
    public double LimiterBounceRpm { get; init; } = 25;
    public double LimiterBounceHz { get; init; } = 12;

    // Road speed tracks the rev pull: climbs with rpm on the way up, coasts back to a standstill on the way down.
    public double SpeedLow { get; init; } = 0;
    public double SpeedHigh { get; init; } = 120;

    // Throttle/load character of each leg. Climb opens the throttle off idle to wide-open and loads up; the coast is
    // a lifted-throttle overrun (throttle shut, load near zero -> the derivations read it as decel fuel cutoff).
    public double ThrottleIdle { get; init; } = 4;
    public double ThrottleOpen { get; init; } = 92;
    public double LoadIdle { get; init; } = 22;
    public double LoadHigh { get; init; } = 95;
    public double LoadOverrun { get; init; } = 3;

    // One full loop: climb + limiter hold + coast. The sweep repeats with this period.
    public double PeriodMs => AccelTimeMs + LimiterHoldMs + DecelTimeMs;
}

public static class ScenarioCatalogue
{
    // Hand-tuned operating points for a warm gas V8. KOEO is engine-off (zero rpm/airflow, throttle shut); Idle and
    // Cruise are steady running points. AccelDecelSweep's tuple is only its off-idle floor (where every loop starts
    // and ends) - the live values come from SweepProfile, so these numbers just give a sane boot/exit value.
    private static readonly Scenario[] AllScenarios =
    {
        new(ScenarioId.KeyOnEngineOff,  "通电未启动", EngineRpm:    0, VehicleSpeed:   0, ThrottlePosition:  0, EngineLoad:  0),
        new(ScenarioId.Idle,            "怠速",               EngineRpm:  750, VehicleSpeed:   0, ThrottlePosition:  0, EngineLoad: 22),
        new(ScenarioId.Cruise,          "轻载巡航",       EngineRpm: 2000, VehicleSpeed: 100, ThrottlePosition: 18, EngineLoad: 38),
        new(ScenarioId.AccelDecelSweep, "加速/减速扫描",EngineRpm:  800, VehicleSpeed:   0, ThrottlePosition:  4, EngineLoad: 22),
    };

    private static readonly IReadOnlyDictionary<ScenarioId, Scenario> ByIdMap = AllScenarios.ToDictionary(s => s.Id);

    public static Scenario Get(ScenarioId id) => ByIdMap[id];

    public static IReadOnlyList<Scenario> All => AllScenarios;
}

namespace Common.Signals;

// Static description of a signal: how it reads to a human and the plausible engineering range it lives in. The range
// is advisory - it bounds editor sliders and clamps egregious overrides - but the EngineModel's own derivation is
// what normally keeps values sane. Wire scaling deliberately does NOT live here: each mode owns its own encoding
// (J1979 formulas for $01, A2L scalars for $22), so one signal can be dressed differently per mode.
public sealed record SignalDef(SignalId Id, string Name, string Unit, double Min, double Max);

// The built-in signal catalogue. The set is fixed in code because the engine model is universal gas-V8 physics, not
// per-ECU config; callers can therefore rely on every SignalId having exactly one entry here.
public static class SignalCatalogue
{
    private static readonly SignalDef[] AllDefs =
    {
        new(SignalId.EngineRpm,                  "发动机转速",                  "rpm",    0, 8000),
        new(SignalId.VehicleSpeed,               "车速",               "km/h",   0, 255),
        new(SignalId.ThrottlePosition,           "节气门位置",           "%",      0, 100),
        new(SignalId.EngineLoad,                 "发动机负荷",                 "%",      0, 100),
        new(SignalId.ManifoldAbsolutePressure,   "进气歧管绝对压力",  "kPa",    0, 255),
        new(SignalId.MassAirFlow,                "空气质量流量",               "g/s",    0, 655),
        new(SignalId.EngineTorque,               "发动机扭矩",               "Nm",  -120, 2000),
        new(SignalId.FuelPressure,               "燃油压力",               "kPa",    0, 765),
        new(SignalId.TimingAdvance,              "点火提前角",              "deg",  -64, 64),
        new(SignalId.ControlModuleVoltage,       "控制模块电压",      "V",      0, 18),
        new(SignalId.ShortTermFuelTrimBank1,     "短期燃油修正B1",     "%",   -100, 100),
        new(SignalId.LongTermFuelTrimBank1,      "长期燃油修正B1",      "%",   -100, 100),
        new(SignalId.ShortTermFuelTrimBank2,     "短期燃油修正B2",     "%",   -100, 100),
        new(SignalId.LongTermFuelTrimBank2,      "长期燃油修正B2",      "%",   -100, 100),
        new(SignalId.O2VoltageBank1Sensor1,      "氧传感器电压B1S1",             "V",      0, 1.275),
        new(SignalId.O2VoltageBank1Sensor2,      "氧传感器电压B1S2",             "V",      0, 1.275),
        new(SignalId.O2VoltageBank2Sensor1,      "氧传感器电压B2S1",             "V",      0, 1.275),
        new(SignalId.O2VoltageBank2Sensor2,      "氧传感器电压B2S2",             "V",      0, 1.275),
        new(SignalId.CommandedEquivalenceRatio,  "指令当量比", "lambda", 0, 2),
        new(SignalId.AcceleratorPedalPosition,   "加速踏板位置",  "%",      0, 100),
        new(SignalId.CoolantTemp,                "发动机冷却液温度",         "degC", -40, 215),
        new(SignalId.IntakeAirTemp,              "进气温度",             "degC", -40, 215),
        new(SignalId.BarometricPressure,         "大气压力",         "kPa",    0, 255),
        new(SignalId.AmbientAirTemp,             "环境空气温度",            "degC", -40, 215),
        new(SignalId.FuelLevel,                  "燃油液位",                  "%",      0, 100),
        new(SignalId.EngineOilTemp,              "发动机机油温度",             "degC", -40, 215),
    };

    private static readonly IReadOnlyDictionary<SignalId, SignalDef> ByIdMap = AllDefs.ToDictionary(d => d.Id);

    public static SignalDef Get(SignalId id) => ByIdMap[id];

    public static IReadOnlyList<SignalDef> All => AllDefs;
}

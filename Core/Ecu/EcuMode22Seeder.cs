using Common.Protocol;
using Common.Signals;

namespace Core.Ecu;

// Gives a freshly-created EcuSimulator ECU a curated set of live $22 (ReadDataByIdentifier, 2-byte DID) PIDs so its
// "$22" editor section isn't empty out of the box and a tester sees scenario-correlated values immediately.
//
// Each row uses a REAL GM DID number + A2L linear scaling pulled from the embedded E38/E67 A2L library, and is backed
// by a signal from the shared engine model - so the value moves with the active scenario (and idle dither) exactly
// like the $01 projection, just dressed in GM A2L scaling instead of the legislated J1979 formula. The wire byte
// width is chosen to comfortably hold the scaled raw (the A2L's own element-size field is the internal fixed-point
// width, not the $22 response length, so it isn't trustworthy here).
//
// Only quantities with a clean integer-encoded DID in the A2L are included; MAF and vehicle speed are intentionally
// omitted (the A2L exposes those only as FLOAT32 / via driver-state bytes, which the integer ValueCodec can't dress
// faithfully). A donor bin / DPS archive still replaces this with the vehicle's real $22 map when one is supplied.
//
// Entry points: the Add ECU button, the first-launch DefaultEcuConfig, AND a re-seed pass after each EcuSimulator
// config load (DefaultEcuConfig.SeedDefaults) so a config saved before this set existed still backfills the set on the
// next launch. Primed ECUs own their $22 set from the archive and are skipped via the IsPrimed guard. Existing rows at
// a DID are never overwritten, so it's precedence-safe against a loaded config and a no-op on re-seed.
public static class EcuMode22Seeder
{
    // DID + signal + A2L scaling (phys = Scalar*raw + Offset) + wire width. DID numbers and slopes are verbatim from
    // the embedded A2L $22 library; widths are sized to fit the scaled raw.
    public readonly record struct Mode22Seed(
        ushort Did, SignalId Signal, string Name, double Scalar, double Offset, int Bytes, PidDataType Type);

    public static readonly Mode22Seed[] Seeds =
    {
        new(0x1421, SignalId.EngineRpm,                  "发动机转速",                    0.125,       0, 2, PidDataType.Unsigned),
        new(0x0005, SignalId.CoolantTemp,                "发动机冷却液温度",    0.0078125,   0, 2, PidDataType.Signed),
        new(0x000A, SignalId.FuelPressure,               "估算燃油轨压力",  0.03125,     0, 2, PidDataType.Unsigned),
        new(0x000B, SignalId.ManifoldAbsolutePressure,   "进气歧管绝对压力",  0.00390625,  0, 2, PidDataType.Unsigned),
        new(0x000F, SignalId.IntakeAirTemp,              "进气温度",        0.0078125,   0, 2, PidDataType.Signed),
        new(0x000E, SignalId.TimingAdvance,              "点火提前角",                 0.0078125,   0, 2, PidDataType.Signed),
        new(0x004C, SignalId.ThrottlePosition,           "节气门位置",             0.00152588,  0, 2, PidDataType.Unsigned),
        new(0x0042, SignalId.ControlModuleVoltage,       "运行/启动电压",             0.000976562, 0, 2, PidDataType.Signed),
        new(0x0044, SignalId.CommandedEquivalenceRatio,  "指令当量比",   0.000976562, 0, 2, PidDataType.Unsigned),
        new(0x0046, SignalId.AmbientAirTemp,             "估算环境空气温度",    0.0078125,   0, 2, PidDataType.Signed),
        new(0x002F, SignalId.FuelLevel,                  "燃油箱液位",               0.00305176,  0, 2, PidDataType.Unsigned),
        new(0x0049, SignalId.AcceleratorPedalPosition,   "加速踏板位置 D",  0.00152588,  0, 2, PidDataType.Unsigned),
        new(0x004A, SignalId.AcceleratorPedalPosition,   "加速踏板位置 E",  0.00152588,  0, 2, PidDataType.Unsigned),
    };

    // Known English names from prior seed runs, mapped to their Chinese localisations.
    // Applied to existing Mode22 rows on seed so configs saved before localisation
    // pick up the translated labels without losing user-edited rows.
    private static readonly Dictionary<string, string> LegacyNameMap = new()
    {
        ["Engine RPM"]                    = "发动机转速",
        ["Engine coolant temperature"]    = "发动机冷却液温度",
        ["Estimated fuel rail pressure"]  = "估算燃油轨压力",
        ["Intake manifold abs pressure"]  = "进气歧管绝对压力",
        ["Intake air temperature"]        = "进气温度",
        ["Spark advance"]                 = "点火提前角",
        ["Throttle position"]             = "节气门位置",
        ["Run/crank voltage"]             = "运行/启动电压",
        ["Commanded equivalence ratio"]   = "指令当量比",
        ["Estimated ambient air temp"]    = "估算环境空气温度",
        ["Fuel tank level"]               = "燃油箱液位",
        ["Accelerator pedal position D"]  = "加速踏板位置 D",
        ["Accelerator pedal position E"]  = "加速踏板位置 E",
    };

    // Adds every seed DID the ECU does not already carry as a Mode22 row. Existing rows win (loaded config / prior
    // seed); primed ECUs are skipped entirely. Existing rows whose Name matches a pre-localisation English label
    // are migrated to the Chinese equivalent (user-edited names are untouched).
    public static void Seed(EcuNode node)
    {
        if (node.IsPrimed) return;

        foreach (var s in Seeds)
        {
            var existing = node.GetPidByWireId(s.Did);
            if (existing != null)
            {
                // Migrate pre-localisation English names to Chinese; leave any other name alone.
                if (LegacyNameMap.TryGetValue(existing.Name, out var zh) && existing.Name != zh)
                    existing.Name = zh;
                continue;
            }

            node.AddPid(new Pid
            {
                Mode        = PidMode.Mode22,
                Address     = s.Did,
                Name        = s.Name,
                Signal      = s.Signal,
                Scalar      = s.Scalar,
                Offset      = s.Offset,
                LengthBytes = s.Bytes,
                Size        = s.Bytes switch { 1 => PidSize.Byte, 2 => PidSize.Word, _ => PidSize.DWord },
                DataType    = s.Type,
                Unit        = "",
            });
        }
    }
}

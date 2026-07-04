using Common.Signals;

namespace Core.Ecu;

// Maps a Ford DMR (Data-Mode-Read) RAM address to a live engine-simulator signal.
//
// PCMTec sets up a datalog by binding a slot index to a RAM address via $A1
// SETUP_DMR (`A1 <index> <mode> <4B addr>`) and reading the values back on the
// 0x6A0 rapid-packet stream. The slot index is assigned dynamically per session;
// the ADDRESS is the stable identifier the user knows the meaning of (e.g.
// 0x003F7FA0 = engine RPM on the HAEE4UY strategy). This table lets the user
// pre-wire each address to a SignalId so FordUdsDispatch's broadcast loop drives
// that slot's DMR value from the matching engine signal.
//
// Only meaningful for ECUs running the Ford UDS persona (the only persona that
// serves $A1/$A0 DMR). Addresses with no mapping fall back to EngineRpm so a
// table-less config keeps today's behaviour.
public sealed class DmrSignalMapping
{
    /// <summary>The 32-bit RAM address PCMTec binds via $A1 (matched against the live slot map).</summary>
    public uint Address { get; set; }

    /// <summary>Optional human label (e.g. "Engine RPM") - editor convenience only.</summary>
    public string Name { get; set; } = "";

    /// <summary>The engine-simulator signal sampled to produce this slot's DMR value.</summary>
    public SignalId Signal { get; set; } = SignalId.EngineRpm;

    /// <summary>Wire encoding of the value bytes (must match PCMTec's parameter definition for this
    /// address). Default Float32BE - the validated RPM form on the HAEE4UY strategy.</summary>
    public DmrValueEncoding Encoding { get; set; } = DmrValueEncoding.Float32BE;

    /// <summary>Linear scale applied before encoding: emitted = signal * Scale + Offset. Lets the user
    /// match PCMTec's own scaling so the displayed value equals the simulated signal.</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>Linear offset applied before encoding (see <see cref="Scale"/>).</summary>
    public double Offset { get; set; }
}

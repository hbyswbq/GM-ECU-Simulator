using Common.Glitch;
using Common.Protocol;
using Common.Replay;
using Common.Signals;
using Common.Waveforms;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Common.Persistence;

// JSON-serialised configuration. Hex-string CAN IDs follow the Tester-Emu (https://github.com/jakka351/Tester-Emu)
// convention so a human can hand-edit. PidDto and WaveformDto stay flat - easier to diff in source control than a
// deeply-nested structure.
//
// Schema version 1 is the fresh-start baseline - the accumulated v2-v19 migration history was collapsed into this
// single version. ConfigSerializer stamps every saved file with CurrentVersion and refuses to load a file that claims
// a newer version than this build knows (a forward-compat guard); there is no back-version migration.
public sealed class SimulatorConfig
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string? Description { get; set; }
    public List<EcuDto> Ecus { get; set; } = new();

    // Null for users who have never loaded a bin. The coordinator
    // owns the runtime state; this DTO only carries the persisted hints.
    public BinReplayConfig? BinReplay { get; set; }

    // Null until the user has touched the Bootloader-capture toggle. When
    // present, ConfigStore.ApplyTo restores the Enabled flag (and optional
    // directory override) to bus.Capture so the toggle survives a restart.
    public BootloaderCaptureConfig? BootloaderCapture { get; set; }

    // Path to a DPS programming archive (.zip) the simulator should auto-
    // ingest on startup via Core.Dps.ArchivePrimer.ApplyTo. Null = no
    // prime-from-archive behaviour. Set by the File -> Prime from DPS
    // archive... menu item.
    public string? PrimeArchivePath { get; set; }

    // Optional path to a full 2 MiB ECU flash readback (.bin) whose boot
    // block (0x000000-0x00FFFF) is spliced with the archive OS module to
    // form a synthetic full binary for Mode1ADidBinExtractor. Null = no
    // donor (archive-only prime, walker returns null as before).
    public string? DonorBinPath { get; set; }

    // Ordered set of PIDs pinned to the main window's live-tile dashboard.
    // Cross-ECU (each entry names its owning ECU), so it lives at the config
    // root rather than under EcuDto. Null / omitted -> empty dashboard. The
    // WPF MainViewModel owns reading/writing this; ConfigStore.ApplyTo (which
    // only touches the bus) ignores it. Tiles whose (Ecu, Mode, Address)
    // target no longer resolves are dropped silently on load.
    public List<LiveTileDto>? LiveTiles { get; set; }
}

// One pinned live-tile on the main window's dashboard. References its target
// indirectly (by name/id, not object identity) so it survives a save/load
// round-trip and a PID-list rebuild (Load PIDs). The order of the list IS the
// on-screen tile order.
//
//   Source = Pid  -> an editable $22/$2D/$1A PID row, keyed by (Ecu, Mode, Address).
//   Source = Obd2 -> a built-in $01 (OBD-II / J1979) PID, keyed by (Ecu, Address),
//                    where Address holds the 1-byte $01 PID id (Mode is unused).
//
// Source defaults to Pid when absent, so a tile written before $01 tiles existed still resolves.
public sealed class LiveTileDto
{
    public LiveTileSource Source { get; set; } = LiveTileSource.Pid;

    public required string Ecu { get; set; }

    // Only meaningful when Source == Pid; defaults to the legacy Mode22.
    public PidMode Mode { get; set; } = PidMode.Mode22;

    [JsonConverter(typeof(HexUIntConverter))]
    public required uint Address { get; set; }
}

// Which value layer a live-tile is pinned to. Serialised as a camelCase string.
public enum LiveTileSource
{
    Pid,
    Obd2,
}

// Persisted bootloader-capture configuration. Slots into
// SimulatorConfig.BootloaderCapture. Directory is null when the user is happy
// with the default (%LOCALAPPDATA%\GmEcuSimulator\logs\captures); a non-null value
// overrides CaptureSettings.CaptureDirectory at load time. The legacy
// Enabled flag from earlier versions is silently dropped on load - capture
// writes are unconditional now (controlled by directory presence in
// CaptureSettings).
public sealed class BootloaderCaptureConfig
{
    public string? Directory { get; set; }
}

public sealed class EcuDto
{
    public required string Name { get; set; }

    [JsonConverter(typeof(HexUShortConverter))]
    public required ushort PhysicalRequestCanId { get; set; }

    [JsonConverter(typeof(HexUShortConverter))]
    public required ushort UsdtResponseCanId { get; set; }

    [JsonConverter(typeof(HexUShortConverter))]
    public required ushort UudtResponseCanId { get; set; }

    // Per-ECU glitch-injection settings. Always serialised; defaults to
    // disabled with all probabilities at 0 so saved configs are unaffected
    // until the user opts in via the editor.
    public GlitchConfig Glitch { get; set; } = GlitchConfig.CreateDefault();

    // ID of the security module to instantiate for $27 on this ECU. Null
    // (or unknown to the registry) → $27 returns NRC $11 ServiceNotSupported.
    public string? SecurityModuleId { get; set; }

    // Module-specific configuration handed to ISecurityAccessModule.LoadConfig.
    // Each module deserialises its own shape from this blob - ConfigSchema
    // doesn't need to know about any of them. Null is valid (module gets
    // defaults).
    public JsonElement? SecurityModuleConfig { get; set; }

    // ISO 15765-2 Flow Control BS byte emitted on First Frame reception.
    // Default 0 (no further FC; send all CFs in one burst). Override to
    // mimic real silicon - e.g. 6Speed.T43 tester needs BS=1 in the FC
    // tail to recognise the response.
    public byte FlowControlBlockSize { get; set; }

    // GMW3110-2010 §8.16 ReportProgrammedState ($A2) byte. Default 0x00
    // (FullyProgrammed) matches a normal running ECU.
    public byte ProgrammedState { get; set; }

    // 8-bit diagnostic address returned by $1A $B0 (Read ECU Diagnostic
    // Address). Typically equals the low byte of PhysicalRequestCanId, e.g.
    // PhysicalRequestCanId = $7E0 -> DiagnosticAddress = $11. Default 0.
    [JsonConverter(typeof(HexByteConverter))]
    public byte DiagnosticAddress { get; set; }

    // Flash-timing profile for the ford-uds flash-write path. Both default
    // 0 = instant. FlashTransferDelayMs delays each $36 response; FlashEraseDelayMs
    // is the modelled $B1 erase duration (driven by $7F B1 78 pending frames).
    // See EcuNode for the full rationale. Optional; absence -> 0.
    public int FlashTransferDelayMs { get; set; }
    public int FlashEraseDelayMs { get; set; }

    // When true, $23 ReadMemoryByAddress requests for addresses beyond the loaded
    // flash bin (RAM) get a positive zero-filled response instead of NRC $31. See
    // EcuNode.RamReadReturnsZeros. Default false -> spec-correct NRC, so old
    // configs load with unchanged behaviour.
    public bool RamReadReturnsZeros { get; set; }

    // When true, a Ford $A1 SETUP_DMR request whose RAM address is NOT in this
    // ECU's $A1 (SetupDataMode) grid gets NRC $31 RequestOutOfRange instead of
    // the positive E1 echo. See EcuNode.RejectUnmappedDmr. Default false ->
    // accept-all (the capture-friendly behaviour), so old configs are unchanged.
    public bool RejectUnmappedDmr { get; set; }

    // Which GM flash-READ dialect this ECU answers on $35/$36 (GM persona only):
    // "e38e67" = PowerPCM native boot-ROM upload, "t43" = 6Speed.T43 read-kernel.
    // See EcuNode.ReadFamily / ReadKernelFamily. Null / omitted -> "e38e67", so
    // old configs load unchanged; persisted only when it differs (WhenWritingNull).
    public string? ReadFamily { get; set; }

    // Response-timing profile. ResponseDelayMs models this ECU's processing
    // latency on every diagnostic response (0 = instant, the default). When it
    // exceeds the active stack's P2, the ECU emits 7F sid 78 RCR-RP heartbeats to
    // P2* unless Emit78WhenSlow is false. SessionTimeoutOverrideMs overrides the
    // active stack's P3C/S3 session timeout. See EcuNode for the full rationale.
    // All nullable so a default ECU stays quiet in the JSON (WhenWritingNull):
    // absence -> ResponseDelayMs 0, Emit78WhenSlow true (so old configs keep
    // spec-correct pending behaviour), no session-timeout override.
    public int? ResponseDelayMs { get; set; }
    public bool? Emit78WhenSlow { get; set; }
    public int? SessionTimeoutOverrideMs { get; set; }

    public List<PidDto> Pids { get; set; } = new();

    // The diagnostic standard set this ECU speaks (the stack-synthesis
    // discriminator). Null or omitted -> "gmw3110" (J1979 + GMW3110, the default
    // every GM ECU starts with). "ford-uds" selects the Ford UDS capture stack,
    // which logs every request and answers a whitelist. Absence in older configs
    // is silently treated as "gmw3110".
    public string? PersonaId { get; set; }

    // Protocol-stack service-allow-list overrides (DESIGN doc section 6). Each entry narrows or
    // widens which SIDs one bound standard answers on this ECU, relative to the synthesized default.
    // Null / omitted -> this ECU's bindings are exactly ProtocolStacks.SynthesizeFor(PersonaId +
    // CAN ids); written only for the standards the user has customised (a delta), so standard configs
    // stay quiet. See StackDto.
    public List<StackDto>? Stacks { get; set; }

    // Optional path to a flash bin file backing Service $23 ReadMemoryByAddress
    // when PersonaId == "ford-uds". The file is loaded once at config-apply
    // time via FordUdsDispatch.LoadFlashBin(path); subsequent $23 requests
    // serve directly from those bytes. Use this so PCMTec's flash-cross-check
    // probes (VIN at 0x000100C0, etc.) can complete against the real HAEE4UY
    // contents instead of NRC-ing. Path can be absolute or relative to the
    // config file's directory. Null / missing -> $23 returns NRC $22.
    public string? FlashBinPath { get; set; }

    // The operating point this ECU boots at (drives the live signal model). Null / omitted -> Idle. Persisted only
    // when it differs from Idle so standard configs stay quiet.
    public ScenarioId? Scenario { get; set; }

    // ID of the engine character driving the live signal model's derivation (induction curve, airflow, fuelling). Null
    // (or unknown to EngineCharacterRegistry) -> the naturally-aspirated default, so every config saved before this
    // field existed loads with the original behaviour unchanged. "boosted-gas-v8" selects the forced-induction model.
    public string? EngineModelId { get; set; }

    // The AccelDecelSweep rev-pull timing (climb / limiter-hold / coast / cross-fade / limiter-cut) is no longer
    // configurable per ECU - it is fixed at SweepProfile.Default. The former Sweep* override fields were retired; old
    // configs that still carry them deserialise harmlessly (unknown JSON members are ignored).

    // The $01 (OBD-II) PIDs this ECU has turned OFF, as a delta against the
    // built-in E38/E67 default supported subset. The advertised $01 set (and
    // its computed $00/$20/... support bitmask) is DefaultSupported minus this
    // list. Null / empty -> the ECU advertises the full default subset. Stored
    // as a delta so standard configs stay quiet and the default set can evolve
    // without rewriting every saved ECU.
    public List<byte>? Mode1Disabled { get; set; }

    // DBC-driven CAN broadcast set this ECU emits unsolicited while a J2534 host session is open
    // (see Core.Scheduler.BroadcastScheduler). Each entry is a CAN message with bit-packed signals
    // mapped to the live engine model or constants. Null / omitted -> no broadcast traffic; written
    // only when non-empty so standard configs stay quiet. Imported from a .dbc via the editor or
    // round-tripped on its own as a *.dbc.json (a flat List<BroadcastMessageDto>).
    public List<BroadcastMessageDto>? Broadcasts { get; set; }

    // Ford-persona DMR (Data-Mode-Read) address -> engine signal map. Lets the user pre-wire each
    // datalog RAM address PCMTec binds via $A1 to a live engine SignalId so the 0x6A0 stream carries
    // real values. Null / omitted -> no mappings (slots fall back to EngineRpm). Only consulted by
    // the Ford UDS persona.
    public List<DmrSignalMappingDto>? DmrSignalMappings { get; set; }
}

// One per-stack entry in EcuDto.Stacks - the persistence projection of a StackBinding's enabled
// allow-list (DESIGN doc section 6). Standard names a bound diagnostic standard ("J1979" / "GMW3110"
// / "UDS"); Services is the allow-list of SIDs that stack answers on this ECU as hex strings
// ("0x22"), or null/omitted for the wildcard "*" (every service the stack's catalog implements).
// The config references services by SID only - names, NRC vocabulary and timing come from the stack
// catalog in code at load time, never re-described here. Persisted only when the enabled set diverges
// from the synthesized default, so a quick config carries no Stacks and an empty Services list means
// "this stack answers nothing" (a deliberate hand-edit), distinct from null = wildcard.
public sealed class StackDto
{
    public required string Standard { get; set; }
    public List<string>? Services { get; set; }
}

// One DMR address -> engine signal mapping. Address is the 32-bit RAM address PCMTec reads via the
// datalog; Signal is the engine-simulator value the Ford persona writes into that slot's 0x6A0 frame.
public sealed class DmrSignalMappingDto
{
    [JsonConverter(typeof(HexUIntConverter))]
    public required uint Address { get; set; }

    public string Name { get; set; } = "";
    public required SignalId Signal { get; set; }

    // Wire encoding + linear transform applied before encoding (emitted = signal * Scale + Offset).
    // Defaults match the validated RPM form, so older files lacking these fields load unchanged.
    public DmrValueEncoding Encoding { get; set; } = DmrValueEncoding.Float32BE;
    public double Scale { get; set; } = 1.0;
    public double Offset { get; set; }
}

// One unsolicited CAN broadcast message. CanId is the raw arbitration ID a passive logger sees (not
// a diagnostic response id). PeriodMs is the free-form transmit period (from the DBC's
// GenMsgCycleTime, editable). Dlc is the payload length the signals pack into.
public sealed class BroadcastMessageDto
{
    [JsonConverter(typeof(HexUIntConverter))]
    public required uint CanId { get; set; }

    public required string Name { get; set; }
    public int Dlc { get; set; } = 8;
    public int PeriodMs { get; set; } = 100;
    public bool Enabled { get; set; } = true;

    // 29-bit extended arbitration ID when true (rare on GM/Ford HS buses, but the DBC carries it).
    public bool Extended { get; set; }

    public List<BroadcastSignalDto> Signals { get; set; } = new();
}

// One bit-packed signal within a broadcast message. The layout block (StartBit..Max) comes from the
// DBC SG_ line; the mapping block (Signal / ValueSource / Constant) is how the simulator sources the
// value at emit time: a live engine SignalId, a fixed Constant, or none (0).
public sealed class BroadcastSignalDto
{
    public required string Name { get; set; }
    public int StartBit { get; set; }
    public int Length { get; set; }
    public Common.Dbc.DbcByteOrder ByteOrder { get; set; } = Common.Dbc.DbcByteOrder.Motorola;
    public bool Signed { get; set; }
    public double Scale { get; set; } = 1.0;
    public double Offset { get; set; }
    public string Unit { get; set; } = "";
    public double Min { get; set; }
    public double Max { get; set; }

    // Live-signal source for this field. Signal sampled from the ECU's EngineModel when ValueSource
    // == Signal; Constant value when == Constant; 0 when None / null. Null in older files predating
    // the field -> ConfigStore infers it (a non-null Signal -> Signal, else None).
    public SignalId? Signal { get; set; }
    public BroadcastValueSource? ValueSource { get; set; }
    public double Constant { get; set; }
}

public sealed class PidDto
{
    [JsonConverter(typeof(HexUIntConverter))]
    public required uint Address { get; set; }

    public required string Name { get; set; }
    public required PidSize Size { get; set; }
    public required PidDataType DataType { get; set; }
    public double Scalar { get; set; } = 1.0;
    public double Offset { get; set; } = 0.0;
    public string Unit { get; set; } = "";
    public required WaveformDto Waveform { get; set; }

    /// <summary>
    /// Which service this row serves on the wire. See <see cref="PidMode"/>
    /// for the per-mode meaning of <see cref="Address"/>. Defaults to
    /// <see cref="PidMode.Mode22"/> so a config without a Mode field loads
    /// with the legacy single-mode behaviour.
    /// </summary>
    public PidMode Mode { get; set; } = PidMode.Mode22;

    /// <summary>
    /// Optional explicit response length in bytes. Overrides <see cref="Size"/>
    /// (which caps at 4) for PIDs longer than 4 bytes. Real GM ECUs expose $22
    /// PIDs of arbitrary byte length (e.g. E38 PID 0x155B is 17 bytes); bin-
    /// auto-extracted PID entries carry this field with the real-ECU length.
    /// </summary>
    public int? LengthBytes { get; set; }

    /// <summary>
    /// Optional verbatim response bytes as a contiguous hex string (e.g.
    /// <c>"0000..00"</c> for zero-fill). When present, the $22 handler returns
    /// these bytes directly and skips the waveform-encoding path. Length must
    /// match <see cref="LengthBytes"/> (or <see cref="Size"/> when <c>LengthBytes</c>
    /// is null) - padded with zeros if shorter. Lowercase, no spaces, no <c>0x</c>
    /// prefix; <c>null</c> means "use waveform" as before.
    /// </summary>
    public string? StaticBytes { get; set; }

    // Optional signal-backed source for this PID's value (the redesigned live-signal model). When set, the value comes
    // from the ECU's EngineModel rather than the waveform/StaticBytes, encoded with this row's Scalar/Offset. Null = a
    // legacy waveform / static PID. Serialised as a camelCase string (e.g. "engineRpm").
    public SignalId? Signal { get; set; }

    // Where the row draws its live value: "none" (reads 0), "waveform", or "signal". Null when the config predates
    // the explicit selector - ConfigStore.PidFrom then infers it (a non-null Signal -> Signal, otherwise the old
    // null-signal-means-waveform fallback) so such files keep behaving exactly as they did. Serialised camelCase.
    public PidValueSource? ValueSource { get; set; }
}

public sealed class WaveformDto
{
    public required WaveformShape Shape { get; set; }
    public double Amplitude { get; set; } = 1.0;
    public double Offset { get; set; } = 0.0;
    public double FrequencyHz { get; set; } = 1.0;
    public double PhaseDeg { get; set; } = 0.0;
    public double DutyCycle { get; set; } = 0.5;

    // Only meaningful when Shape == CsvFile; null / HoldLast for every other
    // shape so the JSON stays minimal on round-trip.
    public string? CsvFilePath { get; set; }
    public CsvLoopMode CsvLoopMode { get; set; } = CsvLoopMode.HoldLast;

    public WaveformConfig ToWaveformConfig() => new()
    {
        Shape       = Shape,
        Amplitude   = Amplitude,
        Offset      = Offset,
        FrequencyHz = FrequencyHz,
        PhaseDeg    = PhaseDeg,
        DutyCycle   = DutyCycle,
        CsvFilePath = CsvFilePath,
        CsvLoopMode = CsvLoopMode,
    };

    public static WaveformDto From(WaveformConfig cfg) => new()
    {
        Shape       = cfg.Shape,
        Amplitude   = cfg.Amplitude,
        Offset      = cfg.Offset,
        FrequencyHz = cfg.FrequencyHz,
        PhaseDeg    = cfg.PhaseDeg,
        DutyCycle   = cfg.DutyCycle,
        CsvFilePath = cfg.CsvFilePath,
        CsvLoopMode = cfg.CsvLoopMode,
    };
}

using Common.Glitch;
using Common.Protocol;
using Common.Signals;
using Core.Security;
using System.Text.Json;

namespace Core.Ecu;

// One simulated ECU on the virtual GMLAN bus. All identity properties
// are mutable so the editor UI can rename / re-address an ECU live.
// The PID list is a lock-protected List with an event so the UI can
// rebuild its display when PIDs are added or removed.
//
// CAN ID convention: real OBD-II-compliant GM vehicles use the $7E0+ pair
// (USDT request $7E0..$7E7, response $7E8..$7EF). GMW3110's worked examples
// use $241/$641/$541 - pedagogical only. Defaults match the real hardware
// convention; tests that quote the spec's example IDs do so on purpose to
// keep the test bytes traceable to the spec tables.
public sealed class EcuNode
{
    public string Name { get; set; } = "";

    // The CAN ids feed protocol-stack binding synthesis, so changing one invalidates the
    // lazily-built Stacks cache (see Stacks / RebuildStacks below). Backing fields rather than
    // auto-props purely so the editor can re-address an ECU live and Resolve stays current.
    private ushort physicalRequestCanId;
    private ushort usdtResponseCanId;
    private ushort uudtResponseCanId;
    public ushort PhysicalRequestCanId { get => physicalRequestCanId; set { physicalRequestCanId = value; stacks = null; } }
    public ushort UsdtResponseCanId    { get => usdtResponseCanId;    set { usdtResponseCanId = value;    stacks = null; } }
    public ushort UudtResponseCanId    { get => uudtResponseCanId;    set { uudtResponseCanId = value;    stacks = null; } }

    // Set by ArchivePrimer.BuildEcuNode. Primed ECUs are live on the bus
    // during the session but are not written to ecu_config.json - they are
    // reconstructed at startup from the persisted PrimeArchivePath.
    public bool IsPrimed { get; set; }

    // ISO 15765-2 Flow Control BS byte emitted by this ECU's reassembler in
    // response to an inbound First Frame. The FC tail (after the 0x30 CTS
    // PCI byte) is `BS STmin`; STmin is hard-coded to 0 (no inter-frame
    // delay), which is the most permissive behaviour and what every host
    // we've tested against accepts.
    //
    // Override BS per ECU to mimic real silicon: e.g. the 6Speed.T43 tester
    // checks the FC bytes for the substring "01", so a TCM configured to
    // emit BS=1 (FC = 30 01 00) makes that pattern match and lets the
    // kernel-upload flow proceed.
    public byte FlowControlBlockSize { get; set; }

    // GMW3110-2010 §8.16 ReportProgrammedState ($A2) value returned in the
    // positive response. Defaults to 0x00 FullyProgrammed - what a normal
    // running ECU reports. Other defined values:
    //   0x00 FP   fully programmed
    //   0x01 NSC  no op s/w or cal data
    //   0x02 NC   op s/w present, cal missing
    //   0x03 SDC  s/w present, default/no-start cal
    //   0x50 GMF  general memory fault
    //   0x51 RMF  RAM memory fault
    //   0x52 NVRMF NVRAM memory fault
    //   0x53 BMF  boot memory failure
    //   0x54 FMF  flash memory failure
    //   0x55 EEMF EEPROM memory failure
    public byte ProgrammedState { get; set; }

    /// <summary>
    /// GMW3110 8-bit diagnostic address. Returned in the $1A $B0 (Read ECU
    /// Diagnostic Address) response as the canonical "5A B0 &lt;diag_addr&gt;" reply,
    /// which is how testers and DPS rebuild their bus mapping matrix. Typically
    /// equals the low byte of <see cref="PhysicalRequestCanId"/>, e.g. $11.
    /// </summary>
    public byte DiagnosticAddress { get; set; }

    // Per-ECU glitch-injection configuration. The data model exists so the
    // UI/persistence layers can edit and round-trip these settings; the actual
    // injection logic in Core/Services is NOT yet implemented.
    public GlitchConfig Glitch { get; set; } = GlitchConfig.CreateDefault();

    // Three mode-keyed stores. Each PidMode owns its own dictionary keyed by the slice of Pid.Address the wire uses
    // for lookup, so a $1A request for DID $0C and a $22 request for 0x000C never reach into each other's namespace.
    // A single lock guards all three for simple atomic Add/Remove/Relocate. (Mode $01 is no longer a store - it is
    // the built-in J1979 projection over the signal layer; see Mode1Supported / Service01Handler.)
    //
    // Keys:
    //   mode1APids  key = (byte)(Pid.Address & 0xFF)        GMW3110 DID
    //   mode22Pids  key = (ushort)(Pid.Address & 0xFFFF)    $22 wire PID id
    //   mode2DPids  key = Pid.Address                       full 32-bit RAM addr
    //   mode23Pids  key = Pid.Address                       full 32-bit mem addr ($23 ReadMemoryByAddress)
    //
    // $22 wire lookup for Mode2D goes through GetPidByWireId, which derives
    // the alias 0xF000 | (addr & 0x0FFF) from each Mode2D entry's address.
    // Mode23 rows back $23 ReadMemoryByAddress via GetMemoryReadPid (exact
    // address match), consulted in VirtualBus before stack dispatch.
    private readonly Dictionary<byte,   Pid> mode1APids = new();
    private readonly Dictionary<ushort, Pid> mode22Pids = new();
    private readonly Dictionary<uint,   Pid> mode2DPids = new();
    private readonly Dictionary<uint,   Pid> mode23Pids = new();
    private readonly Lock pidsLock = new();

    // SAE J1979 Mode $09 RequestVehicleInformation store, keyed by InfoType id ($02 VIN, $04 CALID).
    // This is the make-neutral backing store Service09Handler reads. It is DELIBERATELY DISTINCT from the
    // $1A identity dictionaries above: Mode $09 is legislated OBD and is answered even by personas that do
    // NOT implement GMW3110 $1A at all (the Ford UDS capture stack NRC/silently-drops $1A), so Mode $09
    // cannot lean on the Mode1A store as a backing source. Seeded per-persona at config-apply
    // (ConfigStore): Ford from the flash bin (FordUdsDispatch.SeedMode09Identity), GM by projecting its
    // own $1A identity once (Service09Handler.SeedFromIdentity), since on real GM silicon the Mode $09
    // VIN/CALID equal the $1A $90/$C0 identity.
    private readonly Dictionary<byte, byte[]> mode09Info = new();

    // GMW3110 §8.3 ReadDataByIdentifier ($1A) data. Each DID maps to a raw
    // byte array; the service handler returns [$5A, did, ...bytes] verbatim
    // so the user can configure any spec-defined identifier (VIN $90,
    // calibration ID $92, etc.) without the simulator interpreting the value.
    // Mutable through Set/RemoveIdentifier so the editor UI can hot-edit.
    private readonly Dictionary<byte, byte[]> identifiers = new();
    // Per-DID provenance tag (User / Bin / Auto). STICKY across RemoveIdentifier:
    // a user who blanks a value still owns that row, so a subsequent auto-
    // populate or merge-mode bin load won't overwrite the deliberate blank.
    // Cleared explicitly via ClearAllIdentifierSources during a Replace-all
    // bin load, which then re-marks every well-known DID as Bin even when
    // blank (the user explicitly opted into "this ECU is the bin's view now").
    // Guarded by the same identifiersLock for atomic value+source updates.
    private readonly Dictionary<byte, DidSource> identifierSources = new();
    private readonly Lock identifiersLock = new();

    /// <summary>Raised after a PID is added, removed, or the list is cleared.</summary>
    public event EventHandler? PidsChanged;

    /// <summary>Raised after an identifier is added, replaced, removed, or cleared.</summary>
    public event EventHandler? IdentifiersChanged;

    // Per-ECU runtime state (Dpids, TesterPresent, Reassembler, security
    // session, $2D dynamic-PID set, LastEnhancedChannel, etc.) lives in
    // NodeState. EcuNode keeps user config; State carries runtime state.
    public NodeState State { get; } = new();

    // Signal-layer model for the redesigned diagnostic modes, added alongside the legacy per-mode Pid stores so the
    // modes can migrate one at a time ($01 reads from here first). The universal physics live in EngineModel /
    // J1979Catalogue (shared by every ECU); only the per-ECU pieces sit on the node.

    // Per-ECU live engine model: the scenario-driven signals every live projection reads from.
    public EngineModel EngineModel { get; } = new();

    // Per-ECU non-analog state behind the OBD-II status PIDs (MIL, DTC count, O2 layout, conformance, fuel type).
    public DiscreteState DiscreteState { get; } = new();

    // The J1979 Mode $01 PIDs this ECU advertises. The $00/$20/... support bitmasks are computed from exactly this
    // subset, so the advertised map can never claim a PID the ECU won't answer. A per-ECU copy of the catalogue
    // default, so toggling one ECU's set never mutates the shared default or another ECU's. The editor's $01 rows
    // flip membership via SetMode1Supported.
    private HashSet<byte> mode1Supported = new(J1979Catalogue.DefaultSupported);
    public IReadOnlySet<byte> Mode1Supported
    {
        get => mode1Supported;
        set => mode1Supported = new HashSet<byte>(value);
    }

    // Enable or disable a single J1979 PID in this ECU's advertised $01 subset (drives the $00/$20 support bitmask).
    public void SetMode1Supported(byte pid, bool supported)
    {
        if (supported) mode1Supported.Add(pid); else mode1Supported.Remove(pid);
    }

    // For ECUs using the ford-uds persona, the path of the flash bin
    // loaded into FordUdsDispatch at config-apply time. Stored here
    // purely so the save path (ConfigStore.EcuDtoFrom) can round-trip the
    // field back to JSON without losing it through a UI save. The persona
    // itself owns the byte array (static singleton); this is a bookkeeping
    // mirror, not a second copy of the data.
    public string? FlashBinPath { get; set; }

    // ---- Flash-READ ($35/$36 upload) profile ----
    //
    // Which GM flash-read dialect this ECU answers on $35/$36, for the two real
    // reader tools (PowerPCM_Flasher native upload = E38E67, 6Speed.T43 read-
    // kernel = T43). Editable in the Advanced tab under the security-module
    // picker (GM persona only). Default E38E67; see ReadKernelFamily.
    public ReadKernelFamily ReadFamily { get; set; } = ReadKernelFamily.E38E67;

    // Lazily-loaded, cached copy of the flash image behind a $35/$36 read,
    // sourced from FlashBinPath. A read serves these bytes so the dumped image
    // matches the ECU's loaded bin; addresses past the bin (or with no bin
    // loaded) read back as 0x00. The cache is keyed on the path so a Bin
    // Load/Clear in the editor transparently re-sources it. Guarded by
    // flashImageLock - reads run on the IPC dispatch thread.
    private readonly Lock flashImageLock = new();
    private byte[]? flashImageCache;
    private string? flashImageCachePath;

    /// <summary>
    /// Fill <paramref name="dest"/> with flash bytes starting at absolute byte
    /// <paramref name="offset"/>, sourced from the loaded flash bin
    /// (<see cref="FlashBinPath"/>). Bytes at or past the bin length - or any
    /// byte when no bin is loaded / the file can't be read - come back 0x00, so
    /// a read of an unbacked ECU still completes (a zero-filled dump) rather
    /// than stalling the tester. Used by the $35/$36 flash-read emulation.
    /// </summary>
    public void CopyFlash(long offset, Span<byte> dest)
    {
        byte[]? image;
        lock (flashImageLock)
        {
            if (!string.Equals(flashImageCachePath, FlashBinPath, StringComparison.Ordinal))
            {
                flashImageCachePath = FlashBinPath;
                flashImageCache = null;
                if (!string.IsNullOrWhiteSpace(FlashBinPath) && System.IO.File.Exists(FlashBinPath))
                {
                    try { flashImageCache = System.IO.File.ReadAllBytes(FlashBinPath); }
                    catch { flashImageCache = null; }   // unreadable -> serve zeros
                }
            }
            image = flashImageCache;
        }

        dest.Clear();
        if (image is null || offset >= image.Length || offset < 0) return;
        int avail = (int)Math.Min(dest.Length, image.Length - offset);
        if (avail > 0) image.AsSpan((int)offset, avail).CopyTo(dest);
    }

    // ---- Flash-timing profile (ford-uds flash-write path) ----
    //
    // A simulator answers instantly, so a flash write completes in well under
    // 10 s; a real PCM takes 30 s+. These two knobs let the ford-uds persona
    // model realistic timing. Both default 0 = instant (no behavioural change,
    // tests stay fast); set them in the editor's Advanced section.
    //
    // FlashTransferDelayMs: delay before each $36 TransferData positive response
    //   ($76). ~960 blocks * 30 ms ~= 30 s. Keep WELL under the tester's read
    //   timeout (~2.5 s observed) or the host aborts with ERR_TIMEOUT - per-block
    //   pacing is the realistic lever, not one giant delay.
    // FlashEraseDelayMs: time the erase takes. The positive response (Ford $B1 ->
    //   $F1, GM SPS $31 -> $71) is simply DEFERRED by this much - the ECU goes
    //   quiet then answers when done. NOT modelled with $7F nn 78 ResponsePending:
    //   PCMTec aborts on a pending response to the $B1 erase (observed 2026-06-06).
    public int FlashTransferDelayMs { get; set; }
    public int FlashEraseDelayMs { get; set; }

    // When true, a $23 ReadMemoryByAddress that targets RAM - any address range
    // lying at or beyond the loaded flash bin's length (with no bin loaded the
    // length is 0, so every address counts) - is answered with a positive $63
    // reply padded with zero bytes instead of NRC $31 RequestOutOfRange. The
    // check runs in VirtualBus.DispatchUsdt before the persona dispatch, so it
    // applies to every persona; in-bin reads still fall through to the persona
    // (the ford-uds persona serves the real bytes). Default false = the
    // spec-correct NRC $31 behaviour, so existing configs are unchanged.
    public bool RamReadReturnsZeros { get; set; }

    // When true, a Ford $A1 SETUP_DMR whose 32-bit RAM address has no matching
    // row in DmrSignalMappings (the $A1 SetupDataMode grid) is answered with
    // NRC $31 RequestOutOfRange instead of the positive E1 echo. Consulted only
    // by the Ford UDS dispatch (FordUdsDispatch); other personas don't serve
    // $A1. Default false = accept any address (the capture-friendly behaviour
    // that lets PCMTec bind slots we haven't pre-wired), so existing configs are
    // unchanged. Tick it to model an ECU that only accepts known DMR addresses -
    // but then every address PCMTec polls must already be in the grid or its
    // datalog won't start.
    public bool RejectUnmappedDmr { get; set; }

    // ---- Response-timing profile (P2 / P2* / session timeout) ----
    //
    // ResponseDelayMs models the time THIS ECU takes to produce a diagnostic
    // response (a real ECU answers within P2; a simulator answers instantly). 0
    // (default) = instant, byte-identical to the historic behaviour and what
    // every existing flow/test relies on. When set, every USDT response is
    // deferred by this much: VirtualBus.DispatchUsdt wires it onto the node's
    // fragmenter as a pacing hook (Core/Services/ResponseTiming). When the delay
    // exceeds the active stack's P2, the ECU emits 7F sid 78 RCR-RP heartbeats to
    // hold the tester's deadline open to P2* until the real response is ready
    // (Emit78WhenSlow). The flash paths use FlashTiming instead, which bypasses
    // the hook, so a flash response is never double-paced.
    public int ResponseDelayMs { get; set; }

    /// <summary>
    /// Emit 7F sid 78 RequestCorrectlyReceived-ResponsePending when a response is
    /// slower than P2 (default true, spec-correct). Set false to model an ECU that
    /// goes quiet and answers when done - some hosts (PCMTec on the $B1 erase)
    /// abort on a pending reply. Only consulted when <see cref="ResponseDelayMs"/>
    /// exceeds the active stack's P2; the flash paths are always silent (FlashTiming),
    /// so this governs only the generic response path.
    /// </summary>
    public bool Emit78WhenSlow { get; set; } = true;

    /// <summary>
    /// Optional per-ECU override (ms) of the diagnostic session timeout (GMW3110
    /// P3Cnom / ISO 14229 S3). Null (default) = use the active protocol stack's
    /// <see cref="Core.Protocol.TimingProfile.SessionTimeoutMs"/>. The
    /// TesterPresentTicker keys the P3C/S3 expiry off <see cref="SessionTimeoutMs"/>.
    /// </summary>
    public int? SessionTimeoutOverrideMs { get; set; }

    /// <summary>The active diagnostic session timeout (ms): the per-ECU override when
    /// set, else the first bound stack's <see cref="Core.Protocol.TimingProfile.SessionTimeoutMs"/>,
    /// else the GMW3110 P3Cnom default. Read by the TesterPresentTicker each tick so it
    /// tracks the live stack (e.g. the transient SPS kernel binding).</summary>
    public int SessionTimeoutMs =>
        SessionTimeoutOverrideMs
        ?? Stacks.FirstOrDefault()?.Stack.Timing.SessionTimeoutMs
        ?? Timing.P3Cnom;

    /// <summary>The application-layer <see cref="Core.Protocol.TimingProfile"/> in effect
    /// for a request arriving on <paramref name="canId"/>: the primary bound stack on that
    /// CAN id, falling back to the first stack, then GM timing. Drives the P2 / P2*
    /// response pacing (VirtualBus.DispatchUsdt).</summary>
    public Core.Protocol.TimingProfile EffectiveTiming(uint canId) =>
        PrimaryForCanId(canId)?.Stack.Timing
        ?? Stacks.FirstOrDefault()?.Stack.Timing
        ?? Core.Protocol.TimingProfile.Gm;

    // The standard this ECU speaks ("gmw3110" or "ford-uds") - the discriminator stack synthesis
    // keys off (ProtocolStacks.SynthesizeFor) and config round-trips. Changing it invalidates the
    // lazily-built Stacks cache. The SPS-kernel handover is NOT a PersonaId change: it replaces
    // Stacks transiently via EnterKernelMode/ExitKernelMode.
    private string personaId = "gmw3110";
    public string PersonaId { get => personaId; set { personaId = value; stacks = null; } }

    // ---- Protocol-stack bindings ----------------------------------------------------------
    // The diagnostic standards this ECU answers, each bound to a set of CAN ids with an enabled
    // allow-list. VirtualBus.DispatchUsdt resolves (canId, sid) -> binding through Resolve below.
    // The baseline list is lazily synthesised from PersonaId + CAN ids (ProtocolStacks.
    // SynthesizeFor) and cached; the PersonaId / CAN-id setters null the cache so the next access
    // re-derives it. While an SPS kernel handover is active, kernelStacks takes precedence (see
    // EnterKernelMode) so invalidating the baseline cache cannot disturb the kernel.
    private IList<Core.Protocol.StackBinding>? stacks;
    public IList<Core.Protocol.StackBinding> Stacks
        => kernelStacks ?? (stacks ??= BuildBaselineStacks());

    // Synthesize the baseline bindings (PersonaId + CAN ids), then layer any per-standard service
    // overrides (the stacks[] config / editor checklist) on top: a binding whose Stack.Standard has
    // an override gets its Enabled filter replaced, narrowing or widening which SIDs it answers.
    private IList<Core.Protocol.StackBinding> BuildBaselineStacks()
    {
        var bindings = Core.Protocol.ProtocolStacks.SynthesizeFor(this);
        if (serviceOverrides is { Count: > 0 })
            for (int i = 0; i < bindings.Count; i++)
                if (serviceOverrides.TryGetValue(bindings[i].Stack.Standard, out var filter))
                    bindings[i] = bindings[i] with { Enabled = filter };
        return bindings;
    }

    /// <summary>Force re-synthesis of the baseline <see cref="Stacks"/> from the current PersonaId +
    /// CAN ids (and service overrides). (The setters already invalidate the cache; this is for
    /// callers that mutate state the setters don't observe.)</summary>
    public void RebuildStacks() => stacks = BuildBaselineStacks();

    // ---- Per-standard service overrides (the stacks[] config / editor render-full-store-delta) ----
    // Keyed by IProtocolStack.Standard. Absent for a standard => its binding keeps the synthesized
    // filter (wildcard on the OBD GMW3110 / J1979 bindings; the restricted 9-SID set on a non-OBD
    // GMW3110 binding). The editor stores ONLY the delta here and ConfigStore persists it as
    // EcuDto.Stacks; a standard config carries no overrides and rebuilds purely from synthesis.
    private Dictionary<string, Core.Protocol.IServiceFilter>? serviceOverrides;

    /// <summary>The per-standard allow-list overrides currently in effect (the persistence delta),
    /// or null when none are set.</summary>
    public IReadOnlyDictionary<string, Core.Protocol.IServiceFilter>? ServiceOverrides => serviceOverrides;

    /// <summary>The override filter for <paramref name="standard"/>, or null when that standard's
    /// binding uses its synthesized default.</summary>
    public Core.Protocol.IServiceFilter? GetServiceOverride(string standard)
        => serviceOverrides is not null && serviceOverrides.TryGetValue(standard, out var f) ? f : null;

    /// <summary>Override (or, when <paramref name="filter"/> is null, clear) the enabled-service
    /// allow-list for the bound stack named <paramref name="standard"/>. Invalidates the baseline
    /// Stacks cache so the next dispatch sees the change.</summary>
    public void SetServiceOverride(string standard, Core.Protocol.IServiceFilter? filter)
    {
        if (filter is null)
        {
            serviceOverrides?.Remove(standard);
            if (serviceOverrides is { Count: 0 }) serviceOverrides = null;
        }
        else
        {
            (serviceOverrides ??= new())[standard] = filter;
        }
        stacks = null;
    }

    /// <summary>The baseline bindings <see cref="Core.Protocol.ProtocolStacks.SynthesizeFor"/> would
    /// produce for this ECU IGNORING any service overrides - the editor compares the user's ticks
    /// against these defaults so it can persist only the delta.</summary>
    public IList<Core.Protocol.StackBinding> SynthesizedDefaults()
        => Core.Protocol.ProtocolStacks.SynthesizeFor(this);

    /// <summary>Resolve an inbound (CAN id, SID) to the first binding that owns it, or null if no
    /// bound stack claims the SID on that CAN id (DESIGN doc section 5).</summary>
    public Core.Protocol.StackBinding? Resolve(uint canId, byte sid)
        => Stacks.FirstOrDefault(b => b.Owns(canId, sid));

    /// <summary>The first binding listening on <paramref name="canId"/> regardless of SID - the
    /// stack whose serviceNotSupported NRC answers an unowned SID on physical addressing.</summary>
    public Core.Protocol.StackBinding? PrimaryForCanId(uint canId)
        => Stacks.FirstOrDefault(b => b.CanIds.Match(canId));

    // ---- SPS kernel handover (transient stack replacement) ----
    // $36 sub $80 DownloadAndExecute hands the bus to an uploaded SPS kernel (Service36Handler
    // calls EnterKernelMode); $20 / P3C timeout restores the baseline (EcuExitLogic calls
    // ExitKernelMode). The kernel binding lives in its OWN field, separate from the baseline
    // `stacks` cache, and Stacks returns it in preference while set - so the kernel is
    // AUTHORITATIVE (a baseline GM SID it doesn't implement NRC-$11s, matching real hardware) AND a
    // CAN-id / PersonaId setter nulling the baseline cache mid-flash cannot demote it. On exit the
    // baseline re-synthesises lazily, picking up any CAN-id change made meanwhile.
    private IList<Core.Protocol.StackBinding>? kernelStacks;

    /// <summary>True while an SPS kernel binding has replaced the baseline stacks.</summary>
    public bool InKernelMode => kernelStacks is not null;

    /// <summary>Replace the active stacks with the single SPS-kernel binding.</summary>
    public void EnterKernelMode(Core.Protocol.StackBinding kernel)
        => kernelStacks = new List<Core.Protocol.StackBinding> { kernel };

    /// <summary>End a kernel handover - the baseline stacks take over again (re-synthesised lazily
    /// on next access). No-op when not in kernel mode, so EcuExitLogic / reset paths leave a
    /// configured (non-kernel) ECU untouched.</summary>
    public void ExitKernelMode() => kernelStacks = null;

    // The chosen security module for this ECU (null = $27 returns NRC $11
    // ServiceNotSupported). Mutable so the editor can hot-swap modules at
    // runtime, mirroring how identity fields work. The module instance is
    // separate from NodeState: state is data, the module is behaviour.
    public ISecurityAccessModule? SecurityModule { get; set; }

    // Raw module-specific configuration as last loaded from disk or edited
    // in the UI. ConfigStore round-trips this verbatim; the module consumes
    // it via LoadConfig. EcuNode owns the blob so the JSON survives across
    // module hot-swaps and so saving doesn't require each module to remember
    // its own config separately.
    public JsonElement? SecurityModuleConfig { get; set; }

    /// <summary>
    /// Snapshot of every PID across every mode-keyed store, ordered by mode
    /// then by key for deterministic enumeration. Returns an array copy under
    /// the lock so callers can iterate cross-thread without holding it.
    /// Replaces the legacy <c>Pids</c> + <c>Mode1Pids</c> split.
    /// </summary>
    public IEnumerable<Pid> AllPids
    {
        get
        {
            lock (pidsLock)
            {
                var arr = new Pid[mode22Pids.Count + mode2DPids.Count + mode23Pids.Count + mode1APids.Count];
                int i = 0;
                foreach (var kv in mode22Pids.OrderBy(kv => kv.Key))  arr[i++] = kv.Value;
                foreach (var kv in mode2DPids.OrderBy(kv => kv.Key))  arr[i++] = kv.Value;
                foreach (var kv in mode23Pids.OrderBy(kv => kv.Key))  arr[i++] = kv.Value;
                foreach (var kv in mode1APids.OrderBy(kv => kv.Key))  arr[i++] = kv.Value;
                return arr;
            }
        }
    }

    /// <summary>Look up by full <see cref="Pid.Address"/> across every store.
    /// For Mode22/Mode1A/Mode1 the address is the wire id; for Mode2D it is the
    /// 32-bit memory address. First match wins in Mode22 -> Mode2D -> Mode1A ->
    /// Mode1 order.</summary>
    public Pid? GetPid(uint address)
    {
        lock (pidsLock)
        {
            if (address <= 0xFFFF && mode22Pids.TryGetValue((ushort)address, out var m22)) return m22;
            if (mode2DPids.TryGetValue(address, out var m2D))                                return m2D;
            if (address <= 0xFF   && mode1APids.TryGetValue((byte)address, out var m1A))    return m1A;
            return null;
        }
    }

    /// <summary>Wire-side $22 lookup. Mode22 hits the dict directly; Mode2D
    /// matches via the derived alias <c>0xF000 | (Address &amp; 0x0FFF)</c>.
    /// Mode1A / Mode1 rows are unreachable here by design - $22 has its own
    /// 2-byte PID id namespace disjoint from $1A's 1-byte DID space and $01's
    /// 1-byte PID space.</summary>
    public Pid? GetPidByWireId(ushort wireId)
    {
        lock (pidsLock)
        {
            if (mode22Pids.TryGetValue(wireId, out var m22)) return m22;
            // Mode2D alias scan. The dict is typically small (single-digit
            // entries even on heavily-used $2D sessions), so a linear walk
            // is cheaper than maintaining a parallel alias->Pid map.
            foreach (var (addr, pid) in mode2DPids)
                if ((ushort)(0xF000 | (addr & 0x0FFF)) == wireId) return pid;
            return null;
        }
    }

    /// <summary>$23 ReadMemoryByAddress hook. Returns the Mode23 row whose
    /// <see cref="Pid.Address"/> equals <paramref name="address"/> exactly, or
    /// null when no row claims it (the caller falls back to the loaded flash bin
    /// / RAM-read-zeros / stack NRC). Consulted by VirtualBus before stack
    /// dispatch so a user-defined row wins over every other $23 source.</summary>
    public Pid? GetMemoryReadPid(uint address)
    {
        lock (pidsLock) return mode23Pids.TryGetValue(address, out var p) ? p : null;
    }

    /// <summary>$1A handler hook. Returns the Mode1A row for the given DID,
    /// or null - the caller falls back to <c>GetIdentifier</c> for bin/archive-
    /// seeded values that weren't overridden in the editor grid.</summary>
    public Pid? GetMode1APid(byte did)
    {
        lock (pidsLock) return mode1APids.TryGetValue(did, out var p) ? p : null;
    }

    /// <summary>$09 RequestVehicleInformation lookup. Returns the bytes backing an InfoType ($02 VIN,
    /// $04 CALID), or null when this ECU advertises nothing for it. Read by Service09Handler; this is the
    /// ONLY source it consults - Mode $09 never reads the $1A identity stores.</summary>
    public byte[]? GetMode09Info(byte infoType)
    {
        lock (pidsLock) return mode09Info.TryGetValue(infoType, out var v) ? v : null;
    }

    /// <summary>Set (or replace) the bytes backing a Mode $09 InfoType. An empty span clears the entry so
    /// the InfoType reports unsupported. Seeded at config-apply by the per-persona Mode $09 seeders.</summary>
    public void SetMode09Info(byte infoType, ReadOnlySpan<byte> data)
    {
        lock (pidsLock)
        {
            if (data.IsEmpty) mode09Info.Remove(infoType);
            else mode09Info[infoType] = data.ToArray();
        }
    }

    /// <summary>Insert or replace a PID. Routes to the per-mode store based on
    /// <see cref="Pid.Mode"/>; replaces any existing entry with the same key in
    /// that store. Always raises <see cref="PidsChanged"/>.</summary>
    public void AddPid(Pid pid)
    {
        // Bind every added PID to this ECU's engine model so a signal-backed PID can resolve live values; harmless
        // for non-signal PIDs (the engine reference is only consulted when Pid.Signal is set).
        pid.AttachEngine(EngineModel);
        lock (pidsLock)
        {
            switch (pid.Mode)
            {
                case PidMode.Mode1A: mode1APids[(byte)(pid.Address & 0xFF)]    = pid; break;
                case PidMode.Mode22: mode22Pids[(ushort)(pid.Address & 0xFFFF)] = pid; break;
                case PidMode.Mode2D: mode2DPids[pid.Address]                    = pid; break;
                case PidMode.Mode23: mode23Pids[pid.Address]                    = pid; break;
            }
        }
        PidsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Remove a PID. Targets the store that owns <see cref="Pid.Mode"/>;
    /// uses <see cref="Pid.Address"/>-derived key for the lookup. Returns true
    /// when an entry was removed.</summary>
    public bool RemovePid(Pid pid)
    {
        bool removed;
        lock (pidsLock)
        {
            removed = pid.Mode switch
            {
                PidMode.Mode1A  => mode1APids.Remove((byte)(pid.Address & 0xFF)),
                PidMode.Mode22  => mode22Pids.Remove((ushort)(pid.Address & 0xFFFF)),
                PidMode.Mode2D  => mode2DPids.Remove(pid.Address),
                PidMode.Mode23  => mode23Pids.Remove(pid.Address),
                _               => false,
            };
        }
        if (removed)
            PidsChanged?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    /// <summary>Remove every PID whose <see cref="Pid.Address"/> matches across
    /// every store. The caller doesn't know which mode owns the address - used
    /// by <c>EcuExitLogic</c> to clean up $2D-defined dynamic PIDs (registered
    /// as Mode22 with Address = pidId) at session end.</summary>
    public bool RemovePidByAddress(uint address)
    {
        bool removed = false;
        lock (pidsLock)
        {
            if (address <= 0xFFFF && mode22Pids.Remove((ushort)address)) removed = true;
            if (mode2DPids.Remove(address))                              removed = true;
            if (mode23Pids.Remove(address))                              removed = true;
            if (address <= 0xFF   && mode1APids.Remove((byte)address))   removed = true;
        }
        if (removed)
            PidsChanged?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    /// <summary>Move <paramref name="pid"/> between mode stores in one atomic
    /// step. Called by the editor's mode-flip handler: the underlying Pid
    /// instance stays the same (the editor's ObservableCollection doesn't
    /// churn), only its storage location moves. Removes from the store keyed
    /// by <paramref name="oldMode"/>, inserts into the store keyed by the
    /// pid's CURRENT mode (which the caller has already updated).</summary>
    public void RelocatePidMode(Pid pid, PidMode oldMode)
    {
        if (oldMode == pid.Mode) return;
        lock (pidsLock)
        {
            switch (oldMode)
            {
                case PidMode.Mode1A: mode1APids.Remove((byte)(pid.Address & 0xFF));    break;
                case PidMode.Mode22: mode22Pids.Remove((ushort)(pid.Address & 0xFFFF)); break;
                case PidMode.Mode2D: mode2DPids.Remove(pid.Address);                    break;
                case PidMode.Mode23: mode23Pids.Remove(pid.Address);                    break;
            }
            switch (pid.Mode)
            {
                case PidMode.Mode1A: mode1APids[(byte)(pid.Address & 0xFF)]    = pid; break;
                case PidMode.Mode22: mode22Pids[(ushort)(pid.Address & 0xFFFF)] = pid; break;
                case PidMode.Mode2D: mode2DPids[pid.Address]                    = pid; break;
                case PidMode.Mode23: mode23Pids[pid.Address]                    = pid; break;
            }
        }
        PidsChanged?.Invoke(this, EventArgs.Empty);
    }

    // Re-key a PID after its Address changed in place. The per-mode stores are keyed by Address, so editing only
    // Pid.Address (the editor's Address column) would leave the entry under its old key and every GetPid /
    // GetPidByWireId lookup by the new address would miss. Callers pass the prior address so we can move the entry
    // within the PID's current mode store. No-op when the address is unchanged.
    public void RekeyPidAddress(Pid pid, uint oldAddress)
    {
        if (oldAddress == pid.Address) return;
        lock (pidsLock)
        {
            switch (pid.Mode)
            {
                case PidMode.Mode1A:
                    mode1APids.Remove((byte)(oldAddress & 0xFF));
                    mode1APids[(byte)(pid.Address & 0xFF)] = pid;
                    break;
                case PidMode.Mode22:
                    mode22Pids.Remove((ushort)(oldAddress & 0xFFFF));
                    mode22Pids[(ushort)(pid.Address & 0xFFFF)] = pid;
                    break;
                case PidMode.Mode2D:
                    mode2DPids.Remove(oldAddress);
                    mode2DPids[pid.Address] = pid;
                    break;
                case PidMode.Mode23:
                    mode23Pids.Remove(oldAddress);
                    mode23Pids[pid.Address] = pid;
                    break;
            }
        }
        PidsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Notifies subscribers that the PID list has changed without adding/removing.
    /// Editor calls this after mutating a PID's properties so the live monitor
    /// refreshes its column data.
    /// </summary>
    public void RaisePidsChanged() => PidsChanged?.Invoke(this, EventArgs.Empty);

    // ---- DBC broadcast set ----------------------------------------------------------------------
    // Unsolicited CAN broadcast messages this ECU emits while a host session is open (driven by
    // BroadcastScheduler). Lock-guarded with a change event so the scheduler can rebuild its timers
    // and the editor can rebind, mirroring the PID store shape.
    private readonly List<BroadcastMessage> broadcasts = new();
    private readonly Lock broadcastsLock = new();

    /// <summary>Raised after a broadcast message is added, removed, replaced, or edited.</summary>
    public event EventHandler? BroadcastsChanged;

    /// <summary>Snapshot copy - safe to enumerate cross-thread (e.g. the scheduler tick).</summary>
    public IReadOnlyList<BroadcastMessage> Broadcasts
    {
        get { lock (broadcastsLock) return broadcasts.ToArray(); }
    }

    public void AddBroadcast(BroadcastMessage msg)
    {
        lock (broadcastsLock) broadcasts.Add(msg);
        BroadcastsChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool RemoveBroadcast(BroadcastMessage msg)
    {
        bool removed;
        lock (broadcastsLock) removed = broadcasts.Remove(msg);
        if (removed) BroadcastsChanged?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    public void ReplaceBroadcasts(IEnumerable<BroadcastMessage> newMessages)
    {
        lock (broadcastsLock) { broadcasts.Clear(); broadcasts.AddRange(newMessages); }
        BroadcastsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Notifies subscribers a broadcast message's properties changed in place (period,
    /// signal mapping, ...) so the scheduler can rebuild its timers.</summary>
    public void RaiseBroadcastsChanged() => BroadcastsChanged?.Invoke(this, EventArgs.Empty);

    // ---- DMR address -> engine signal map (Ford persona only) -----------------------------------
    // Pre-wired map from a DMR RAM address to a live engine SignalId, used by FordUdsDispatch's
    // 0x6A0 broadcast loop to drive each datalog slot's value. Lock-guarded with a change event,
    // mirroring the broadcast/PID stores. Only the Ford UDS persona consults it.
    private readonly List<DmrSignalMapping> dmrSignalMappings = new();
    private readonly Lock dmrSignalMapLock = new();

    /// <summary>Raised after a DMR signal mapping is added, removed, replaced, or edited.</summary>
    public event EventHandler? DmrSignalMappingsChanged;

    /// <summary>Snapshot copy - safe to enumerate cross-thread (the broadcast tick).</summary>
    public IReadOnlyList<DmrSignalMapping> DmrSignalMappings
    {
        get { lock (dmrSignalMapLock) return dmrSignalMappings.ToArray(); }
    }

    public void AddDmrSignalMapping(DmrSignalMapping m)
    {
        lock (dmrSignalMapLock) dmrSignalMappings.Add(m);
        DmrSignalMappingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool RemoveDmrSignalMapping(DmrSignalMapping m)
    {
        bool removed;
        lock (dmrSignalMapLock) removed = dmrSignalMappings.Remove(m);
        if (removed) DmrSignalMappingsChanged?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    public void ReplaceDmrSignalMappings(IEnumerable<DmrSignalMapping> mappings)
    {
        lock (dmrSignalMapLock) { dmrSignalMappings.Clear(); dmrSignalMappings.AddRange(mappings); }
        DmrSignalMappingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RaiseDmrSignalMappingsChanged() => DmrSignalMappingsChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>The full mapping for <paramref name="address"/>, or null if unmapped. Called from the
    /// broadcast tick, so it reads under the lock without allocating a snapshot.</summary>
    public DmrSignalMapping? DmrMappingFor(uint address)
    {
        lock (dmrSignalMapLock)
            foreach (var m in dmrSignalMappings)
                if (m.Address == address) return m;
        return null;
    }

    /// <summary>The engine signal mapped to <paramref name="address"/>, or null if unmapped.</summary>
    public Common.Signals.SignalId? DmrSignalFor(uint address) => DmrMappingFor(address)?.Signal;

    /// <summary>Snapshot copy of the identifier map - safe to enumerate cross-thread.</summary>
    public IReadOnlyDictionary<byte, byte[]> Identifiers
    {
        get { lock (identifiersLock) return identifiers.ToDictionary(kv => kv.Key, kv => (byte[])kv.Value.Clone()); }
    }

    /// <summary>
    /// Snapshot copy of the per-DID source map - safe to enumerate cross-thread.
    /// Includes entries that have no bytes (sticky blanks: source=User with
    /// the value cleared) so ConfigStore can preserve the sticky tag on save.
    /// </summary>
    public IReadOnlyDictionary<byte, DidSource> IdentifierSources
    {
        get { lock (identifiersLock) return new Dictionary<byte, DidSource>(identifierSources); }
    }

    /// <summary>Looks up a $1A identifier. Returns null if the DID is not configured.</summary>
    public byte[]? GetIdentifier(byte did)
    {
        lock (identifiersLock) return identifiers.TryGetValue(did, out var data) ? (byte[])data.Clone() : null;
    }

    /// <summary>
    /// Sets (or replaces) the data for a $1A identifier with an explicit
    /// provenance tag. The bytes are copied. Callers that don't track
    /// provenance use the single-arg overload, which defaults the source
    /// to <see cref="DidSource.User"/>.
    /// </summary>
    public void SetIdentifier(byte did, ReadOnlySpan<byte> data, DidSource source)
    {
        lock (identifiersLock)
        {
            identifiers[did] = data.ToArray();
            identifierSources[did] = source;
        }
        IdentifiersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Backwards-compatible overload that records the value as user-typed.
    /// New code that knows the provenance should call the three-arg overload
    /// with an explicit <see cref="DidSource"/>.
    /// </summary>
    public void SetIdentifier(byte did, ReadOnlySpan<byte> data)
        => SetIdentifier(did, data, DidSource.User);

    /// <summary>
    /// Removes the bytes for a DID but keeps its source tag. This is what
    /// makes "user typed then deleted" stay <see cref="DidSource.User"/>
    /// across subsequent auto-populate / merge-mode bin loads. Call
    /// <see cref="ClearAllIdentifierSources"/> to wipe the tags too.
    /// </summary>
    public bool RemoveIdentifier(byte did)
    {
        bool removed;
        lock (identifiersLock) removed = identifiers.Remove(did);
        if (removed) IdentifiersChanged?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    /// <summary>
    /// Sets only the provenance tag for a DID without touching the bytes.
    /// Used by Replace-all bin loads to mark every well-known DID as Bin
    /// source even when the bin didn't surface a value for it.
    /// </summary>
    public void SetIdentifierSource(byte did, DidSource source)
    {
        lock (identifiersLock) identifierSources[did] = source;
        IdentifiersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Returns the recorded provenance for a DID, or <see cref="DidSource.Blank"/>
    /// if none has ever been set (the default for a fresh ECU).
    /// </summary>
    public DidSource GetIdentifierSource(byte did)
    {
        lock (identifiersLock)
            return identifierSources.TryGetValue(did, out var s) ? s : DidSource.Blank;
    }

    /// <summary>
    /// Drops every recorded provenance tag. Used by Replace-all bin loads
    /// to reset the source state before re-marking. Does NOT touch the
    /// identifier byte map - callers that want to wipe values too must
    /// loop <see cref="RemoveIdentifier"/> separately.
    /// </summary>
    public void ClearAllIdentifierSources()
    {
        lock (identifiersLock) identifierSources.Clear();
        IdentifiersChanged?.Invoke(this, EventArgs.Empty);
    }

    public override string ToString()
    {
        return Name;
    }
}

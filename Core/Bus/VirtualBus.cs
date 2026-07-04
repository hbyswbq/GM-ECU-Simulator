using Common.PassThru;
using Common.Protocol;
using Core.Ecu;
using Core.Protocol;
using Core.Replay;
using Core.Scheduler;
using Core.Services;
using Core.Transport;
using Core.Utilities;
using System.Diagnostics;

namespace Core.Bus;

// The virtual GMLAN bus. Holds the configured ECU nodes, the periodic
// DPID scheduler, and the TesterPresent ticker. Routes inbound frames
// from any J2534 channel to the matching ECU by destination CAN ID.
//
// Mutating the ECU set (Add/Remove/Replace) is thread-safe; iterators
// take a lock-protected snapshot so the IPC dispatcher and the editor
// UI never collide.
public sealed class VirtualBus
{
    private readonly List<EcuNode> nodes = new();
    private readonly Lock nodesLock = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();

    public DpidScheduler Scheduler { get; }
    public TesterPresentTicker Ticker { get; }
    public IdleBusSupervisor IdleSupervisor { get; }

    // Emits the ECUs' DBC-driven CAN broadcast messages via Broadcaster while a host session is open.
    // Driven by the composition root (RebuildAndStart on HostConnected, StopAll on HostDisconnected).
    public BroadcastScheduler BroadcastScheduler { get; }
    public double NowMs => clock.Elapsed.TotalMilliseconds;

    /// <summary>
    /// Free-running, monotonic microsecond clock shared by the WHOLE bus (all
    /// channels, all ECUs/stacks - one global time base, not per-stack).
    /// Stamped onto every host-bound frame's <see cref="Common.PassThru.PassThruMsg.Timestamp"/>
    /// at the single delivery chokepoint (<see cref="ChannelSession.EnqueueRx"/>)
    /// so a J2534 host can plot a real time axis. TimeSpan.Ticks are 100 ns
    /// units, so /10 yields microseconds. Cast to uint to match the J2534
    /// PASSTHRU_MSG Timestamp field; the ~71.6 min 32-bit wrap is expected and
    /// hosts handle it (the value only needs to be monotonic and in µs).
    /// </summary>
    public uint NowMicros => (uint)(clock.Elapsed.Ticks / 10);

    /// <summary>
    /// Bootloader-capture configuration. Writes happen unconditionally when
    /// <see cref="CaptureSettings.CaptureDirectory"/> is set; null disables
    /// disk side effects (unit tests construct VirtualBus directly and
    /// leave it null, WPF startup sets the LOCALAPPDATA path).
    /// </summary>
    public CaptureSettings Capture { get; } = new();

    // Bin-replay coordinator. Set by the composition root (App.OnStartup).
    // Null in tests that don't exercise the replay path; the dispatcher
    // and ticker check for null before calling MaybeStart / MaybeStop.
    public BinReplayCoordinator? Replay { get; set; }

    // Last time any host activity was observed (any incoming IPC request).
    // Updated by Shim.Ipc.RequestDispatcher.Dispatch via NoteHostActivity.
    // Currently informational - the IdleBusSupervisor that used to react to
    // gaps in this stream was stubbed 2026-05-15 (see IdleBusSupervisor.cs
    // header); the value is kept because tests assert on it and a future
    // diagnostic / metrics path may want it again.
    private long lastHostActivityMs;
    public long LastHostActivityMs => Volatile.Read(ref lastHostActivityMs);
    public void NoteHostActivity() => Volatile.Write(ref lastHostActivityMs, (long)NowMs);

    /// <summary>
    /// Raised on an explicit bus-wide idle reset. The time-based supervisor
    /// that used to fire this was stubbed 2026-05-15; nothing in the current
    /// build raises it on a schedule. Kept so subscribers
    /// (Shim.Ipc.IpcSessionState, MainWindow file-log lifecycle, BinReplay)
    /// stay wired up if a future force-idle path calls IdleBusSupervisor.DoReset.
    /// </summary>
    public event Action? IdleReset;
    internal void RaiseIdleReset() => IdleReset?.Invoke();

    /// <summary>
    /// Raised when the host calls PassThruOpen (graceful session start).
    /// Counterpart to <see cref="HostDisconnected"/>; subscribers open
    /// per-session resources here (e.g. a fresh log file).
    /// Public Raise so Shim.Ipc.RequestDispatcher can fire it from the
    /// IPC layer.
    /// </summary>
    public event Action? HostConnected;
    public void RaiseHostConnected()
    {
        // Session-lifecycle trace. The broadcast scheduler and file-log sink
        // arm on this event; a desync here (host actively making calls while
        // the sim still thinks it's disconnected) silently stops broadcasts
        // and refuses to start the file log, so make every transition visible.
        LogJ2534?.Invoke("[session] HostConnected (PassThruOpen)");
        HostConnected?.Invoke();
    }

    /// <summary>
    /// Raised when the host's J2534 session ends. Two paths fire it:
    ///   - graceful: RequestDispatcher.Close on PassThruClose;
    ///   - pipe drop: NamedPipeServer.HandleClientAsync's finally block, on
    ///     any non-clean exit (USB unplug, host crash, host process killed).
    /// Subscribers finalise per-session resources here so each capture
    /// lands as one tidy file with its closing trailer. They must be
    /// idempotent because pipe drop can follow a graceful Close.
    /// </summary>
    public event Action? HostDisconnected;
    public void RaiseHostDisconnected()
    {
        LogJ2534?.Invoke("[session] HostDisconnected (PassThruClose / pipe-drop)");
        HostDisconnected?.Invoke();
    }

    /// <summary>Raised after Add/Remove/Replace mutates the ECU set.</summary>
    public event EventHandler? NodesChanged;

    /// <summary>
    /// Cross-channel raw-frame broadcast hook. Set by the Shim's
    /// IpcSessionState at construct time so any Core code (stacks,
    /// schedulers) can push a UUDT frame at every open channel. Null
    /// while no IPC session is alive - callers must null-check.
    /// </summary>
    public IFrameBroadcaster? Broadcaster { get; set; }

    /// <summary>
    /// Frame-level traffic sink. Set to non-null to receive one record per
    /// frame in two formats simultaneously:
    ///   pretty - human-readable, space-delimited, for the on-screen textbox
    ///            e.g. "[chan 1] Rx 7E2 02 10 02  ; StartDiagnosticSession"
    ///   csv    - comma-separated for the log file; leading [timestamp],[CAN]
    ///            prefix is prepended by MainWindow before the disk write
    ///            e.g. "[chan 1],Rx,7E2 02 10 02,StartDiagnosticSession"
    /// Rx = frame received from the J2534 host; Tx = frame the simulator
    /// generated for the host ("- HOST FILTERED" appended when the host's own
    /// channel filter blocked delivery). The third argument is true when the
    /// frame carries a TesterPresent request ($3E) or its positive response
    /// ($7E); the fourth is true when the frame is a configured DBC broadcast
    /// (its CAN ID matches one of an ECU's <see cref="EcuNode.Broadcasts"/>).
    /// The UI log pane can use either flag to suppress that class of noise
    /// independently of the file-log capture. Null means no logging.
    /// </summary>
    public Action<string, string, bool, bool>? LogFrame { get; set; }

    /// <summary>
    /// When true, every bus-frame log line is suffixed with a short
    /// human-readable tag produced by <see cref="Common.Protocol.UdsAnnotator"/>
    /// (e.g. "  ; SecurityAccess - RequestSeed"). Off by default - testers
    /// who only want raw hex pay no annotation cost on the hot path.
    /// </summary>
    public bool AnnotateFrames { get; set; }

    /// <summary>
    /// When true, long ISO-TP transfers in the bus log are condensed: the
    /// first <see cref="BulkTransferCollapser.HeadCfCount"/> consecutive
    /// frames pass through, the middle is replaced with a single marker
    /// line summarising how many frames were hidden, then the last
    /// <see cref="BulkTransferCollapser.TailCfCount"/> CFs come through.
    /// Transitions off→on do nothing for transfers already in progress;
    /// transitions on→off call <see cref="BulkTransferCollapser.Reset"/>
    /// so the next pass starts clean.
    /// </summary>
    public bool CollapseBulkTransfers
    {
        get => collapseBulkTransfers;
        set
        {
            if (collapseBulkTransfers == value) return;
            collapseBulkTransfers = value;
            if (!value) bulkCollapser.Reset();
        }
    }
    private bool collapseBulkTransfers;
    private readonly BulkTransferCollapser bulkCollapser = new();

    // When true, RequestDispatcher.StartPeriodic creates a timer that drives
    // $3E TesterPresent on the host's behalf for any host that registered the
    // frame via PassThruStartPeriodicMsg. Off makes the simulator accept the
    // registration but not tick - the host's P3C session lapses unless it
    // sends $3E some other way. Process-wide because it's a simulator-helper
    // policy, not an ECU trait; MainViewModel pushes the user's persisted
    // AppSettings choice in on startup and on every menu toggle.
    public bool AllowPeriodicTesterPresent { get; set; } = true;

    /// <summary>
    /// Diagnostic sink for events emitted from the Shim/ project: PassThru*
    /// IPC narration (with embedded multi-line tx[i]/rx[i] detail), pipe
    /// connect/disconnect, periodic-message register/unregister, idle-drain
    /// notices. Written to the file log with the [J2534] tag. Unlike
    /// LogFrame this is NOT gated by the "Log frame traffic" checkbox.
    /// Null means no logging.
    /// </summary>
    public Action<string>? LogJ2534 { get; set; }

    /// <summary>
    /// Diagnostic sink for simulator-internal events: service-handler
    /// decisions, security-module state transitions, DPID scheduler stalls,
    /// idle-supervisor exits, capture-writer output, app lifecycle.
    /// Written to the file log with the [SIM] tag. Null means no logging.
    /// </summary>
    public Action<string>? LogSim { get; set; }

    /// <summary>
    /// Higher-prominence sink for events the user should see at a glance -
    /// surfaced in the status bar at the bottom of the main window. Reserved
    /// for things that meaningfully change what the simulator is doing for
    /// a host (rejected connect attempts, etc.). Null means no surfacing.
    /// </summary>
    public Action<string>? OnStatusMessage { get; set; }

    // Two formats are emitted to the LogFrame sink per frame:
    //
    //   pretty  "[chan N] <Rx|Tx> <canId payload> [; annotation]"
    //           human-readable, rendered in the on-screen textbox as-is. The
    //           channel tag stays on the pretty line because a glance at the
    //           textbox during multi-channel debugging needs the disambiguator.
    //
    //   csv     "[<Rx|Tx>],<canId payload> [- HOST FILTERED],<annotation>"
    //           BusLogger prepends "[timestamp],[CAN]," before the disk write,
    //           producing the 5-column shape "[time],[CAN],[Rx],<canId>,<note>".
    //           The channel column was dropped per user request: in practice all
    //           real-world traces are single-channel and the constant "[chan 1],"
    //           prefix was wasted spreadsheet width. The annotation column is
    //           always present (empty string when off or annotator returns null)
    //           for stable column count.
    //
    // Collapse markers from BulkTransferCollapser follow the same two shapes:
    //   pretty  "[chan N] -- bulk transfer collapsed: N frames hidden --"
    //   csv     ",,-- bulk transfer collapsed: N frames hidden --"

    internal void LogTx(uint chId, ReadOnlySpan<byte> frame)
    {
        var sink = LogFrame;
        if (sink == null) return;
        if (frame.Length < CanFrame.IdBytes) return;
        var canId = CanFrame.ReadId(frame);
        var payload = CanFrame.Payload(frame);
        var (pretty, csv) = FormatFrame(chId, "Rx", canId, payload, hostFiltered: false);
        bool isTp = Common.Protocol.UdsAnnotator.IsTesterPresent(canId, payload);
        // Host-originated (Rx) frames are never DBC broadcasts - those only
        // flow host-ward through LogRx/LogRxFiltered.
        EmitFrame(chId, canId, payload, pretty, csv, isTp, isBroadcast: false, sink);
    }

    // isBroadcast: set by the caller when this frame was delivered via the
    // IFrameBroadcaster path (DBC scheduler OR a stack's UUDT stream, e.g.
    // FordUdsDispatch's $A0 DMR frames on 0x6A0). OR'd with the CAN-ID heuristic
    // so a configured DBC id is still tagged even if it ever logs by another
    // route. Either makes the UI "Hide broadcasts" filter drop the line.
    internal void LogRx(uint chId, ReadOnlySpan<byte> frame, bool isBroadcast = false)
    {
        var sink = LogFrame;
        if (sink == null) return;
        if (frame.Length < CanFrame.IdBytes) return;
        var canId = CanFrame.ReadId(frame);
        var payload = CanFrame.Payload(frame);
        var (pretty, csv) = FormatFrame(chId, "Tx", canId, payload, hostFiltered: false);
        bool isTp = Common.Protocol.UdsAnnotator.IsTesterPresent(canId, payload);
        EmitFrame(chId, canId, payload, pretty, csv, isTp, isBroadcast || IsConfiguredBroadcastId(canId), sink);
    }

    internal void LogRxFiltered(uint chId, ReadOnlySpan<byte> frame, bool isBroadcast = false)
    {
        var sink = LogFrame;
        if (sink == null) return;
        if (frame.Length < CanFrame.IdBytes) return;
        var canId = CanFrame.ReadId(frame);
        var payload = CanFrame.Payload(frame);
        var (pretty, csv) = FormatFrame(chId, "Tx", canId, payload, hostFiltered: true);
        bool isTp = Common.Protocol.UdsAnnotator.IsTesterPresent(canId, payload);
        EmitFrame(chId, canId, payload, pretty, csv, isTp, isBroadcast || IsConfiguredBroadcastId(canId), sink);
    }

    /// <summary>
    /// True when <paramref name="canId"/> matches a DBC broadcast message
    /// configured on any ECU (<see cref="EcuNode.Broadcasts"/>). A secondary
    /// heuristic behind the authoritative delivery-path flag (see LogRx) - it
    /// catches configured DBC ids; stack UUDT broadcasts (0x6A0, ...) are not
    /// in Broadcasts and rely on the flag. Diagnostic response IDs ($7E8/$5E8)
    /// never match.
    /// </summary>
    internal bool IsConfiguredBroadcastId(uint canId)
    {
        lock (nodesLock)
        {
            foreach (var n in nodes)
                foreach (var b in n.Broadcasts)
                    if (b.CanId == canId) return true;
        }
        return false;
    }

    // Width the byte field is padded to so annotation tags align in a column;
    // see the comment in FormatFrame for the derivation.
    private const int AnnotationColumn = 27;

    // Builds both the pretty (textbox) and csv (file) representations from
    // the same intermediate data so the annotator is invoked at most once.
    private (string pretty, string csv) FormatFrame(uint chId, string direction, uint canId, ReadOnlySpan<byte> payload, bool hostFiltered)
    {
        string bytes = $"{FormatId(canId)} {HexFormat.Bytes(payload)}";
        if (hostFiltered) bytes += " - HOST FILTERED";
        string? annotation = AnnotateFrames
            ? Common.Protocol.UdsAnnotator.Annotate(canId, payload)
            : null;
        string pretty;
        if (annotation != null)
        {
            // Pad the byte field to a fixed width so the "; <tag>" annotations
            // line up in a column across frames. AnnotationColumn covers an
            // 11-bit ID (3 hex) plus a full 8-byte CAN payload
            // ("7E0 07 23 00 01 00 C0 00 04" = 27 chars); wider frames (29-bit
            // IDs or the " - HOST FILTERED" suffix) just push their tag right.
            string field = bytes.Length < AnnotationColumn ? bytes.PadRight(AnnotationColumn) : bytes;
            pretty = $"[chan {chId}] {direction} {field}  ; {annotation}";
        }
        else
        {
            pretty = $"[chan {chId}] {direction} {bytes}";
        }
        string csv = $"[{direction}],{bytes},{annotation ?? ""}";
        return (pretty, csv);
    }

    private void EmitFrame(uint chId, uint canId, ReadOnlySpan<byte> payload, string pretty, string csv, bool isTesterPresent, bool isBroadcast, Action<string, string, bool, bool> sink)
    {
        if (collapseBulkTransfers)
            bulkCollapser.Process(chId, canId, payload, pretty, csv, isTesterPresent, isBroadcast, sink);
        else
            sink(pretty, csv, isTesterPresent, isBroadcast);
    }

    private static string FormatId(uint id)
        => id <= 0x7FF ? id.ToString("X3") : id.ToString("X8");

    public VirtualBus()
    {
        Scheduler          = new DpidScheduler(this);
        Ticker             = new TesterPresentTicker(this, Scheduler);
        IdleSupervisor     = new IdleBusSupervisor(this, Scheduler);
        BroadcastScheduler = new BroadcastScheduler(this);
    }

    /// <summary>Snapshot copy - safe to enumerate cross-thread.</summary>
    public IReadOnlyList<EcuNode> Nodes
    {
        get { lock (nodesLock) return nodes.ToArray(); }
    }

    public void AddNode(EcuNode node)
    {
        lock (nodesLock) nodes.Add(node);
        NodesChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool RemoveNode(EcuNode node)
    {
        bool removed;
        lock (nodesLock) removed = nodes.Remove(node);
        if (!removed) return false;
        Scheduler.Stop(node, Array.Empty<byte>());
        NodesChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void ReplaceNodes(IEnumerable<EcuNode> newNodes)
    {
        EcuNode[] toStop;
        lock (nodesLock)
        {
            toStop = nodes.ToArray();
            nodes.Clear();
            nodes.AddRange(newNodes);
        }
        foreach (var n in toStop) Scheduler.Stop(n, Array.Empty<byte>());
        NodesChanged?.Invoke(this, EventArgs.Empty);
    }

    public EcuNode? FindByRequestId(uint canId)
    {
        lock (nodesLock) return nodes.FirstOrDefault(n => n.PhysicalRequestCanId == canId);
    }

    public void DispatchHostTx(ReadOnlySpan<byte> frame, ChannelSession ch)
    {
        if (frame.Length < CanFrame.IdBytes + 1) return;

        LogTx(ch.Id, frame);

        uint canId = CanFrame.ReadId(frame);
        var data = CanFrame.Payload(frame);

        if (canId == GmlanCanId.AllNodesRequest)
        {
            DispatchFunctional(data, ch);
            return;
        }
        if (canId == GmlanCanId.Obd2FunctionalRequest)
        {
            // ISO 15765-4 functional broadcast. Normal addressing - no extAddr
            // byte to consume. Modern GM ECUs accept both this AND $101+$FE.
            DispatchObd2Functional(data, ch);
            return;
        }

        var node = FindByRequestId(canId);
        if (node == null)
        {
            var msg = $"[bus] no ECU at {FormatId(canId)} -- frame dropped";
            LogFrame?.Invoke(msg, msg, false, false);
            return;
        }

        // FlowControl frames inbound from the host are for OUR fragmenter (the
        // host is the receiver of an in-flight ECU response). Route to the
        // node's fragmenter instead of letting the reassembler discard them.
        // §9.6.5 - FC carries FS / BS / STmin in bytes 1..3 of the data field.
        if (data.Length >= 3 && (data[0] >> 4) == 0x3)
        {
            var fs = (Common.IsoTp.FlowStatus)(byte)(data[0] & 0x0F);
            byte bsByte = data[1];
            byte stMinByte = data[2];
            node.State.Fragmenter.OnFlowControl(fs, bsByte, stMinByte);
            return;
        }

        var assembled = node.State.Reassembler.Feed(data, (bs, st) =>
        {
            var fc = new byte[CanFrame.IdBytes + 3];
            CanFrame.WriteId(fc, node.UsdtResponseCanId);
            fc[CanFrame.IdBytes + 0] = (byte)PciType.FlowControl;
            fc[CanFrame.IdBytes + 1] = bs;
            fc[CanFrame.IdBytes + 2] = st;
            ch.EnqueueRx(new PassThruMsg { ProtocolID = ProtocolID.CAN, Data = fc });
        }, node.FlowControlBlockSize, fcSeparationTime: 0);

        if (assembled == null) return;
        // Tag physical requests with the stack the request CAN ID implies.
        // Real E38/E67 silicon routes each CAN ID to exactly one dispatcher;
        // future per-handler stack gates use this to NRC services that
        // aren't exposed on the caller's stack.
        var stack = DiagnosticStackClassifier.StackForCanId(canId);
        DispatchUsdt(node, assembled, ch, isFunctional: false, canId, stack);
    }

    private void DispatchFunctional(ReadOnlySpan<byte> data, ChannelSession ch)
    {
        if (data.Length < 3) return;
        byte extAddr = data[0];
        byte pci = data[1];
        if ((pci >> 4) != 0) return;
        int len = pci & 0x0F;
        if (len < 1 || len > data.Length - 2) return;
        if (extAddr != GmlanCanId.AllNodesExtAddr) return;
        var payload = data.Slice(2, len);

        // $101 + $FE extAddr is GMLAN's AllNodes functional broadcast - on real
        // E38/E67 silicon this routes through the OBD-II/UDS dispatcher (the
        // RE survey of CAN ID $101 confirmed it). Tag accordingly.
        EcuNode[] snapshot;
        lock (nodesLock) snapshot = nodes.ToArray();
        foreach (var node in snapshot)
            DispatchUsdt(node, payload, ch, isFunctional: true, GmlanCanId.AllNodesRequest, DiagnosticStack.Uds);
    }

    /// <summary>
    /// ISO 15765-4 OBD-II functional broadcast at CAN ID $7DF. Normal addressing,
    /// so data[0] is the PCI byte (no extAddr byte to consume). Only Single
    /// Frame is meaningful for functional broadcast - multi-frame to many
    /// receivers would need per-receiver FlowControl, which the standard
    /// doesn't define for $7DF. Dispatches the SF payload to every ECU with
    /// isFunctional=true, so existing per-service gates (e.g. Service3EHandler
    /// accepting both physical and functional, Service22Handler returning
    /// early on functional, etc.) behave correctly.
    /// </summary>
    private void DispatchObd2Functional(ReadOnlySpan<byte> data, ChannelSession ch)
    {
        if (data.Length < 2) return;
        byte pci = data[0];
        if ((pci >> 4) != 0) return;                           // SF only
        int len = pci & 0x0F;
        if (len < 1 || len > data.Length - 1) return;
        var payload = data.Slice(1, len);

        EcuNode[] snapshot;
        lock (nodesLock) snapshot = nodes.ToArray();
        foreach (var node in snapshot)
            DispatchUsdt(node, payload, ch, isFunctional: true, GmlanCanId.Obd2FunctionalRequest, DiagnosticStack.Uds);
    }

    private void DispatchUsdt(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch, bool isFunctional, uint canId, DiagnosticStack stack)
    {
        if (usdt.Length < 1) return;
        byte sid = usdt[0];
        // Bin-replay start trigger: first $22 / $AA after a host connects.
        // CAS-only inside MaybeStart; safe under concurrent dispatch on
        // multiple ECUs from different IPC pipe threads.
        if (sid == Service.ReadDataByParameterIdentifier
            || sid == Service.ReadDataByPacketIdentifier)
            Replay?.MaybeStart(NowMs);

        // Exception isolation: a buggy waveform / security algorithm /
        // value-codec must not crash the bus thread or leave the channel
        // unusable. Any throw inside a handler is logged and translated to a
        // generic NRC $22 (CNCRSE - "ECU not in a state to perform this
        // service"). Physical requests get the NRC; functional broadcasts stay
        // silent on error per §6.x convention. The inner try guards against
        // the fragmenter ALSO throwing (e.g. if state is corrupted), so the
        // catch can never raise a second-order exception.
        try
        {
            // Wire the response pacing for this request onto the node's
            // fragmenter BEFORE any handler enqueues a response. Every
            // fragmenter-routed USDT response (positive, NRC, RAM-read-zeros, the
            // dispatch-error fallback) then honours the node's ResponseDelayMs
            // against the resolved stack's P2 / P2* (Core/Services/ResponseTiming).
            // A disabled/exempt request clears the pacing so behaviour is
            // byte-identical to the historic immediate send. Benign under
            // concurrent dispatch because a node's pacing is constant. The $AA
            // periodic UUDT stream, the DBC broadcasts, and inbound-FC emission
            // all bypass the fragmenter, so none of them are paced.
            ApplyResponsePacing(node, canId, isFunctional);

            // User-defined Mode23 row: a $23 ReadMemoryByAddress whose address
            // exactly matches a configured Mode23 PID row is answered from that
            // row's value source (static bytes / waveform / signal). Highest
            // precedence - an explicit row is the user saying "this address
            // returns exactly this", so it wins over the loaded flash bin AND the
            // RAM-read-zeros fallback. Runs before stack dispatch so it applies to
            // every stack that carries $23 (GMW3110 and Ford UDS); a request that
            // matches no row falls through to the bin / stack path unchanged.
            if (sid == 0x23 && TryAnswerMemoryReadFromRows(node, usdt, ch, NowMs))
                return;

            // RAM-read fallback (per-ECU opt-in): a $23 ReadMemoryByAddress
            // targeting an address beyond the loaded flash bin (RAM) gets a
            // positive zero-filled reply instead of NRC $31 RequestOutOfRange.
            // Runs before stack dispatch so it applies to every stack;
            // in-bin reads fall through to the owning stack's own handler.
            if (sid == 0x23 && node.RamReadReturnsZeros
                && TryAnswerRamReadWithZeros(node, usdt, ch))
            {
                return;
            }

            // Every node dispatches through the protocol-stack model (the persona dispatch path
            // and CommonServices are retired). Resolve (canId, sid) -> the binding that owns it;
            // its stack handles the request (positive or a stack-correct NRC). A node mid-SPS-
            // kernel-handover has its Stacks replaced by the kernel binding, so this same path
            // serves it. There is no cross-stack fallback.
            var binding = node.Resolve(canId, sid);
            if (binding is not null
                && binding.Stack.Dispatch(node, usdt, ch, isFunctional, NowMs, new StackContext(Scheduler, stack, binding)))
                return;
            // No binding owns the SID, or the owning stack declined (owned-but-unhandled, e.g. GM
            // $12/$23). Physical -> the primary stack's serviceNotSupported NRC; functional
            // broadcast stays silent.
            if (!isFunctional)
            {
                byte nrc = node.PrimaryForCanId(canId)?.Stack.Nrc.ServiceNotSupported ?? Nrc.ServiceNotSupported;
                ServiceUtil.EnqueueNrc(node, ch, sid, nrc);
            }
        }
        catch (Exception ex)
        {
            LogSim?.Invoke(
                $"[bus] dispatch error on ECU '{node.Name}' SID 0x{sid:X2}: {ex.GetType().Name}: {ex.Message}");
            if (!isFunctional)
            {
                try
                {
                    node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
                        new byte[] { Service.NegativeResponse, sid, Nrc.ConditionsNotCorrectOrSequenceError });
                }
                catch (Exception ex2)
                {
                    LogSim?.Invoke(
                        $"[bus] failed to send fallback NRC for SID 0x{sid:X2}: {ex2.GetType().Name}: {ex2.Message}");
                }
            }
        }
    }

    // Set (or clear) the node fragmenter's response pacing for the request about
    // to be dispatched. Pace only solicited PHYSICAL responses from a baseline
    // (non-kernel) ECU:
    //   - delay <= 0 (the default): feature off - byte-identical immediate send.
    //   - functional broadcast: a real ECU answers $7DF/$101 promptly, and a 78
    //     RCR-RP to a broadcast many ECUs share is non-standard; keep it immediate
    //     so e.g. the ForScan $22 0200 probe is unchanged.
    //   - kernel mode: the transient SPS kernel must answer promptly (the flasher
    //     polls $31 CheckMemory tightly); it does not honour the generic latency knob.
    // Set just-in-time on the shared per-node fragmenter; under concurrent dispatch
    // the only per-request variation is functional (null) vs physical (the same
    // node-constant record), so a stomp at worst paces or skips one response
    // differently - harmless, and the fragmenter still serialises the pace itself.
    private void ApplyResponsePacing(EcuNode node, uint canId, bool isFunctional)
    {
        var frag = node.State.Fragmenter;
        int delay = node.ResponseDelayMs;
        if (delay <= 0 || isFunctional || node.InKernelMode) { frag.Pacing = null; return; }
        frag.Pacing = new ResponsePacing
        {
            ProcessingMs = delay,
            Timing = node.EffectiveTiming(canId),
            Emit78 = node.Emit78WhenSlow,
            Log = LogSim,
        };
    }

    // Answer a $23 ReadMemoryByAddress targeting RAM with a positive $63 reply
    // of <len> zero bytes. Returns true when handled, false when the request
    // isn't the recognised 7-byte ReadMemoryByAddress shape or the address
    // range lies wholly inside the loaded flash bin (in which case the Ford
    // dispatch serves the real bytes). Request layout (the Ford UDS $23 wire format):
    //   23 <4-byte BE address> <2-byte BE length>   (7 bytes total)
    // "RAM" = any byte of the requested range lies at or past the bin length;
    // with no bin loaded the bin length is 0, so every address is RAM.
    // Serve a $23 ReadMemoryByAddress from a user-defined Mode23 PID row (any
    // stack). The request is `23 <4B BE addr> <2B BE len>` (7 bytes); a row whose
    // Address equals addr answers it, with the row's value written into a
    // len-sized payload via Pid.WriteResponseBytes (truncated or zero-padded to
    // len, exactly like the $22 / $2D paths). Returns false - falling through to
    // the bin / RAM-zeros / stack path - when the request isn't the expected
    // shape, len is zero, or no row claims the address.
    private static bool TryAnswerMemoryReadFromRows(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch, double nowMs)
    {
        if (usdt.Length != 7) return false;
        uint addr = (uint)((usdt[1] << 24) | (usdt[2] << 16) | (usdt[3] << 8) | usdt[4]);
        ushort len = (ushort)((usdt[5] << 8) | usdt[6]);
        if (len == 0) return false;

        var pid = node.GetMemoryReadPid(addr);
        if (pid is null) return false;

        var reply = new byte[1 + len];
        reply[0] = 0x63;                          // 0x23 | 0x40 positive response
        pid.WriteResponseBytes(nowMs, reply.AsSpan(1));   // fills len bytes (truncate / zero-pad)
        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId, reply);
        return true;
    }

    private static bool TryAnswerRamReadWithZeros(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch)
    {
        if (usdt.Length != 7) return false;
        uint addr = (uint)((usdt[1] << 24) | (usdt[2] << 16) | (usdt[3] << 8) | usdt[4]);
        ushort len = (ushort)((usdt[5] << 8) | usdt[6]);
        if (len == 0) return false;

        uint binLength = (uint)Core.Protocol.FordUdsDispatch.FlashBinSize;
        // Wholly inside the bin -> not RAM; let the Ford dispatch serve real bytes.
        // ulong guards against addr+len wrapping for addresses near uint.MaxValue.
        if ((ulong)addr + len <= binLength) return false;

        var reply = new byte[1 + len];
        reply[0] = 0x63;                       // 0x23 | 0x40 positive response
        // reply[1..] stays zero-initialised - the zero-filled RAM payload.
        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId, reply);
        return true;
    }
}

using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Services;

namespace Core.Protocol;

// The built protocol-stack singletons and the synthesis that gives every EcuNode its Stacks
// binding list. Each stack (J1979, GMW3110, UDS, plus the transient UDS-kernel) carries a
// DispatchFn that wraps its dispatch logic; VirtualBus.DispatchUsdt routes every node through
// EcuNode.Resolve -> StackBinding -> the stack's DispatchFn. SynthesizeFor builds the per-ECU
// bindings from PersonaId + CAN ids.
public static class ProtocolStacks
{
    public static readonly IProtocolStack J1979 = new ProtocolStack(
        "J1979", StandardCatalogs.J1979, NrcProfile.Gm, SessionModel.Gm, TimingProfile.Gm,
        new AddressingModel(Request: 0x7E0, Response: 0x7E8, Functional: AddressingModel.Obd2Functional),
        DispatchJ1979);

    public static readonly IProtocolStack Gmw3110 = new ProtocolStack(
        "GMW3110", StandardCatalogs.Gmw3110, NrcProfile.Gm, SessionModel.Gm, TimingProfile.Gm,
        new AddressingModel(Request: 0x7E0, Response: 0x7E8,
                            Functional: AddressingModel.Obd2Functional, FunctionalAlt: AddressingModel.GmlanFunctional),
        DispatchGmw3110);

    public static readonly IProtocolStack Uds = new ProtocolStack(
        "UDS", StandardCatalogs.Uds, NrcProfile.Uds, SessionModel.Uds, TimingProfile.Uds,
        new AddressingModel(Request: 0x7E0, Response: 0x7E8, Functional: AddressingModel.Obd2Functional),
        DispatchUds);

    // The Ford capture persona's stack: same dispatch as UDS (FordUdsDispatch + the shared $22),
    // same UDS NRC/session/timing vocabulary, but a COMPREHENSIVE catalog (J1979 OBD + UDS gospel +
    // Ford-proprietary $A0/$A1/$B1) so the editor checklist shows everything the persona presents.
    // A Ford node binds this CatchAll; the catalog drives both the editor list and the in-dispatch
    // allow-list gate, so every shown service is tickable and an unticked one NRC-$11s.
    public static readonly IProtocolStack Ford = new ProtocolStack(
        "Ford", StandardCatalogs.Ford, NrcProfile.Uds, SessionModel.Uds, TimingProfile.Uds,
        new AddressingModel(Request: 0x7E0, Response: 0x7E8, Functional: AddressingModel.Obd2Functional),
        DispatchUds);

    // The SPS programming kernel boot-loaded via $36 sub $80. Narrow set - real kernels answer
    // only a handful of services; anything else NRC-$11s (the kernel is authoritative once it
    // replaces the baseline stacks). $22 is included because the old persona path served it via
    // CommonServices during kernel mode.
    public static readonly IProtocolStack UdsKernel = new ProtocolStack(
        "UDS-Kernel", ServiceCatalog.Of(
            (0x20, "返回正常模式"), (0x22, "按标识符读取数据"), (0x31, "例程控制"),
            (0x34, "请求下载"), (0x35, "请求上传"), (0x36, "传输数据"), (0x3E, "测试仪在线")),
        NrcProfile.Gm, SessionModel.Gm, TimingProfile.Gm,
        new AddressingModel(Request: 0x7E0, Response: 0x7E8, Functional: AddressingModel.Obd2Functional),
        DispatchUdsKernel);

    // ---- Dispatch wiring -------------------------------------------------------------------
    // The GMW3110 stack's dispatch IS the Gmw3110Dispatch SID switch, reached as this stack's
    // dispatch implementation. There is no per-handler stack gate: the OBD-vs-enhanced split is
    // structural (SynthesizeFor binds only the restricted enhanced set on non-OBD ids), so
    // ctx.Stack is passed through but not consulted here.
    private static bool DispatchGmw3110(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch,
                                        bool isFunctional, double nowMs, StackContext ctx)
        => Gmw3110Dispatch.Instance.Dispatch(node, usdt, ch, isFunctional, usdt[0], nowMs, ctx.Scheduler, ctx.Stack);

    // The UDS stack's dispatch IS the Ford PCMTec capture dispatch: it logs every request, answers
    // the Ford whitelist ($01/$09 via the shared J1979 handlers, $23/$27/$A0/$A1/$B1/$34/$36/$37/$11/$3E),
    // and declines the rest.
    // $22 (formerly CommonServices) is handled here so the stack owns it. A Ford node binds this
    // stack CatchAll so every SID - including ones outside the UDS catalog - reaches the logger.
    // Because CatchAll bypasses Owns's allow-list check, the per-service editor checklist can't gate
    // at Resolve time the way the GM stacks do; instead we hand the binding's Enabled allow-list to
    // the dispatch (ctx.Binding.Enabled) so an unticked UDS service NRC-$11s in-dispatch (still
    // logged first). Proprietary SIDs outside the UDS catalog are never gated.
    private static bool DispatchUds(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch,
                                    bool isFunctional, double nowMs, StackContext ctx)
    {
        if (FordUdsDispatch.Instance.Dispatch(node, usdt, ch, isFunctional, usdt[0], nowMs,
                                              ctx.Scheduler, ctx.Stack, ctx.Binding?.Enabled))
            return true;
        if (usdt[0] == Service.ReadDataByParameterIdentifier)   // $22 - was CommonServices
        {
            Service22Handler.Handle(node, usdt, ch, nowMs, isFunctional);
            return true;
        }
        return false;   // -> VirtualBus NRCs $11 (physical); functional already returned true above
    }

    // The SPS-kernel dispatch IS the UdsKernelDispatch switch ($31/$3E/$20/$34/$36), with $22
    // (formerly CommonServices) handled here so the kernel catalog owns it.
    private static bool DispatchUdsKernel(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch,
                                          bool isFunctional, double nowMs, StackContext ctx)
    {
        if (usdt[0] == Service.ReadDataByParameterIdentifier)   // $22 - was CommonServices
        {
            Service22Handler.Handle(node, usdt, ch, nowMs, isFunctional);
            return true;
        }
        return UdsKernelDispatch.Instance.Dispatch(node, usdt, ch, isFunctional, usdt[0], nowMs, ctx.Scheduler, ctx.Stack);
    }

    // The transient binding pushed by Service36Handler on $36 sub $80, on the ECU's current CAN
    // ids. EcuNode.EnterKernelMode replaces Stacks with this until $20 / P3C timeout.
    public static StackBinding KernelBindingFor(EcuNode node) => new(UdsKernel,
        new AddressingModel(Request: node.PhysicalRequestCanId, Response: node.UsdtResponseCanId,
                            Functional: AddressingModel.Obd2Functional, FunctionalAlt: AddressingModel.GmlanFunctional,
                            Uudt: node.UudtResponseCanId),
        AllServices.Instance);

    // J1979 owns $01..$0A; $01 (ShowCurrentData) and $09 (RequestVehicleInformation) have handlers, the rest decline
    // and the bus NRCs $11 (physical) / stays silent (functional). The "$01 only on the OBD dispatcher" gate is now
    // STRUCTURAL (step 3): J1979 is bound only on OBD-classified nodes, so a non-OBD GM node has no J1979 binding and
    // $01 falls to NRC $11 via Resolve. Legislated OBD is identical across makes, so these handlers are SHARED: GM
    // reaches them through this binding, and the ford-uds capture dispatch delegates $01/$09 to the same
    // Service01Handler / Service09Handler (FordUdsDispatch) rather than carrying its own copies.
    private static bool DispatchJ1979(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch,
                                      bool isFunctional, double nowMs, StackContext ctx)
    {
        switch (usdt[0])
        {
            case Service.Obd01ShowCurrentData:
                Service01Handler.Handle(node, usdt, ch, nowMs, isFunctional);
                return true;
            case Service.RequestVehicleInformation:   // $09 VIN / CalID, identity-sourced
                Service09Handler.Handle(node, usdt, ch, isFunctional);
                return true;
            default:
                return false;
        }
    }

    // Resolve a config / persistence standard name to the built stack. Open registry (DESIGN
    // doc section 6): adding a standard = registering it here.
    public static IProtocolStack? ByName(string standard) => standard switch
    {
        "J1979"   => J1979,
        "GMW3110" => Gmw3110,
        "UDS"     => Uds,
        "Ford"    => Ford,
        _         => null,
    };

    // The GMLAN enhanced-diag dispatcher's SID set on real E38/E67 silicon (9 SIDs): the GM
    // services reachable on BOTH dispatchers. The OBD-only SIDs ($01,$10,$22,$2C,$2D,$3B,$AA,
    // $AE) are deliberately absent, so a node bound with only this set NRC-$11s them - which is
    // exactly what the old RequireUdsStack gate did for a non-OBD request (DESIGN doc section 5,
    // step 3). Used as the allow-list for a GM node configured on a non-OBD (enhanced) CAN id.
    public static readonly byte[] GmlanEnhancedDispatcherSids =
        { 0x1A, 0x20, 0x27, 0x28, 0x34, 0x36, 0x3E, 0xA2, 0xA5 };

    // Synthesize the binding list for an ECU from its current persona + CAN ids. Real E38/E67
    // silicon demuxes by CAN id (the dual dispatcher): the OBD ids reach the full GMW3110 set +
    // J1979; the GMLAN enhanced-diag ids reach only the restricted 9-SID set. Our EcuNode carries
    // one physical id, so we model whichever dispatcher that id selects. Catalog membership keeps
    // the wildcards honest (J1979 owns $01; the "*" GMW3110 binding does not, $01 not being in the
    // GMW3110 catalog).
    public static IList<StackBinding> SynthesizeFor(EcuNode node)
    {
        // Every binding listens on the ECU's physical request id plus the OBD/GMLAN functional
        // ids; the UUDT id rides along for $AA streams.
        var canIds = new AddressingModel(
            Request: node.PhysicalRequestCanId, Response: node.UsdtResponseCanId,
            Functional: AddressingModel.Obd2Functional, FunctionalAlt: AddressingModel.GmlanFunctional,
            Uudt: node.UudtResponseCanId);

        // Ford ECU: a single catch-all capture binding on the comprehensive Ford catalog (OBD +
        // UDS + Ford-proprietary). No separate J1979 binding - the OBD modes live in the Ford catalog
        // and CatchAll routes every SID to the logger; the Ford dispatch delegates the legislated OBD
        // modes ($01/$09) to the SAME shared J1979 handlers GM uses (FordUdsDispatch).
        // The catalog is what the editor checklist renders and what the in-dispatch allow-list gate
        // consults, so every presented service is tickable.
        if (node.PersonaId == "ford-uds")
        {
            return new List<StackBinding>
            {
                new StackBinding(Ford, canIds, AllServices.Instance) { CatchAll = true },
            };
        }

        // GM ECU on an OBD-classified id ($7E0..$7E7): the OBD dispatcher - full GMW3110 + J1979.
        if (DiagnosticStackClassifier.StackForCanId(node.PhysicalRequestCanId) == DiagnosticStack.Uds)
        {
            return new List<StackBinding>
            {
                new(J1979, canIds, AllServices.Instance),
                new(Gmw3110, canIds, AllServices.Instance),
            };
        }

        // GM ECU on a non-OBD (GMLAN enhanced-diag) id: the enhanced dispatcher - the restricted
        // 9-SID set only, no J1979, no OBD-only SIDs. Reproduces the old RequireUdsStack gate
        // structurally.
        return new List<StackBinding>
        {
            new(Gmw3110, canIds, new SidAllowList(GmlanEnhancedDispatcherSids)),
        };
    }
}

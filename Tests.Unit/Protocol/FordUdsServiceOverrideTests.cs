using Common.PassThru;
using Common.Protocol;
using Common.Persistence;
using Core.Bus;
using Core.Ecu;
using Core.Persistence;
using Core.Protocol;
using Core.Scheduler;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Protocol;

// The Ford capture stack exposes the SAME per-service checklist the GM stacks do, over the
// comprehensive Ford catalog (OBD modes + UDS services + Ford-proprietary $A0/$A1/$B1) - so
// everything the persona presents is listed and tickable, and an unticked service NRC-$11s. The
// wrinkle is that the Ford binding is CatchAll (it must SEE every SID to log it), so the allow-list
// can't gate at Resolve time the way the GM stacks do - Resolve always returns the catch-all binding.
// Instead DispatchUds hands the binding's Enabled allow-list to FordUdsDispatch, which NRC-$11s an
// unticked Ford-catalog SID in-dispatch (after logging it). A SID outside the catalog entirely (a
// probe like $99) is never gated. These tests lock the gate at the dispatch level, end-to-end through
// the bus, and the EcuDto.Stacks round-trip.
//
// Collection-serialised with the other Ford tests: FordUdsDispatch owns process-wide static state
// (the per-session log file, the flash bin, the DMR slot map).
[Collection(FordUdsPersonaCollection.Name)]
public sealed class FordUdsServiceOverrideTests
{
    private const byte Sid09 = 0x09;   // RequestVehicleInformation - OBD, in the Ford catalog
    private const byte Sid22 = 0x22;   // ReadDataByIdentifier      - UDS, in the Ford catalog
    private const byte Sid27 = 0x27;   // SecurityAccess            - UDS, in the Ford catalog
    private const byte SidA1 = 0xA1;   // Ford DMR setup            - proprietary, in the Ford catalog

    private static (EcuNode node, ChannelSession ch) FordNode()
    {
        var node = NodeFactory.CreateNode();
        node.PersonaId = "ford-uds";
        return (node, NodeFactory.CreateChannel());
    }

    // The Ford catalog minus one SID - the explicit allow-list the editor stores when the user unticks it.
    private static SidAllowList FordCatalogWithout(byte sid)
        => new(StandardCatalogs.Ford.Sids.Where(s => s != sid));

    private static byte[] WrapCanFrame(uint canId, byte[] data)
    {
        var f = new byte[4 + data.Length];
        f[0] = (byte)((canId >> 24) & 0xFF);
        f[1] = (byte)((canId >> 16) & 0xFF);
        f[2] = (byte)((canId >> 8) & 0xFF);
        f[3] = (byte)(canId & 0xFF);
        data.CopyTo(f, 4);
        return f;
    }

    // ---- The catalog lists everything the persona presents ----

    [Fact]
    public void FordCatalog_ContainsObd_Uds_And_Proprietary()
    {
        var sids = StandardCatalogs.Ford.Sids;
        Assert.Contains((byte)0x09, sids);   // OBD
        Assert.Contains((byte)0x22, sids);   // UDS
        Assert.Contains((byte)0x27, sids);   // UDS
        Assert.Contains((byte)0xA0, sids);   // Ford proprietary
        Assert.Contains((byte)0xA1, sids);
        Assert.Contains((byte)0xB1, sids);
        // The full UDS gospel is present too: the catalog is the full MENU the user ticks support
        // from, so even services FordUdsDispatch has no handler for are listed (ticking one and then
        // requesting it NRC-$11s, like GM's ticked-but-unimplemented entries).
        foreach (var s in StandardCatalogs.Uds.Sids) Assert.Contains(s, sids);
    }

    // ---- Dispatch-level gate (the allow-list is passed explicitly) ----

    [Fact]
    public void UntickedUdsSid_NrcsServiceNotSupported()
    {
        // $27 is in the Ford catalog. Unticked (absent from the allow-list) -> NRC $11 instead of a
        // seed, and the security module is NOT installed (we never reach the $27 handler).
        var (node, ch) = FordNode();
        Assert.Null(node.SecurityModule);

        bool claimed = FordUdsDispatch.Instance.Dispatch(
            node, new byte[] { 0x27, 0x01 }, ch, isFunctional: false, sid: 0x27, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()), stack: DiagnosticStack.Uds,
            enabled: FordCatalogWithout(Sid27));

        Assert.True(claimed);
        Assert.Null(node.SecurityModule);
        Assert.Equal(new byte[] { Service.NegativeResponse, 0x27, Nrc.ServiceNotSupported },
            TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void UntickedObdSid_NrcsServiceNotSupported()
    {
        // OBD $09 is in the Ford catalog too - unticking it makes the ECU NRC $11 a Mode 09 request.
        var (node, ch) = FordNode();
        FordUdsDispatch.LoadFlashBin((byte[]?)null);

        FordUdsDispatch.Instance.Dispatch(
            node, new byte[] { 0x09, 0x02 }, ch, isFunctional: false, sid: 0x09, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()), stack: DiagnosticStack.Uds,
            enabled: FordCatalogWithout(Sid09));

        Assert.Equal(new byte[] { Service.NegativeResponse, 0x09, Nrc.ServiceNotSupported },
            TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void TickedUdsSid_StillAnswered()
    {
        // A restrictive allow-list that KEEPS $27 (drops $22) still runs the seed flow.
        var (node, ch) = FordNode();

        bool claimed = FordUdsDispatch.Instance.Dispatch(
            node, new byte[] { 0x27, 0x01 }, ch, isFunctional: false, sid: 0x27, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()), stack: DiagnosticStack.Uds,
            enabled: FordCatalogWithout(Sid22));

        Assert.True(claimed);
        Assert.NotNull(node.SecurityModule);
        Assert.Equal(0x67, TestFrame.DequeueSingleFrameUsdt(ch)[0]);   // positive SecurityAccess response
    }

    [Fact]
    public void ProprietarySid_IsGateable()
    {
        // $A1 is now IN the Ford catalog, so it gates like any other service: unticked -> NRC $11.
        var (node, ch) = FordNode();
        byte[] a1 = { 0xA1, 0x01, 0x8C, 0x00, 0x3F, 0x7F, 0xA0 };

        FordUdsDispatch.Instance.Dispatch(
            node, a1, ch, isFunctional: false, sid: 0xA1, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()), stack: DiagnosticStack.Uds,
            enabled: FordCatalogWithout(SidA1));

        Assert.Equal(new byte[] { Service.NegativeResponse, 0xA1, Nrc.ServiceNotSupported },
            TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void ProprietarySid_TickedOrUngated_StillAnswered()
    {
        // With the SID allowed (here: no allow-list at all -> the legacy direct-call path) $A1 answers.
        var (node, ch) = FordNode();
        byte[] a1 = { 0xA1, 0x01, 0x8C, 0x00, 0x3F, 0x7F, 0xA0 };

        FordUdsDispatch.Instance.Dispatch(
            node, a1, ch, isFunctional: false, sid: 0xA1, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()), stack: DiagnosticStack.Uds);

        Assert.Equal(new byte[] { 0xE1, 0x01 }, TestFrame.DequeueSingleFrameUsdt(ch));
    }

    // ---- End-to-end through the bus (locks the StackContext.Binding wiring) ----

    [Fact]
    public void Bus_UntickedSid_Nrcs11()
    {
        // The override the editor would store; drives a real $27 through VirtualBus so the binding's
        // Enabled reaches the dispatch via StackContext.Binding.
        var (node, ch) = FordBusNode();
        node.SetServiceOverride("Ford", FordCatalogWithout(Sid27));

        ch.Bus!.DispatchHostTx(WrapCanFrame(NodeFactory.PhysReq, new byte[] { 0x02, 0x27, 0x01 }), ch);

        Assert.Equal(new byte[] { Service.NegativeResponse, 0x27, Nrc.ServiceNotSupported },
            TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void Bus_ProprietarySid_AnsweredByDefault()
    {
        // No override -> every Ford-catalog service ticked -> $A1 answered end-to-end.
        var (node, ch) = FordBusNode();

        ch.Bus!.DispatchHostTx(
            WrapCanFrame(NodeFactory.PhysReq, new byte[] { 0x07, 0xA1, 0x01, 0x8C, 0x00, 0x3F, 0x7F, 0xA0 }), ch);

        Assert.Equal(new byte[] { 0xE1, 0x01 }, TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void Bus_ProprietarySid_GatedByOverride()
    {
        // Untick $A1 -> NRC $11 end-to-end, proving the proprietary services gate like the rest.
        var (node, ch) = FordBusNode();
        node.SetServiceOverride("Ford", FordCatalogWithout(SidA1));

        ch.Bus!.DispatchHostTx(
            WrapCanFrame(NodeFactory.PhysReq, new byte[] { 0x07, 0xA1, 0x01, 0x8C, 0x00, 0x3F, 0x7F, 0xA0 }), ch);

        Assert.Equal(new byte[] { Service.NegativeResponse, 0xA1, Nrc.ServiceNotSupported },
            TestFrame.DequeueSingleFrameUsdt(ch));
    }

    private static (EcuNode node, ChannelSession ch) FordBusNode()
    {
        var node = NodeFactory.CreateNode();
        node.PersonaId = "ford-uds";
        var bus = new VirtualBus();
        bus.AddNode(node);
        var ch = new ChannelSession { Id = 1, Protocol = ProtocolID.CAN, Baud = 500_000, Bus = bus };
        return (node, ch);
    }

    // ---- EcuDto.Stacks round-trip (render-full / store-delta, same contract as GM) ----

    [Fact]
    public void Override_RoundTripsAsFordDelta()
    {
        var node = NodeFactory.CreateNode();
        node.PersonaId = "ford-uds";
        node.SetServiceOverride("Ford", FordCatalogWithout(Sid27));

        var dto = ConfigStore.EcuDtoFrom(node);
        var ford = Assert.Single(dto.Stacks!);
        Assert.Equal("Ford", ford.Standard);
        Assert.NotNull(ford.Services);
        Assert.DoesNotContain("0x27", ford.Services!);   // the unticked SID is absent from the delta
        Assert.Contains("0x22", ford.Services!);

        var reloaded = ConfigStore.EcuNodeFrom(dto);
        var binding = Assert.Single(reloaded.Stacks);
        Assert.True(binding.CatchAll);                  // still the capture stack
        Assert.False(binding.Enabled.Allows(Sid27));    // gate will NRC $27
        Assert.True(binding.Enabled.Allows(Sid22));     // $22 still answered
    }

    [Fact]
    public void NoOverride_IsQuiet()
    {
        var node = NodeFactory.CreateNode();
        node.PersonaId = "ford-uds";
        Assert.Null(ConfigStore.EcuDtoFrom(node).Stacks);   // a plain Ford config carries no stacks[]
    }
}

using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Protocol;
using Core.Scheduler;
using Core.Utilities;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Ecu;

// FordUdsPersona covers:
//   - A SID the persona has no handler for is logged then DECLINED (Dispatch returns false, emits
//     nothing) so the bus NRC-$11s it like a real PCM - whether the SID is IN the Ford catalog
//     (ticked-but-unimplemented, e.g. $10) or OUTSIDE it ("not present", e.g. $99). Catalog membership
//     is an internal modelling artifact with no on-wire correlate. ($22 is the one in-catalog decline
//     the bus routes to Service22Handler instead of NRC'ing.) (End-to-end bus behaviour, including the
//     shared $22 path and the not-present NRC path, is in CommonServicesDispatchTests.)
//   - Persona logs to a file (best-effort, validated by checking that the
//     file path is non-null after Dispatch).
//   - Functional broadcasts produce no response (spec).
//
// Test pattern matches Service22HandlerTests: build a node + channel, call
// Dispatch directly, drain the response queue.
[Collection(FordUdsPersonaCollection.Name)]
public sealed class FordUdsPersonaTests
{
    private static (Core.Ecu.EcuNode node, ChannelSession ch) MakeNodeWithPersona()
    {
        var node = NodeFactory.CreateNode();
        node.PersonaId = "ford-uds";
        var ch = NodeFactory.CreateChannel();
        return (node, ch);
    }

    // SIDs IN the Ford catalog (OBD / UDS / proprietary) that FordUdsDispatch has no handler for:
    // it declines (returns false) so DispatchUds can route $22 / the bus can NRC. This is the
    // ticked-but-unimplemented case - identical to GM listing $12 etc. and NRC'ing it.
    public static IEnumerable<object[]> InCatalogUnhandledCases() => new[]
    {
        new object[] { new byte[] { 0x10, 0x01 } },       // DiagnosticSessionControl (UDS, no handler)
        // Note: $22 is in the catalog too but routed to Service22Handler by DispatchUds, so it also
        // declines here. $01/$09 (legislated OBD) are delegated to the shared J1979 handlers, and
        // $3E/$23/$27/$11/$34/$36/$37/$A0/$A1/$B1 have handlers (covered below).
    };

    [Theory]
    [MemberData(nameof(InCatalogUnhandledCases))]
    public void Physical_InCatalogUnhandledSid_DeclinesForBusToNrc(byte[] usdt)
    {
        var (node, ch) = MakeNodeWithPersona();
        var sid = usdt[0];

        bool claimed = FordUdsDispatch.Instance.Dispatch(
            node, usdt, ch,
            isFunctional: false, sid: sid, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        // In-catalog but no handler -> decline so the bus NRCs $11 (ticked-but-unimplemented).
        Assert.False(claimed, "an in-catalog SID with no handler declines so the bus can NRC");
        TestFrame.AssertEmpty(ch);
    }

    // SIDs OUTSIDE the Ford catalog ("not present"): logged then DECLINED (returns false) so the bus
    // NRC-$11s them - the same wire effect as the in-catalog-unhandled case above, matching a real PCM.
    public static IEnumerable<object[]> NotPresentCases() => new[]
    {
        new object[] { new byte[] { 0x21, 0x01 } },       // ReadDataByLocalId - not in OBD/UDS/proprietary
        new object[] { new byte[] { 0x99 } },             // Wholly unknown SID
    };

    [Theory]
    [MemberData(nameof(NotPresentCases))]
    public void Physical_NotPresentSid_DeclinesForBusToNrc(byte[] usdt)
    {
        var (node, ch) = MakeNodeWithPersona();
        var sid = usdt[0];

        bool claimed = FordUdsDispatch.Instance.Dispatch(
            node, usdt, ch,
            isFunctional: false, sid: sid, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        // A SID outside the Ford catalog is logged by the capture stack then declined (returns false)
        // so the bus NRC-$11s it like a real PCM. Dispatch itself emits nothing; the NRC is the bus's
        // job (full path verified in CommonServicesDispatchTests).
        Assert.False(claimed, "ford-uds declines SIDs outside its catalog so the bus can NRC $11");
        TestFrame.AssertEmpty(ch);
    }

    [Fact]
    public void Functional_DoesNotEmitResponse()
    {
        var (node, ch) = MakeNodeWithPersona();
        byte[] usdt = { 0x22, 0xF1, 0x90 };

        bool claimed = FordUdsDispatch.Instance.Dispatch(
            node, usdt, ch,
            isFunctional: true, sid: 0x22, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        Assert.True(claimed);
        TestFrame.AssertEmpty(ch);
    }

    [Fact]
    public void Mode09Pid02_NoBin_EmitsFirstFrameWithVinFallbackPrefix()
    {
        // 49 02 01 + 17-byte VIN = 20 bytes total. The Fragmenter emits a
        // First Frame (announcing total length 0x014, carrying 6 payload
        // bytes), then waits for FlowControl from the receiver before
        // emitting Consecutive Frames. We only verify the First Frame
        // contents - multi-frame reassembly with the FC handshake is
        // covered exhaustively in IsoTpTxStateMachineTests.
        // No bin loaded -> the VinFallback string is used.
        FordUdsDispatch.LoadFlashBin((byte[]?)null);
        var (node, ch) = MakeNodeWithPersona();
        // $09 is now identity-sourced (shared Service09Handler): seed DID $90/$C0 from the bin/fallback,
        // exactly as ConfigStore does on a ford-uds config load.
        FordUdsDispatch.SeedMode09Identity(node);
        byte[] usdt = { 0x09, 0x02 };

        bool claimed = FordUdsDispatch.Instance.Dispatch(
            node, usdt, ch,
            isFunctional: false, sid: 0x09, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        Assert.True(claimed);
        Assert.True(ch.RxQueue.TryDequeue(out var msg), "expected first frame");
        var data = msg!.Data;
        // First 4 bytes are CAN ID, then ISO-TP PCI. FF nibble = 1.
        const int IdBytes = 4;
        Assert.Equal(0x10, data[IdBytes] & 0xF0);           // FF PCI nibble
        int totalLen = ((data[IdBytes] & 0x0F) << 8) | data[IdBytes + 1];
        Assert.Equal(20, totalLen);                          // 3-byte header + 17-byte VIN
        Assert.Equal(0x49, data[IdBytes + 2]);               // positive-response SID
        Assert.Equal(0x02, data[IdBytes + 3]);               // PID echo
        Assert.Equal(0x01, data[IdBytes + 4]);               // NODI
        // First Frame carries 6 bytes of payload, of which 3 are the
        // header and 3 are the start of the VIN ("6FP").
        var vinStart = System.Text.Encoding.ASCII.GetString(data, IdBytes + 5, 3);
        Assert.Equal("6FP", vinStart);
    }

    [Fact]
    public void Mode09Pid04_NoBin_EmitsFirstFrameWithCalIdFallbackPrefix()
    {
        // 49 04 01 + 16-byte CalID = 19 bytes total. No bin loaded -> the
        // CalIdFallback string ("HAEE4UY") is used.
        FordUdsDispatch.LoadFlashBin((byte[]?)null);
        var (node, ch) = MakeNodeWithPersona();
        FordUdsDispatch.SeedMode09Identity(node);
        byte[] usdt = { 0x09, 0x04 };

        bool claimed = FordUdsDispatch.Instance.Dispatch(
            node, usdt, ch,
            isFunctional: false, sid: 0x09, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        Assert.True(claimed);
        Assert.True(ch.RxQueue.TryDequeue(out var msg), "expected first frame");
        var data = msg!.Data;
        const int IdBytes = 4;
        Assert.Equal(0x10, data[IdBytes] & 0xF0);
        int totalLen = ((data[IdBytes] & 0x0F) << 8) | data[IdBytes + 1];
        Assert.Equal(19, totalLen);
        Assert.Equal(0x49, data[IdBytes + 2]);
        Assert.Equal(0x04, data[IdBytes + 3]);
        Assert.Equal(0x01, data[IdBytes + 4]);
        // First 3 chars of "HAEE4UY" start at offset 5 in the FF
        var calIdStart = System.Text.Encoding.ASCII.GetString(data, IdBytes + 5, 3);
        Assert.Equal("HAE", calIdStart);
    }

    [Fact]
    public void Mode09Pid02_SourcesVinFromLoadedBin()
    {
        // With a bin loaded, the VIN must come from 0x000100C0 - NOT the
        // fallback - so it agrees with what PCMTec reads via $23 at the same
        // address. Plant a distinct 17-char VIN and assert the First Frame
        // carries its prefix ("WX0"), proving the canned string isn't used.
        var fakeBin = new byte[0x20000];
        System.Text.Encoding.ASCII.GetBytes("WX0TESTVIN1234567", 0, 17, fakeBin, 0x100C0);
        FordUdsDispatch.LoadFlashBin(fakeBin);

        var (node, ch) = MakeNodeWithPersona();
        FordUdsDispatch.SeedMode09Identity(node);
        byte[] usdt = { 0x09, 0x02 };

        FordUdsDispatch.Instance.Dispatch(
            node, usdt, ch,
            isFunctional: false, sid: 0x09, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        Assert.True(ch.RxQueue.TryDequeue(out var msg), "expected first frame");
        var data = msg!.Data;
        const int IdBytes = 4;
        Assert.Equal(20, ((data[IdBytes] & 0x0F) << 8) | data[IdBytes + 1]);
        Assert.Equal(0x49, data[IdBytes + 2]);
        Assert.Equal(0x02, data[IdBytes + 3]);
        var vinStart = System.Text.Encoding.ASCII.GetString(data, IdBytes + 5, 3);
        Assert.Equal("WX0", vinStart);

        FordUdsDispatch.LoadFlashBin((byte[]?)null);
    }

    [Fact]
    public void Mode09Pid04_SourcesCalIdFromLoadedBin_TruncatedAtDot()
    {
        // CalID comes from 0x00010046 and is truncated at the first '.' (the
        // bin stores "<strategy>.HEX"). Plant "ZZ9CAL2.HEX" and assert the
        // First Frame carries the strategy prefix ("ZZ9"), not the fallback.
        var fakeBin = new byte[0x20000];
        System.Text.Encoding.ASCII.GetBytes("ZZ9CAL2.HEX", 0, 11, fakeBin, 0x10046);
        FordUdsDispatch.LoadFlashBin(fakeBin);

        var (node, ch) = MakeNodeWithPersona();
        FordUdsDispatch.SeedMode09Identity(node);
        byte[] usdt = { 0x09, 0x04 };

        FordUdsDispatch.Instance.Dispatch(
            node, usdt, ch,
            isFunctional: false, sid: 0x09, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        Assert.True(ch.RxQueue.TryDequeue(out var msg), "expected first frame");
        var data = msg!.Data;
        const int IdBytes = 4;
        // 49 04 01 + 16-byte zero-padded CalID = 19 bytes total.
        Assert.Equal(19, ((data[IdBytes] & 0x0F) << 8) | data[IdBytes + 1]);
        Assert.Equal(0x49, data[IdBytes + 2]);
        Assert.Equal(0x04, data[IdBytes + 3]);
        var calIdStart = System.Text.Encoding.ASCII.GetString(data, IdBytes + 5, 3);
        Assert.Equal("ZZ9", calIdStart);

        FordUdsDispatch.LoadFlashBin((byte[]?)null);
    }

    [Fact]
    public void Service23_BinNotLoaded_NrcsConditionsNotCorrect()
    {
        var (node, ch) = MakeNodeWithPersona();
        FordUdsDispatch.LoadFlashBin((byte[]?)null);
        byte[] usdt = { 0x23, 0x00, 0x01, 0x00, 0xC0, 0x00, 0x04 };

        FordUdsDispatch.Instance.Dispatch(
            node, usdt, ch,
            isFunctional: false, sid: 0x23, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0x7F, 0x23, 0x22 }, resp); // CNCRSE
    }

    [Fact]
    public void Service23_ServesFromLoadedBin()
    {
        // PCMTec's observed request: read 4 bytes at 0x000100C0 - the VIN
        // anchor in the HAEE4UY bin. We mock the bin with a fake 64KB array
        // where 0x100C0 holds "6FPA" (the first 4 chars of the canned VIN).
        var fakeBin = new byte[0x20000];
        System.Text.Encoding.ASCII.GetBytes("6FPA", 0, 4, fakeBin, 0x100C0);
        FordUdsDispatch.LoadFlashBin(fakeBin);

        var (node, ch) = MakeNodeWithPersona();
        byte[] usdt = { 0x23, 0x00, 0x01, 0x00, 0xC0, 0x00, 0x04 };

        FordUdsDispatch.Instance.Dispatch(
            node, usdt, ch,
            isFunctional: false, sid: 0x23, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        // Response is 5 bytes: 0x63 + "6FPA" - fits a single ISO-TP frame.
        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0x63, 0x36, 0x46, 0x50, 0x41 }, resp);

        FordUdsDispatch.LoadFlashBin((byte[]?)null); // tidy up
    }

    [Fact]
    public void Service23_VerbatimPcmtecBytes_ServesFromBin()
    {
        // Regression: this is the EXACT 7-byte USDT PCMTec sends after Mode
        // 09 VIN/CalID succeed (observed 2026-05-23 15:47). The first
        // iteration of the parser assumed an ALFI byte and required 8 bytes,
        // silently falling through to NRC $11. The bin holds "6FPA" at
        // offset 0x100C0 - the VIN anchor PCMTec is cross-checking.
        var fakeBin = new byte[0x20000];
        System.Text.Encoding.ASCII.GetBytes("6FPA", 0, 4, fakeBin, 0x100C0);
        FordUdsDispatch.LoadFlashBin(fakeBin);

        var (node, ch) = MakeNodeWithPersona();
        // VERBATIM from the PCMTec capture, no extra ALFI byte.
        byte[] usdt = { 0x23, 0x00, 0x01, 0x00, 0xC0, 0x00, 0x04 };

        FordUdsDispatch.Instance.Dispatch(
            node, usdt, ch,
            isFunctional: false, sid: 0x23, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        // 0x63 + "6FPA" - single ISO-TP frame.
        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0x63, 0x36, 0x46, 0x50, 0x41 }, resp);

        FordUdsDispatch.LoadFlashBin((byte[]?)null);
    }

    [Fact]
    public void Service23_OutOfRange_Nrcs()
    {
        var fakeBin = new byte[16];
        FordUdsDispatch.LoadFlashBin(fakeBin);

        var (node, ch) = MakeNodeWithPersona();
        // Request 4 bytes from 0x100C0 against a 16-byte bin -> overflow.
        byte[] usdt = { 0x23, 0x00, 0x01, 0x00, 0xC0, 0x00, 0x04 };
        FordUdsDispatch.Instance.Dispatch(
            node, usdt, ch,
            isFunctional: false, sid: 0x23, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0x7F, 0x23, 0x31 }, resp); // ROOR
        FordUdsDispatch.LoadFlashBin((byte[]?)null);
    }

    [Fact]
    public void ServiceA1_RepliesWithE1AndIndexOnly_AndCapturesMapping()
    {
        // Verbatim PCMTec request from the 2026-05-23 16:30 capture:
        //   A1 01 8C 00 3F 90 B8
        // Phase 7: reply is JUST {E1, index} per the PCMTec dev blog
        // wire-format spec; sending the full 7-byte verbatim echo
        // (Phase 5 behaviour) triggered PCMTec's downstream NRE.
        FordUdsDispatch.ResetDmrSlotMap();
        var (node, ch) = MakeNodeWithPersona();
        byte[] usdt = { 0xA1, 0x01, 0x8C, 0x00, 0x3F, 0x90, 0xB8 };

        FordUdsDispatch.Instance.Dispatch(
            node, usdt, ch,
            isFunctional: false, sid: 0xA1, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0xE1, 0x01 }, resp);
        Assert.Equal((uint)0x003F90B8, FordUdsDispatch.DmrSlotMap[0x01]);
    }

    [Fact]
    public void ServiceA1_AcceptsAlternativeMagicBytes()
    {
        // Per the PCMTec dev blog the magic byte at offset 2 can be any of
        // {89, 8A, 8B, 8C, 91, 92, 93, 94, 99, 9A, 9B, A1, A2, A9}; each
        // represents a different memory access mode. The handler must not
        // filter on 0x8C.
        FordUdsDispatch.ResetDmrSlotMap();
        var (node, ch) = MakeNodeWithPersona();
        var scheduler = new DpidScheduler(new VirtualBus());

        byte[] alt = { 0xA1, 0x02, 0x91, 0x00, 0x3F, 0x9B, 0x70 };
        FordUdsDispatch.Instance.Dispatch(node, alt, ch, false, 0xA1, 0, scheduler, DiagnosticStack.Uds);
        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0xE1, 0x02 }, resp);
        Assert.Equal((uint)0x003F9B70, FordUdsDispatch.DmrSlotMap[0x02]);
    }

    [Fact]
    public void ServiceA1_RejectUnmappedDmr_NrcsRequestOutOfRangeForUnmappedAddress()
    {
        // With the opt-in RejectUnmappedDmr flag set, a $A1 whose RAM address has
        // no row in the $A1 grid (DmrSignalMappings) gets NRC $31 RequestOutOfRange
        // instead of binding the slot + echoing E1. The slot must NOT be captured
        // into DmrSlotMap (it was rejected), but the observe-CSV still records it.
        FordUdsDispatch.ResetDmrSlotMap();
        var (node, ch) = MakeNodeWithPersona();
        node.RejectUnmappedDmr = true;
        byte[] usdt = { 0xA1, 0x05, 0x8C, 0x00, 0x3F, 0x90, 0xB8 };   // addr 0x003F90B8, not mapped

        FordUdsDispatch.Instance.Dispatch(
            node, usdt, ch,
            isFunctional: false, sid: 0xA1, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        Assert.Equal(new byte[] { Service.NegativeResponse, 0xA1, Nrc.RequestOutOfRange },
                     TestFrame.DequeueSingleFrameUsdt(ch));
        Assert.False(FordUdsDispatch.DmrSlotMap.ContainsKey(0x05));
    }

    [Fact]
    public void ServiceA1_RejectUnmappedDmr_AcceptsMappedAddress()
    {
        // Same flag on, but the address IS in the grid - so the request binds the
        // slot and echoes E1 exactly as the default accept-all path does.
        FordUdsDispatch.ResetDmrSlotMap();
        var (node, ch) = MakeNodeWithPersona();
        node.RejectUnmappedDmr = true;
        node.AddDmrSignalMapping(new DmrSignalMapping { Address = 0x003F90B8 });
        byte[] usdt = { 0xA1, 0x05, 0x8C, 0x00, 0x3F, 0x90, 0xB8 };

        FordUdsDispatch.Instance.Dispatch(
            node, usdt, ch,
            isFunctional: false, sid: 0xA1, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        Assert.Equal(new byte[] { 0xE1, 0x05 }, TestFrame.DequeueSingleFrameUsdt(ch));
        Assert.Equal((uint)0x003F90B8, FordUdsDispatch.DmrSlotMap[0x05]);
    }

    [Fact]
    public void ServiceA1_MultipleSlots_AllCaptured()
    {
        FordUdsDispatch.ResetDmrSlotMap();
        var (node, ch) = MakeNodeWithPersona();
        var scheduler = new DpidScheduler(new VirtualBus());

        // Verbatim subsequence from the 16:10 PCMTec capture.
        byte[][] requests =
        {
            new byte[] { 0xA1, 0x01, 0x8C, 0x00, 0x3F, 0x90, 0xB8 },
            new byte[] { 0xA1, 0x08, 0x8C, 0x00, 0x3F, 0x86, 0xEC },
            new byte[] { 0xA1, 0x02, 0x8C, 0x00, 0x3F, 0x9B, 0x70 },
            new byte[] { 0xA1, 0x09, 0x8C, 0x00, 0x3F, 0x7B, 0x28 },
        };
        var expectedAcks = new[] { (byte)0x01, (byte)0x08, (byte)0x02, (byte)0x09 };
        foreach (var (r, expected) in requests.Zip(expectedAcks))
        {
            FordUdsDispatch.Instance.Dispatch(node, r, ch, false, 0xA1, 0, scheduler, DiagnosticStack.Uds);
            var resp = TestFrame.DequeueSingleFrameUsdt(ch);
            Assert.Equal(new byte[] { 0xE1, expected }, resp);
        }

        Assert.Equal((uint)0x003F90B8, FordUdsDispatch.DmrSlotMap[0x01]);
        Assert.Equal((uint)0x003F86EC, FordUdsDispatch.DmrSlotMap[0x08]);
        Assert.Equal((uint)0x003F9B70, FordUdsDispatch.DmrSlotMap[0x02]);
        Assert.Equal((uint)0x003F7B28, FordUdsDispatch.DmrSlotMap[0x09]);
    }

    [Fact]
    public void Service3E_NonSuppressedSubFunction_RepliesPositive()
    {
        var (node, ch) = MakeNodeWithPersona();
        byte[] usdt = { 0x3E, 0x02 };

        FordUdsDispatch.Instance.Dispatch(node, usdt, ch, false, 0x3E, 0,
            new DpidScheduler(new VirtualBus()), DiagnosticStack.Uds);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0x7E, 0x02 }, resp);
    }

    [Fact]
    public void Service3E_SuppressedSubFunction_Silent()
    {
        var (node, ch) = MakeNodeWithPersona();
        byte[] usdt = { 0x3E, 0x80 };

        FordUdsDispatch.Instance.Dispatch(node, usdt, ch, false, 0x3E, 0,
            new DpidScheduler(new VirtualBus()), DiagnosticStack.Uds);

        TestFrame.AssertEmpty(ch);
    }

    [Fact]
    public void ServiceA0_EchoesRequestBodyWithPositiveSid()
    {
        // Observed PCMTec request: A0 0A
        var (node, ch) = MakeNodeWithPersona();
        byte[] usdt = { 0xA0, 0x0A };

        FordUdsDispatch.Instance.Dispatch(
            node, usdt, ch,
            isFunctional: false, sid: 0xA0, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0xE0, 0x0A }, resp);
    }

    [Fact]
    public void Mode09_UnknownPid_NrcsRequestOutOfRange()
    {
        // $09 is now handled by the shared, make-agnostic Service09Handler (legislated OBD). An
        // unsupported InfoType (0x06 CVN - not backed by an identity DID) is dropped; with no supported
        // InfoType left in the request the handler NRCs $31 RequestOutOfRange - the spec-correct answer
        // for a supported service with an out-of-range sub-parameter, NOT $11 ServiceNotSupported (which
        // would wrongly claim the whole service is absent now that $09 is implemented).
        var (node, ch) = MakeNodeWithPersona();
        FordUdsDispatch.SeedMode09Identity(node);   // $02/$04 supported; $06 still isn't
        byte[] usdt = { 0x09, 0x06 };

        bool claimed = FordUdsDispatch.Instance.Dispatch(
            node, usdt, ch,
            isFunctional: false, sid: 0x09, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        Assert.True(claimed, "$09 is handled by the shared J1979 handler, so the persona claims it");
        Assert.Equal(new byte[] { Service.NegativeResponse, 0x09, Nrc.RequestOutOfRange },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void Service27_RequestSeed_ReturnsNonZeroSeedAndInstallsFallbackModule()
    {
        // The motivating bus log: PCMTec sends $27 and the ford-uds persona
        // used to NRC $11, stalling the flash. Now the persona installs the Ford
        // accept-any-key module (config left SecurityModuleId null here) and
        // answers with a real seed.
        var (node, ch) = MakeNodeWithPersona();
        Assert.Null(node.SecurityModule);

        bool claimed = FordUdsDispatch.Instance.Dispatch(
            node, new byte[] { 0x27, 0x01 }, ch,
            isFunctional: false, sid: 0x27, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        Assert.True(claimed);
        Assert.NotNull(node.SecurityModule);
        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(0x67, resp[0]);                       // positive SID
        Assert.Equal(0x01, resp[1]);                       // sub echo
        Assert.Equal(5, resp.Length);                      // 0x67 + sub + 3-byte seed (FG PCM default)
        // The 3-byte seed must not be all-zero (zero means "already unlocked").
        bool anyNonZero = false;
        for (int i = 2; i < resp.Length; i++) if (resp[i] != 0) { anyNonZero = true; break; }
        Assert.True(anyNonZero, "issued seed must be non-zero");
    }

    [Fact]
    public void Service27_RequestSeedThenSendKey_UnlocksAcceptingAnyKey()
    {
        var (node, ch) = MakeNodeWithPersona();
        var scheduler = new DpidScheduler(new VirtualBus());

        FordUdsDispatch.Instance.Dispatch(node, new byte[] { 0x27, 0x01 }, ch, false, 0x27, 0,
            scheduler, DiagnosticStack.Uds);
        var seedResp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(0x67, seedResp[0]);

        // sendKey with an ARBITRARY key - the accept-any module never checks it.
        FordUdsDispatch.Instance.Dispatch(node, new byte[] { 0x27, 0x02, 0xDE, 0xAD }, ch, false, 0x27, 0,
            scheduler, DiagnosticStack.Uds);
        var keyResp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0x67, 0x02 }, keyResp);
        Assert.Equal(1, (int)node.State.SecurityUnlockedLevel);
    }

    [Fact]
    public void Service27_SendKeyWithoutSeed_NrcsConditionsNotCorrect()
    {
        var (node, ch) = MakeNodeWithPersona();
        FordUdsDispatch.Instance.Dispatch(node, new byte[] { 0x27, 0x02, 0x11, 0x22 }, ch, false, 0x27, 0,
            new DpidScheduler(new VirtualBus()), DiagnosticStack.Uds);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0x7F, 0x27, 0x22 }, resp); // CNCRSE
        Assert.Equal(0, (int)node.State.SecurityUnlockedLevel);
    }

    [Fact]
    public void Service27_Level2RequestSeed_AlsoSucceeds()
    {
        // The bus log shows PCMTec trying sub $03 (level 2) before falling back
        // to sub $01; the accept-any module must serve any odd subfunction.
        var (node, ch) = MakeNodeWithPersona();
        FordUdsDispatch.Instance.Dispatch(node, new byte[] { 0x27, 0x03 }, ch, false, 0x27, 0,
            new DpidScheduler(new VirtualBus()), DiagnosticStack.Uds);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(0x67, resp[0]);
        Assert.Equal(0x03, resp[1]);
        Assert.Equal(2, (int)node.State.SecurityPendingSeedLevel);
    }

    [Fact]
    public void Service11_EcuReset_AcksAndClearsSecurityState()
    {
        // The flash tool sends 11 01 (hardReset) mid-unlock; we ack 51 01 and
        // reset like a real PCM rather than NRC $11.
        var (node, ch) = MakeNodeWithPersona();
        node.State.SecurityUnlockedLevel = 2;
        node.State.NormalCommunicationDisabled = true;

        bool claimed = FordUdsDispatch.Instance.Dispatch(
            node, new byte[] { 0x11, 0x01 }, ch,
            isFunctional: false, sid: 0x11, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        Assert.True(claimed);
        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0x51, 0x01 }, resp);
        Assert.Equal(0, (int)node.State.SecurityUnlockedLevel);
        Assert.False(node.State.NormalCommunicationDisabled);
    }

    [Fact]
    public void Service11_EcuReset_SuppressedSubFunction_Silent()
    {
        var (node, ch) = MakeNodeWithPersona();
        FordUdsDispatch.Instance.Dispatch(node, new byte[] { 0x11, 0x81 }, ch, false, 0x11, 0,
            new DpidScheduler(new VirtualBus()), DiagnosticStack.Uds);

        TestFrame.AssertEmpty(ch); // suppress-positive-response bit set (sub 0x01)
    }

    [Fact]
    public void Service11_NonHardReset_NrcsSubFunctionNotSupported()
    {
        // The real PCM MDX supports hardReset ($01) only; any other reset type NRC-$12s.
        var (node, ch) = MakeNodeWithPersona();
        FordUdsDispatch.Instance.Dispatch(node, new byte[] { 0x11, 0x02 }, ch, false, 0x11, 0,
            new DpidScheduler(new VirtualBus()), DiagnosticStack.Uds);

        Assert.Equal(new byte[] { 0x7F, 0x11, 0x12 }, TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void ServiceB1_DiagnosticCommand_AcksF1()
    {
        // Flash erase `B1 00 B2 AA` -> F1 echoing the command (>= 2 bytes; a bare
        // F1 was rejected by PCMTec's J2534 stack with ERR_INVALID_MSG).
        var (node, ch) = MakeNodeWithPersona();
        FordUdsDispatch.Instance.Dispatch(node, new byte[] { 0xB1, 0x00, 0xB2, 0xAA }, ch, false, 0xB1, 0,
            new DpidScheduler(new VirtualBus()), DiagnosticStack.Uds);

        Assert.Equal(new byte[] { 0xF1, 0x00, 0xB2, 0xAA }, TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void Service34_RequestDownload_Acks74AndStartsCapture()
    {
        var (node, ch) = MakeNodeWithPersona();
        byte[] req = { 0x34, 0x00, 0x01, 0x00, 0x00, 0x00, 0x0F, 0x00, 0x00 }; // spanishOak
        FordUdsDispatch.Instance.Dispatch(node, req, ch, false, 0x34, 0,
            new DpidScheduler(new VirtualBus()), DiagnosticStack.Uds);

        Assert.Equal(new byte[] { 0x74, 0x20, 0x04, 0x00 }, TestFrame.DequeueSingleFrameUsdt(ch));
        Assert.True(node.State.DownloadActive);
        Assert.NotNull(node.State.DownloadBuffer);
        Assert.Equal(0u, node.State.DownloadBytesReceived);
    }

    [Fact]
    public void Service36_AfterDownload_Acks76AndBuffersData()
    {
        var (node, ch) = MakeNodeWithPersona();
        var sched = new DpidScheduler(new VirtualBus());
        FordUdsDispatch.Instance.Dispatch(node, new byte[] { 0x34, 0x00, 0x01, 0x00, 0x00, 0x00, 0x0F, 0x00, 0x00 },
            ch, false, 0x34, 0, sched, DiagnosticStack.Uds);
        TestFrame.DequeueSingleFrameUsdt(ch); // drain 74

        FordUdsDispatch.Instance.Dispatch(node, new byte[] { 0x36, 0xDE, 0xAD, 0xBE, 0xEF },
            ch, false, 0x36, 0, sched, DiagnosticStack.Uds);

        Assert.Equal(new byte[] { 0x76, 0x00 }, TestFrame.DequeueSingleFrameUsdt(ch));
        Assert.Equal(4u, node.State.DownloadBytesReceived);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
            node.State.DownloadBuffer!.AsSpan(0, 4).ToArray());
    }

    [Fact]
    public void Service36_WithoutDownload_NrcsConditionsNotCorrect()
    {
        var (node, ch) = MakeNodeWithPersona();
        node.State.DownloadActive = false;
        FordUdsDispatch.Instance.Dispatch(node, new byte[] { 0x36, 0x01, 0x02 }, ch, false, 0x36, 0,
            new DpidScheduler(new VirtualBus()), DiagnosticStack.Uds);

        Assert.Equal(new byte[] { 0x7F, 0x36, 0x22 }, TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void FlashWriteSequence_CapturesImageWithBlock0PreservedAndOverlayAt0x10000()
    {
        // End-to-end: B1 erase -> 34 -> two 36 chunks -> 37. The flushed image must
        // keep the loaded bin's block 0 and overlay the $36 stream at 0x10000.
        var bin = new byte[0x20000];
        bin[0x0000] = 0xB0;       // block-0 markers that must survive
        bin[0xFFFF] = 0xB1;
        FordUdsDispatch.LoadFlashBin(bin);
        try
        {
            var (node, ch) = MakeNodeWithPersona();
            var sched = new DpidScheduler(new VirtualBus());

            FordUdsDispatch.Instance.Dispatch(node, new byte[] { 0xB1, 0x00, 0xB2, 0xAA }, ch, false, 0xB1, 0, sched, DiagnosticStack.Uds);
            Assert.Equal(new byte[] { 0xF1, 0x00, 0xB2, 0xAA }, TestFrame.DequeueSingleFrameUsdt(ch));

            FordUdsDispatch.Instance.Dispatch(node, new byte[] { 0x34, 0x00, 0x01, 0x00, 0x00, 0x00, 0x0F, 0x00, 0x00 }, ch, false, 0x34, 0, sched, DiagnosticStack.Uds);
            Assert.Equal(new byte[] { 0x74, 0x20, 0x04, 0x00 }, TestFrame.DequeueSingleFrameUsdt(ch));

            FordUdsDispatch.Instance.Dispatch(node, new byte[] { 0x36, 0x11, 0x22, 0x33, 0x44 }, ch, false, 0x36, 0, sched, DiagnosticStack.Uds);
            Assert.Equal(new byte[] { 0x76, 0x00 }, TestFrame.DequeueSingleFrameUsdt(ch));
            FordUdsDispatch.Instance.Dispatch(node, new byte[] { 0x36, 0x55, 0x66, 0x77, 0x88 }, ch, false, 0x36, 0, sched, DiagnosticStack.Uds);
            Assert.Equal(new byte[] { 0x76, 0x00 }, TestFrame.DequeueSingleFrameUsdt(ch));

            FordUdsDispatch.Instance.Dispatch(node, new byte[] { 0x37 }, ch, false, 0x37, 0, sched, DiagnosticStack.Uds);
            Assert.Equal(new byte[] { 0x77, 0x00 }, TestFrame.DequeueSingleFrameUsdt(ch));
            Assert.False(node.State.DownloadActive);

            var path = FordUdsDispatch.LastFlashWritePath;
            Assert.False(string.IsNullOrEmpty(path));
            Assert.True(File.Exists(path!));
            var image = File.ReadAllBytes(path!);
            Assert.Equal(0x20000, image.Length);                 // full 128 KiB test image
            Assert.Equal(0xB0, image[0x0000]);                   // block 0 preserved
            Assert.Equal(0xB1, image[0xFFFF]);
            Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 },
                image.AsSpan(0x10000, 8).ToArray());             // both chunks overlaid at 0x10000
        }
        finally
        {
            FordUdsDispatch.LoadFlashBin((byte[]?)null);
        }
    }

    [Fact]
    public void Dispatch_OpensLogFile()
    {
        var (node, ch) = MakeNodeWithPersona();
        FordUdsDispatch.EndSession(); // clean slate
        Assert.Null(FordUdsDispatch.CurrentLogPath);

        FordUdsDispatch.Instance.Dispatch(
            node, new byte[] { 0x22, 0x01, 0x00 }, ch,
            isFunctional: false, sid: 0x22, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        var path = FordUdsDispatch.CurrentLogPath;
        Assert.False(string.IsNullOrEmpty(path), "lazy-open should have created a log file path");
        Assert.True(File.Exists(path!), $"log file should exist at {path}");
    }
}

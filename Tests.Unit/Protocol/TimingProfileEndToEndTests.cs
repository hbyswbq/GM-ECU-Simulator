using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Common.PassThru;
using Common.Protocol;
using Core.Bus;
using Core.Protocol;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Protocol;

// End-to-end coverage of the response-timing wiring through the real bus entry
// point (VirtualBus.DispatchHostTx -> Resolve -> handler -> fragmenter). Proves
// that EcuNode.ResponseDelayMs and the resolved stack's TimingProfile are
// honoured on the wire: instant when 0, deferred when within P2, and a
// 7F sid 78 RCR-RP heartbeat then the real reply when the delay exceeds P2.
public sealed class TimingProfileEndToEndTests
{
    private const ushort PhysReq = NodeFactory.PhysReq;

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

    private static byte[] UnwrapSf(PassThruMsg msg)
    {
        var raw = msg.Data;
        int len = raw[4] & 0x0F;
        return raw.AsSpan(5, len).ToArray();
    }

    private static (VirtualBus bus, ChannelSession ch) Bus(int responseDelayMs = 0, bool emit78 = true)
    {
        var bus = new VirtualBus();
        var node = NodeFactory.CreateNode();
        node.ResponseDelayMs = responseDelayMs;
        node.Emit78WhenSlow = emit78;
        bus.AddNode(node);
        var ch = new ChannelSession { Id = 1, Protocol = ProtocolID.CAN, Baud = 500_000, Bus = bus };
        return (bus, ch);
    }

    private static byte[] WaitForOne(ChannelSession ch, int timeoutMs = 2000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (ch.RxQueue.IsEmpty && Environment.TickCount64 < deadline) Thread.Sleep(5);
        Assert.True(ch.RxQueue.TryDequeue(out var msg), "timed out waiting for a response frame");
        return UnwrapSf(msg!);
    }

    [Fact]
    public void ResponseDelayZero_RespondsImmediately()
    {
        var (bus, ch) = Bus(responseDelayMs: 0);
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x01, 0x3E }), ch);

        // Synchronous: the $7E is on the queue the instant dispatch returns.
        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        Assert.Equal(new byte[] { 0x7E }, UnwrapSf(msg!));
        TestEmpty(ch);
    }

    [Fact]
    public void ResponseDelayWithinP2_DefersResponse_NoPending()
    {
        // GM P2 budget = 100 ms; an 80 ms delay is "fast enough" -> just deferred.
        var (bus, ch) = Bus(responseDelayMs: 80);
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x01, 0x3E }), ch);

        Assert.True(ch.RxQueue.IsEmpty, "response should be deferred, not immediate");
        Assert.Equal(new byte[] { 0x7E }, WaitForOne(ch));   // the real reply, no 78 before it
        TestEmpty(ch);
    }

    [Fact]
    public void ResponseDelayAboveP2_EmitsPendingThenResponse()
    {
        // GM P2 budget = 100 ms, P2* = 5100 ms; a 250 ms delay -> one 78 before
        // the budget (~75 ms), then the real $7E at ~250 ms.
        var (bus, ch) = Bus(responseDelayMs: 250);
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x01, 0x3E }), ch);

        Assert.Equal(new byte[] { 0x7F, 0x3E, 0x78 }, WaitForOne(ch));   // RCR-RP
        Assert.Equal(new byte[] { 0x7E }, WaitForOne(ch));               // real response
        TestEmpty(ch);
    }

    [Fact]
    public void ResponseDelayAboveP2_Emit78False_GoesQuietThenResponds()
    {
        var (bus, ch) = Bus(responseDelayMs: 250, emit78: false);
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x01, 0x3E }), ch);

        // No pending frame at the P2 boundary - the only frame is the real reply.
        Assert.Equal(new byte[] { 0x7E }, WaitForOne(ch));
        TestEmpty(ch);
    }

    [Fact]
    public void Pacing_AppliesToNrc()
    {
        // $11 ECUReset is not a GM service - the bus NRC-$11s it. The NRC is a
        // fragmenter response too, so the delay applies to it.
        var (bus, ch) = Bus(responseDelayMs: 80);
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x01, 0x11 }), ch);

        Assert.True(ch.RxQueue.IsEmpty, "the NRC should be deferred too");
        Assert.Equal(new byte[] { Service.NegativeResponse, 0x11, Nrc.ServiceNotSupported }, WaitForOne(ch));
    }

    [Fact]
    public void FlashTiming_BypassesResponsePacing()
    {
        // A flash response must never go through the generic response pacing (it
        // has its own FlashTiming knob and must not grow an unwanted 78). Arm a
        // pacing that would defer everything ~100 s, then prove an immediate
        // FlashTiming send still lands at once via SendNow.
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();
        node.State.Fragmenter.Pacing = new ResponsePacing { ProcessingMs = 100_000, Timing = TimingProfile.Gm };

        FlashTiming.EnqueueTransferResponse(node, ch, new byte[] { 0x76, 0x00 });

        Assert.True(ch.RxQueue.TryDequeue(out var msg), "flash response must bypass response pacing");
        Assert.Equal(new byte[] { 0x76, 0x00 }, UnwrapSf(msg!));
    }

    [Fact]
    public void PacedMultiFrameResponse_CompletesFcCascade()
    {
        // The highest-risk seam: the deferred response is emitted off-thread (the
        // pace timer), so the FirstFrame goes out from the TimerScheduler thread
        // and the host FC must be processed re-entrantly. A >7-byte $1A reply
        // exercises the full FF/FC/CF cascade through the paced path.
        var bus = new VirtualBus();
        var node = NodeFactory.CreateNode();
        node.ResponseDelayMs = 80;                          // within P2 -> deferred whole, no 78
        var idValue = Enumerable.Range(1, 20).Select(i => (byte)i).ToArray();
        node.SetIdentifier(0x90, idValue);
        bus.AddNode(node);
        var ch = new ChannelSession { Id = 1, Protocol = ProtocolID.CAN, Baud = 500_000, Bus = bus };

        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x02, 0x1A, 0x90 }), ch);
        Assert.True(ch.RxQueue.IsEmpty, "the FirstFrame should be deferred by the pacing");

        var usdt = CollectMultiFrame(bus, ch);
        var expected = new byte[] { 0x5A, 0x90 }.Concat(idValue).ToArray();
        Assert.Equal(expected, usdt);
    }

    [Fact]
    public void FunctionalBroadcast_NotPaced_PhysicalIs()
    {
        var (bus, ch) = Bus(responseDelayMs: 80);

        // Functional OBD-II $01 00 ($7DF) is answered immediately - not paced, so
        // many ECUs answering one broadcast don't all defer / emit 78.
        bus.DispatchHostTx(WrapCanFrame(0x7DF, new byte[] { 0x02, 0x01, 0x00 }), ch);
        Assert.True(ch.RxQueue.TryDequeue(out var fmsg), "functional broadcast must not be paced");
        Assert.Equal(0x41, UnwrapSf(fmsg!)[0]);
        ch.ClearRxQueue();

        // The same request physically addressed IS paced (deferred).
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x02, 0x01, 0x00 }), ch);
        Assert.True(ch.RxQueue.IsEmpty, "physical response should be deferred");
        Assert.Equal(0x41, WaitForOne(ch)[0]);
    }

    [Fact]
    public void KernelMode_ResponseNotPaced()
    {
        // A transient SPS kernel must answer promptly (the flasher polls tightly);
        // it does not honour the generic ResponseDelayMs.
        var bus = new VirtualBus();
        var node = NodeFactory.CreateNode();
        node.ResponseDelayMs = 200;
        node.EnterKernelMode(ProtocolStacks.KernelBindingFor(node));
        bus.AddNode(node);
        var ch = new ChannelSession { Id = 1, Protocol = ProtocolID.CAN, Baud = 500_000, Bus = bus };

        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x01, 0x3E }), ch);
        Assert.False(ch.RxQueue.IsEmpty, "kernel-mode responses must not be paced");
    }

    [Fact]
    public void PacedResponse_CancelledOnChannelTeardown()
    {
        // Host disconnect mid-pace (IpcSessionState.RemoveChannel -> AbortIfActiveOn)
        // must abandon the pending chain so no frame lands on the removed channel.
        var (bus, ch) = Bus(responseDelayMs: 250);             // > P2 -> a pace chain (78 + real)
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x01, 0x3E }), ch);

        bus.FindByRequestId(PhysReq)!.State.Fragmenter.AbortIfActiveOn(ch);

        Thread.Sleep(400);                                     // past the full 250 ms window + the 78
        Assert.True(ch.RxQueue.IsEmpty, "a torn-down channel must receive no paced frames");
    }

    // Reassemble a deferred multi-frame USDT response: wait for the FF, release
    // the CFs with an FC.CTS (BS=0, STmin=0), then collect the CF tail.
    private static byte[] CollectMultiFrame(VirtualBus bus, ChannelSession ch, int timeoutMs = 2000)
    {
        var ff = WaitForRaw(ch, timeoutMs);
        Assert.Equal(0x10, ff[4] & 0xF0);                      // FirstFrame PCI
        int len = ((ff[4] & 0x0F) << 8) | ff[5];
        var data = new List<byte>();
        data.AddRange(ff.AsSpan(6, Math.Min(6, len)).ToArray());

        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x30, 0x00, 0x00 }), ch);   // FC.CTS

        while (data.Count < len)
        {
            var cf = WaitForRaw(ch, timeoutMs);
            Assert.Equal(0x20, cf[4] & 0xF0);                  // ConsecutiveFrame PCI
            int take = Math.Min(7, len - data.Count);
            data.AddRange(cf.AsSpan(5, take).ToArray());
        }
        return data.ToArray();
    }

    private static byte[] WaitForRaw(ChannelSession ch, int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (ch.RxQueue.IsEmpty && Environment.TickCount64 < deadline) Thread.Sleep(2);
        Assert.True(ch.RxQueue.TryDequeue(out var msg), "timed out waiting for a frame");
        return msg!.Data;
    }

    private static void TestEmpty(ChannelSession ch)
        => Assert.False(ch.RxQueue.TryDequeue(out _), "no further frames expected");
}

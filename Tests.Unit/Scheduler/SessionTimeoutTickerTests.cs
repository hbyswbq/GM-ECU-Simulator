using System;
using System.Threading;
using Common.PassThru;
using Core.Bus;
using Core.Ecu;
using Core.Protocol;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Scheduler;

// End-to-end coverage that the TesterPresentTicker keys the P3C / S3 session
// timeout off each node's own EcuNode.SessionTimeoutMs (active stack's
// TimingProfile, or a per-ECU override) rather than a single global constant.
public sealed class SessionTimeoutTickerTests
{
    private static byte[] UnwrapSf(PassThruMsg msg)
    {
        var raw = msg.Data;
        int len = raw[4] & 0x0F;
        return raw.AsSpan(5, len).ToArray();
    }

    [Fact]
    public void SessionTimeoutMs_FollowsActiveStack_AndOverride()
    {
        var gm = NodeFactory.CreateNode();
        Assert.Equal(5000, gm.SessionTimeoutMs);        // GMW3110 P3Cnom via the GM stack

        var ford = NodeFactory.CreateNode();
        ford.PersonaId = "ford-uds";
        Assert.Equal(5000, ford.SessionTimeoutMs);      // UDS S3 via the Ford stack

        gm.SessionTimeoutOverrideMs = 1234;
        Assert.Equal(1234, gm.SessionTimeoutMs);        // per-ECU override wins
    }

    [Fact]
    public void EffectiveTiming_FallsBackToGm_ForUnknownCanId()
    {
        var node = NodeFactory.CreateNode();
        Assert.Equal(TimingProfile.Gm, node.EffectiveTiming(0xDEAD));            // no binding owns it -> first stack
        Assert.Equal(TimingProfile.Gm, node.EffectiveTiming(NodeFactory.PhysReq)); // GM physical -> Gm
    }

    [Fact]
    public void Timeout60_NotPaced_WhenResponseDelaySet()
    {
        // Even with response pacing armed (ResponseDelayMs > P2), the unsolicited
        // teardown $60 must arrive immediately as the bare {0x60}, never deferred
        // or prefixed with 7F 20 78 - it bypasses pacing via SendNow.
        var bus = new VirtualBus();
        var ch = new ChannelSession { Id = 1, Protocol = ProtocolID.CAN, Baud = 500_000, Bus = bus };
        var node = NodeFactory.CreateNode();
        node.ResponseDelayMs = 250;
        node.SessionTimeoutOverrideMs = 200;
        node.State.Fragmenter.Pacing = new ResponsePacing { ProcessingMs = 250, Timing = TimingProfile.Gm };
        node.State.LastEnhancedChannel = ch;
        node.State.TesterPresent.Activate();
        bus.AddNode(node);

        try
        {
            bus.Ticker.Start();
            long deadline = Environment.TickCount64 + 2500;
            while (ch.RxQueue.IsEmpty && Environment.TickCount64 < deadline) Thread.Sleep(10);

            Assert.True(ch.RxQueue.TryDequeue(out var msg), "expected the unsolicited $60");
            Assert.Equal(new byte[] { 0x60 }, UnwrapSf(msg!));   // immediate, no 7F 20 78 ahead of it
            Assert.True(ch.RxQueue.IsEmpty, "teardown bypasses pacing - no 78, no extra frames");
        }
        finally
        {
            bus.Ticker.Dispose();
        }
    }

    [Fact]
    public void Ticker_TimesOutPerNode_FastExpiresSlowSurvives()
    {
        var bus = new VirtualBus();

        // Fast node: a 200 ms override; it should exit within a few ticks.
        var chFast = new ChannelSession { Id = 1, Protocol = ProtocolID.CAN, Baud = 500_000, Bus = bus };
        var fast = NodeFactory.CreateNode();
        fast.SessionTimeoutOverrideMs = 200;
        fast.State.LastEnhancedChannel = chFast;
        fast.State.TesterPresent.Activate();
        bus.AddNode(fast);

        // Slow node: the default 5000 ms; it must still be Active in the window.
        var chSlow = new ChannelSession { Id = 2, Protocol = ProtocolID.CAN, Baud = 500_000, Bus = bus };
        var slow = NodeFactory.CreateNode();
        slow.PhysicalRequestCanId = 0x7E1;
        slow.UsdtResponseCanId = 0x7E9;
        slow.State.LastEnhancedChannel = chSlow;
        slow.State.TesterPresent.Activate();
        bus.AddNode(slow);

        try
        {
            bus.Ticker.Start();

            long deadline = Environment.TickCount64 + 2500;
            while (fast.State.TesterPresent.State == TesterPresentTimerState.Active
                   && Environment.TickCount64 < deadline)
                Thread.Sleep(10);

            Assert.Equal(TesterPresentTimerState.Inactive, fast.State.TesterPresent.State);

            // The P3C timeout sends the unsolicited $20 positive response ($60).
            Assert.True(chFast.RxQueue.TryDequeue(out var msg), "expected an unsolicited $60 on timeout");
            Assert.Equal(new byte[] { 0x60 }, UnwrapSf(msg!));

            // The 5000 ms node is untouched in this window - this is the whole
            // point of a per-node threshold.
            Assert.Equal(TesterPresentTimerState.Active, slow.State.TesterPresent.State);
            Assert.True(chSlow.RxQueue.IsEmpty);
        }
        finally
        {
            bus.Ticker.Dispose();
        }
    }
}

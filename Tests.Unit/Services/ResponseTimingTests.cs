using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Core.Bus;
using Core.Protocol;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Services;

// Unit coverage for the P2 / P2* response pacer (Core/Services/ResponseTiming).
// Pace() is exercised with tiny TimingProfiles and a recording sendNow so the
// 78-RCR-RP cadence is deterministic and fast (no ISO-TP, no bus). The bus-level
// integration of the same logic lives in TimingProfileEndToEndTests.
public sealed class ResponseTimingTests
{
    private static readonly ChannelSession Ch = NodeFactory.CreateChannel();

    private static (ConcurrentQueue<byte[]> sent, Action<ChannelSession, uint, byte[]> send) Recorder()
    {
        var q = new ConcurrentQueue<byte[]>();
        return (q, (_, _, p) => q.Enqueue(p));
    }

    private static List<byte[]> WaitForCount(ConcurrentQueue<byte[]> q, int count, int timeoutMs = 3000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (q.Count < count && Environment.TickCount64 < deadline) Thread.Sleep(5);
        return q.ToArray().ToList();
    }

    private static List<byte[]> WaitForTerminal(ConcurrentQueue<byte[]> q, byte[] terminal, int timeoutMs = 3000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline && (q.IsEmpty || !q.ToArray()[^1].SequenceEqual(terminal)))
            Thread.Sleep(5);
        return q.ToArray().ToList();
    }

    [Fact]
    public void ProcessingZero_SendsImmediately_NoTimer()
    {
        var (q, send) = Recorder();
        var payload = new byte[] { 0x7E };
        ResponseTiming.Pace(new ResponsePacing { ProcessingMs = 0, Timing = new TimingProfile(150, 5100, 5000) },
                            Ch, 0x7E8, payload, send);

        Assert.Single(q);                              // synchronous - no deferral
        Assert.True(q.TryDequeue(out var f));
        Assert.Equal(payload, f);
    }

    [Fact]
    public void WithinP2_DefersWholeResponse_NoPending()
    {
        var (q, send) = Recorder();
        var payload = new byte[] { 0x62, 0x00, 0x0C, 0x11, 0x22 };
        ResponseTiming.Pace(new ResponsePacing { ProcessingMs = 40, Timing = new TimingProfile(120, 600, 5000) },
                            Ch, 0x7E8, payload, send);

        Assert.Empty(q);                               // deferred, not immediate
        var frames = WaitForCount(q, 1);
        Assert.Single(frames);
        Assert.Equal(payload, frames[0]);              // exactly the real response, no 78
    }

    [Fact]
    public void Emit78False_DefersWithoutPending_EvenWhenSlowerThanP2()
    {
        var (q, send) = Recorder();
        var payload = new byte[] { 0x71, 0x04, 0xB2, 0xAA };
        // proc (120) > P2 (20) but 78 suppressed -> a single deferred response.
        ResponseTiming.Pace(
            new ResponsePacing { ProcessingMs = 120, Timing = new TimingProfile(20, 40, 5000), Emit78 = false },
            Ch, 0x7E8, payload, send);

        var frames = WaitForCount(q, 1);
        Assert.Single(frames);
        Assert.Equal(payload, frames[0]);
    }

    [Fact]
    public void BetweenP2AndP2Star_EmitsOnePendingThenResponse()
    {
        var (q, send) = Recorder();
        var payload = new byte[] { 0x62, 0x12, 0x34 };          // positive $22
        // P2=20, P2*=200 (step=150) -> one 78 at ~20 ms, real at ~60 ms.
        ResponseTiming.Pace(new ResponsePacing { ProcessingMs = 60, Timing = new TimingProfile(20, 200, 5000) },
                            Ch, 0x7E8, payload, send);

        var frames = WaitForCount(q, 2);
        Assert.Equal(2, frames.Count);
        Assert.Equal(new byte[] { 0x7F, 0x22, 0x78 }, frames[0]);   // RCR-RP, SID from byte0 - 0x40
        Assert.Equal(payload, frames[1]);
    }

    [Fact]
    public void BeyondP2Star_EmitsMultiplePendingThenResponse()
    {
        var (q, send) = Recorder();
        var payload = new byte[] { 0x62, 0xAB, 0xCD };
        // P2=20, P2*=40 (step=30), proc=160 -> several 78s, then the real reply.
        ResponseTiming.Pace(new ResponsePacing { ProcessingMs = 160, Timing = new TimingProfile(20, 40, 5000) },
                            Ch, 0x7E8, payload, send);

        var frames = WaitForTerminal(q, payload);
        Assert.True(frames.Count >= 3, $"expected >= 2 pending + 1 real, got {frames.Count}");
        Assert.Equal(payload, frames[^1]);
        for (int i = 0; i < frames.Count - 1; i++)
            Assert.Equal(new byte[] { 0x7F, 0x22, 0x78 }, frames[i]);
    }

    [Fact]
    public void Pending_DerivesSidFromNegativeResponse()
    {
        var (q, send) = Recorder();
        var payload = new byte[] { 0x7F, 0x27, 0x33 };          // terminal NRC SAD on $27
        ResponseTiming.Pace(new ResponsePacing { ProcessingMs = 60, Timing = new TimingProfile(20, 200, 5000) },
                            Ch, 0x7E8, payload, send);

        var frames = WaitForCount(q, 2);
        Assert.Equal(new byte[] { 0x7F, 0x27, 0x78 }, frames[0]);   // SID from byte1 of the NRC
        Assert.Equal(payload, frames[1]);
    }

    [Theory]
    [InlineData((byte)0x60)]   // $20 ReturnToNormalMode positive
    [InlineData((byte)0x68)]   // $28 DisableNormalCommunication positive
    [InlineData((byte)0xE5)]   // $A5 ProgrammingMode positive
    public void ForbiddenSids_NeverGetPending_EvenWhenSlow(byte respSid)
    {
        // GMW3110 MSSC: $20 / $28 / $A5 must never be answered with a 7F sid 78.
        // A slow response to one of these is deferred whole (goes quiet), no 78.
        var (q, send) = Recorder();
        var payload = new byte[] { respSid, 0x00 };
        ResponseTiming.Pace(new ResponsePacing { ProcessingMs = 80, Timing = new TimingProfile(20, 200, 5000) },
                            Ch, 0x7E8, payload, send);

        var frames = WaitForCount(q, 1);
        Assert.Single(frames);
        Assert.Equal(payload, frames[0]);          // the positive reply, never a 78
    }

    [Fact]
    public void Cancelled_ChainStopsWithoutSending()
    {
        // isLive() going false (channel torn down / superseded) abandons the chain.
        var (q, send) = Recorder();
        bool live = true;
        ResponseTiming.Pace(new ResponsePacing { ProcessingMs = 200, Timing = new TimingProfile(20, 40, 5000) },
                            Ch, 0x7E8, new byte[] { 0x62, 0x12, 0x34 }, send, isLive: () => live);
        live = false;                              // cancel before the first step fires (~15 ms)
        Thread.Sleep(400);
        Assert.Empty(q);
    }
}

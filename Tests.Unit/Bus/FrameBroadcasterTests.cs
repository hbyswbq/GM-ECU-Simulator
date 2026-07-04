using Common.PassThru;
using Core.Bus;
using Core.Protocol;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Bus;

// Phase 6 broadcast plumbing. Covers:
//   - VirtualBus.Broadcaster is null by default (no IPC session bound).
//   - FordUdsDispatch doesn't crash when bus.Broadcaster is null and
//     EnsureBroadcastStarted fires (timer ticks become no-ops).
//   - When a IFrameBroadcaster fake is wired, the broadcast tick reaches it.
[Collection(FordUdsPersonaCollection.Name)]
public sealed class FrameBroadcasterTests
{
    private sealed class FakeBroadcaster : IFrameBroadcaster
    {
        public List<byte[]> Frames { get; } = new();
        public void BroadcastFrame(byte[] frame) { lock (Frames) Frames.Add(frame); }
    }

    [Fact]
    public void VirtualBus_Default_BroadcasterIsNull()
    {
        var bus = new VirtualBus();
        Assert.Null(bus.Broadcaster);
    }

    [Fact]
    public void FordUdsPersona_BroadcastReachesBroadcaster()
    {
        // Wire a fake broadcaster, send an $A1 to register a slot, send $A0
        // to kick off the broadcast loop, sleep a beat, expect a frame.
        // Drives the real TimerOnDelay so the test is integration-flavoured.
        FordUdsDispatch.StopBroadcast();           // clean slate
        FordUdsDispatch.ResetDmrSlotMap();
        var bus = new VirtualBus();
        var fake = new FakeBroadcaster();
        bus.Broadcaster = fake;

        var node = NodeFactory.CreateNode();
        node.PersonaId = "ford-uds";
        var ch = new ChannelSession
        {
            Id = 1, Protocol = ProtocolID.CAN, Baud = 500_000, Bus = bus,
        };

        // Bind slot 0x08 -> 0x003F86EC via $A1 (verbatim PCMTec capture).
        byte[] a1 = { 0xA1, 0x08, 0x8C, 0x00, 0x3F, 0x86, 0xEC };
        FordUdsDispatch.Instance.Dispatch(node, a1, ch, false, 0xA1, 0,
            new Core.Scheduler.DpidScheduler(bus), Common.Protocol.DiagnosticStack.Uds);
        ch.RxQueue.TryDequeue(out _); // drain echo

        // $A0 starts the broadcast.
        byte[] a0 = { 0xA0, 0x08 };
        FordUdsDispatch.Instance.Dispatch(node, a0, ch, false, 0xA0, 0,
            new Core.Scheduler.DpidScheduler(bus), Common.Protocol.DiagnosticStack.Uds);

        // Give the 100ms timer a couple of cycles to fire.
        Thread.Sleep(350);

        FordUdsDispatch.StopBroadcast();

        Assert.NotEmpty(fake.Frames);
        // Each tick emits one engine DMR frame per bound slot on 0x6A0 in the
        // tester's required rapid-packet format [07, E0, slot, <16-bit BE RPM>, ..]
        // - PLUS one engine-bus RPM broadcast on 0x97. The value bytes are live
        // (driven from the engine model) so we assert format/presence, not exact
        // RPM values here.
        var canIdsSeen = new HashSet<(byte, byte)>();
        bool sawDmrSlot = false;
        bool sawRpm = false;
        foreach (var f in fake.Frames)
        {
            Assert.Equal(12, f.Length);
            canIdsSeen.Add((f[2], f[3]));
            if (f[2] == 0x06 && f[3] == 0xA0)
            {
                Assert.Equal(0x07, f[4]); // format prefix
                Assert.Equal(0xE0, f[5]); // mandatory rapid-packet marker
                Assert.Equal(0x08, f[6]); // slot index (only slot 0x08 bound)
                sawDmrSlot = true;
            }
            else if (f[2] == 0x00 && f[3] == 0x97)
            {
                sawRpm = true;
            }
        }
        Assert.True(sawDmrSlot, "expected engine DMR broadcast on 0x6A0 with 0xE0 marker + slot 0x08");
        Assert.False(canIdsSeen.Contains(((byte)0x06, (byte)0xA4)), "must NOT emit engine data on 0x6A4 (TCM channel)");
        Assert.True(sawRpm,     "expected engine-bus RPM broadcast on 0x97");
    }

    [Fact]
    public void DmrValueBytes_TrackEngineRpm()
    {
        FordUdsDispatch.StopBroadcast();
        FordUdsDispatch.ResetDmrSlotMap();
        var bus = new VirtualBus();
        var fake = new FakeBroadcaster();
        bus.Broadcaster = fake;

        var node = NodeFactory.CreateNode();
        node.PersonaId = "ford-uds";
        node.EngineModel.SetOverride(Common.Signals.SignalId.EngineRpm, 2500);  // pin RPM exactly
        var ch = new ChannelSession { Id = 1, Protocol = ProtocolID.CAN, Baud = 500_000, Bus = bus };

        byte[] a1 = { 0xA1, 0x08, 0x8C, 0x00, 0x3F, 0x86, 0xEC };               // bind slot 0x08
        FordUdsDispatch.Instance.Dispatch(node, a1, ch, false, 0xA1, 0,
            new Core.Scheduler.DpidScheduler(bus), Common.Protocol.DiagnosticStack.Uds);
        ch.RxQueue.TryDequeue(out _);                                           // drain $A1 echo

        byte[] a0 = { 0xA0, 0x08 };                                            // start the stream
        FordUdsDispatch.Instance.Dispatch(node, a0, ch, false, 0xA0, 0,
            new Core.Scheduler.DpidScheduler(bus), Common.Protocol.DiagnosticStack.Uds);

        Thread.Sleep(250);
        FordUdsDispatch.StopBroadcast();
        node.EngineModel.ClearOverride(Common.Signals.SignalId.EngineRpm);

        // 2500 rpm -> DMR value = 32-bit big-endian IEEE-754 float 2500.0f = 0x451C4000;
        // 0x97 carries rpm*4 = 10000 = 0x2710.
        var dmr = fake.Frames.Find(f => f[2] == 0x06 && f[3] == 0xA0);
        Assert.NotNull(dmr);
        Assert.Equal(new byte[] { 0x45, 0x1C, 0x40, 0x00 }, new[] { dmr![7], dmr[8], dmr[9], dmr[10] });
        var rpm97 = fake.Frames.Find(f => f[2] == 0x00 && f[3] == 0x97);
        Assert.NotNull(rpm97);
        Assert.Equal(0x27, rpm97![4]);
        Assert.Equal(0x10, rpm97[5]);
    }

    [Fact]
    public void DmrValueBytes_UseSignalMappedToSlotAddress()
    {
        FordUdsDispatch.StopBroadcast();
        FordUdsDispatch.ResetDmrSlotMap();
        var bus = new VirtualBus();
        var fake = new FakeBroadcaster();
        bus.Broadcaster = fake;

        var node = NodeFactory.CreateNode();
        node.PersonaId = "ford-uds";
        // Map the address slot 0x08's $A1 binds (0x003F86EC) to VehicleSpeed, pinned to 120.
        node.ReplaceDmrSignalMappings(new[]
        {
            new Core.Ecu.DmrSignalMapping { Address = 0x003F86EC, Signal = Common.Signals.SignalId.VehicleSpeed },
        });
        node.EngineModel.SetOverride(Common.Signals.SignalId.VehicleSpeed, 120);
        var ch = new ChannelSession { Id = 1, Protocol = ProtocolID.CAN, Baud = 500_000, Bus = bus };

        byte[] a1 = { 0xA1, 0x08, 0x8C, 0x00, 0x3F, 0x86, 0xEC };               // slot 0x08 -> 0x003F86EC
        FordUdsDispatch.Instance.Dispatch(node, a1, ch, false, 0xA1, 0,
            new Core.Scheduler.DpidScheduler(bus), Common.Protocol.DiagnosticStack.Uds);
        ch.RxQueue.TryDequeue(out _);
        byte[] a0 = { 0xA0, 0x08 };
        FordUdsDispatch.Instance.Dispatch(node, a0, ch, false, 0xA0, 0,
            new Core.Scheduler.DpidScheduler(bus), Common.Protocol.DiagnosticStack.Uds);

        Thread.Sleep(250);
        FordUdsDispatch.StopBroadcast();
        node.EngineModel.ClearOverride(Common.Signals.SignalId.VehicleSpeed);

        // 120 km/h -> the slot carries VehicleSpeed (not RPM): BE float 120.0f = 0x42F00000.
        var dmr = fake.Frames.Find(f => f[2] == 0x06 && f[3] == 0xA0);
        Assert.NotNull(dmr);
        Assert.Equal(new byte[] { 0x42, 0xF0, 0x00, 0x00 }, new[] { dmr![7], dmr[8], dmr[9], dmr[10] });
    }

    [Fact]
    public void DmrValueBytes_ApplyEncodingScaleOffset()
    {
        FordUdsDispatch.StopBroadcast();
        FordUdsDispatch.ResetDmrSlotMap();
        var bus = new VirtualBus();
        var fake = new FakeBroadcaster();
        bus.Broadcaster = fake;

        var node = NodeFactory.CreateNode();
        node.PersonaId = "ford-uds";
        // VehicleSpeed via UInt16BE with raw = speed*2 + 10.
        node.ReplaceDmrSignalMappings(new[]
        {
            new Core.Ecu.DmrSignalMapping
            {
                Address = 0x003F86EC,
                Signal = Common.Signals.SignalId.VehicleSpeed,
                Encoding = Common.Signals.DmrValueEncoding.UInt16BE,
                Scale = 2.0,
                Offset = 10.0,
            },
        });
        node.EngineModel.SetOverride(Common.Signals.SignalId.VehicleSpeed, 100);
        var ch = new ChannelSession { Id = 1, Protocol = ProtocolID.CAN, Baud = 500_000, Bus = bus };

        byte[] a1 = { 0xA1, 0x08, 0x8C, 0x00, 0x3F, 0x86, 0xEC };
        FordUdsDispatch.Instance.Dispatch(node, a1, ch, false, 0xA1, 0,
            new Core.Scheduler.DpidScheduler(bus), Common.Protocol.DiagnosticStack.Uds);
        ch.RxQueue.TryDequeue(out _);
        byte[] a0 = { 0xA0, 0x08 };
        FordUdsDispatch.Instance.Dispatch(node, a0, ch, false, 0xA0, 0,
            new Core.Scheduler.DpidScheduler(bus), Common.Protocol.DiagnosticStack.Uds);

        Thread.Sleep(250);
        FordUdsDispatch.StopBroadcast();
        node.EngineModel.ClearOverride(Common.Signals.SignalId.VehicleSpeed);

        // raw = 100*2 + 10 = 210 = 0x00D2 (UInt16BE) -> frame[7..8] = 00 D2, frame[9..10] = 0.
        var dmr = fake.Frames.Find(f => f[2] == 0x06 && f[3] == 0xA0);
        Assert.NotNull(dmr);
        Assert.Equal(new byte[] { 0x00, 0xD2, 0x00, 0x00 }, new[] { dmr![7], dmr[8], dmr[9], dmr[10] });
    }

    [Fact]
    public void StopBroadcast_IsSafeWhenNothingRunning()
    {
        FordUdsDispatch.StopBroadcast(); // no exception
        FordUdsDispatch.StopBroadcast();
    }
}

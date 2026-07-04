using Common.PassThru;
using Core.Bus;
using Shim.Hardware;
using IntCanFrame = Core.Transport.CanFrame;

namespace Shim.Ipc;

/// <summary>
/// Third simulator transport (beside <see cref="NamedPipeServer"/> and
/// <see cref="RawCanTcpServer"/>): bridges the <see cref="VirtualBus"/> to a PHYSICAL
/// CAN bus through an <see cref="ICanAdapter"/> (Ixxat VCI4 or OBDX Pro). Lets real
/// hardware - e.g. an ESP32 CAN-Display - see the simulated ECUs' broadcast +
/// diagnostic traffic on a real wire, and lets real nodes talk back in.
///
/// <code>
///   physical bus  &lt;--ICanAdapter--&gt;  HardwareCanServer  &lt;--VirtualBus--&gt;  EcuNodes
/// </code>
///
/// Direction map:
///   adapter.FrameReceived (Rx only) -&gt; bus.DispatchHostTx   (physical -&gt; sim)
///   channel.RxQueue drain            -&gt; adapter.Send          (sim -&gt; physical)
/// Self-TX echoes (Direction==Tx) are dropped so the sim's own transmits don't
/// loop back in as inbound host frames.
/// </summary>
public sealed class HardwareCanServer : IAsyncDisposable
{
    // Single shared-bus channel id (cf. RawCanTcpServer's gauge channel). Only one
    // hardware link exists at a time, so a constant is fine.
    private const uint HwChannelId = 0x6B0F;
    private const uint HwBaud = 500_000;        // GM normal-mode broadcast rate

    private readonly VirtualBus bus;
    private readonly Action<string> log;
    private readonly Lock lifecycleLock = new();

    private ICanAdapter? adapter;
    private ChannelSession? channel;
    private SingleChannelBroadcaster? broadcaster;
    private CancellationTokenSource? cts;
    private Task? drainLoop;

    private volatile bool isConnected;
    private string deviceLabel = "";

    public HardwareCanServer(VirtualBus bus, Action<string>? log = null)
    {
        this.bus = bus;
        this.log = log ?? Console.WriteLine;
    }

    /// <summary>True while an adapter is open and bridging.</summary>
    public bool IsRunning { get { lock (lifecycleLock) return adapter != null; } }

    /// <summary>True while the underlying adapter reports a live connection.</summary>
    public bool IsConnected => isConnected;

    /// <summary>Human-readable label of the connected device (empty when stopped).</summary>
    public string DeviceLabel { get { lock (lifecycleLock) return deviceLabel; } }

    /// <summary>
    /// Open the selected device and start bridging. Idempotent: a second call while
    /// already running is a no-op. Throws if the device can't be found or opened
    /// (the caller reverts the transport selection).
    /// </summary>
    public void Start(CanAdapterKind kind, string deviceKey)
    {
        lock (lifecycleLock)
        {
            if (adapter != null) return;

            var device = ResolveDevice(kind, deviceKey);
            var a = CanAdapters.Create(kind);
            a.FrameReceived += OnAdapterFrame;
            a.BusError += OnAdapterError;
            try
            {
                a.Connect(device, CanBitRate.Br500kBit);
            }
            catch
            {
                a.FrameReceived -= OnAdapterFrame;
                a.BusError -= OnAdapterError;
                try { a.Dispose(); } catch { /* best effort */ }
                throw;
            }

            var ch = new ChannelSession
            {
                Id = HwChannelId,
                Protocol = ProtocolID.CAN,
                Baud = HwBaud,
                Bus = bus,
            };

            adapter = a;
            channel = ch;
            deviceLabel = device.ToString();

            // Deliver the unsolicited DBC/UUDT broadcast stream to this channel so
            // the physical peer sees the free-running powertrain frames, not just
            // diagnostic responses.
            broadcaster = new SingleChannelBroadcaster(ch);
            bus.Broadcaster = broadcaster;

            cts = new CancellationTokenSource();
            var token = cts.Token;
            drainLoop = Task.Run(() => DrainAsync(ch, token));

            isConnected = true;
            try { bus.RaiseHostConnected(); }
            catch (Exception ex) { log($"[hardware-can] HostConnected subscriber threw: {ex.Message}"); }
            bus.OnStatusMessage?.Invoke($"Hardware CAN connected: {deviceLabel}");
            log($"HardwareCanServer bridging via {kind}: {deviceLabel}");
        }
    }

    // OBDX keys ARE the transport spec (serial:COM5 / tcp:host:port), so a persisted
    // key that isn't currently enumerated (a COM port not yet re-plugged) is still
    // openable - build a CanDeviceInfo straight from it. Ixxat keys are VCI object
    // ids that must resolve against the live device list.
    private CanDeviceInfo ResolveDevice(CanAdapterKind kind, string deviceKey)
    {
        var found = CanAdapters.Enumerate(kind).FirstOrDefault(d => d.Key == deviceKey);
        if (found != null) return found;
        if (kind == CanAdapterKind.Obdx && !string.IsNullOrWhiteSpace(deviceKey))
            return new CanDeviceInfo(CanAdapterKind.Obdx, deviceKey, "OBDX Pro", deviceKey);
        throw new InvalidOperationException(
            $"CAN device '{deviceKey}' ({kind}) not found. Plug it in or pick another device.");
    }

    // physical -> sim. Rx only: an adapter echoes its own TX back (Ixxat
    // SelfReception; OBDX mirrors Send), and re-injecting that as an inbound host
    // frame would loop.
    private void OnAdapterFrame(CanFrame f)
    {
        if (!isConnected || f.Direction == CanDirection.Tx) return;
        var ch = channel;
        if (ch == null) return;

        var frame = new byte[IntCanFrame.IdBytes + f.Data.Length];
        IntCanFrame.WriteId(frame, f.Identifier);
        f.Data.CopyTo(frame, IntCanFrame.IdBytes);
        try { bus.DispatchHostTx(frame, ch); }
        catch (Exception ex) { log($"[hardware-can] inbound dispatch failed: {ex.Message}"); }
    }

    private void OnAdapterError(string message)
    {
        log($"[hardware-can] {message}");
        bus.OnStatusMessage?.Invoke($"Hardware CAN: {message}");
    }

    // sim -> physical. Everything the sim wants on the wire (ECU diagnostic
    // responses AND the DBC/UUDT broadcasts routed here by SingleChannelBroadcaster)
    // funnels through ch.RxQueue; this single drain loop transmits it. Single writer
    // to the adapter (its Send is internally serialised).
    private async Task DrainAsync(ChannelSession ch, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await ch.RxAvailable.WaitAsync(ct).ConfigureAwait(false);
                while (ch.RxQueue.TryDequeue(out var msg))
                {
                    if (msg.Data.Length < IntCanFrame.IdBytes) continue;   // malformed
                    var a = adapter;
                    if (a == null) return;
                    uint id = IntCanFrame.ReadId(msg.Data);
                    var payload = IntCanFrame.Payload(msg.Data).ToArray();
                    try { a.Send(id, id > 0x7FF, payload); }
                    catch (Exception ex) { log($"[hardware-can] TX failed (id 0x{id:X}): {ex.Message}"); }
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex) { log($"[hardware-can] drain error: {ex.Message}"); }
    }

    public async Task StopAsync()
    {
        ICanAdapter? a;
        Task? loop;
        CancellationTokenSource? c;
        SingleChannelBroadcaster? b;
        lock (lifecycleLock)
        {
            if (adapter == null) return;
            a = adapter; loop = drainLoop; c = cts; b = broadcaster;
            adapter = null; channel = null; drainLoop = null; cts = null; broadcaster = null;
            deviceLabel = "";
        }

        // Drop the broadcaster registration if it's still ours so a later transport
        // rebinds cleanly (mirror IpcSessionState.Dispose).
        if (ReferenceEquals(bus.Broadcaster, b))
            bus.Broadcaster = null;

        c?.Cancel();   // unblocks the drain loop's RxAvailable.WaitAsync

        if (a != null)
        {
            a.FrameReceived -= OnAdapterFrame;
            a.BusError -= OnAdapterError;
            try { a.Disconnect(); } catch { /* best effort */ }
            try { a.Dispose(); } catch { /* best effort */ }
        }

        if (loop != null)
            try { await loop.ConfigureAwait(false); } catch { /* cancellation */ }

        c?.Dispose();

        isConnected = false;
        try { bus.RaiseHostDisconnected(); }
        catch (Exception ex) { log($"[hardware-can] HostDisconnected subscriber threw: {ex.Message}"); }
        bus.OnStatusMessage?.Invoke("Hardware CAN disconnected");
        log("HardwareCanServer stopped.");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}

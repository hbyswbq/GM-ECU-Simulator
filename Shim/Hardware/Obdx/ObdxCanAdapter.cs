// Copied from CAN-Tool (src/Can/Obdx/ObdxCanAdapter.cs) - the shared CAN-adapter
// layer. DIFFERENCE FROM SOURCE: the BLE transport is not enabled in the sim
// (see ObdxTransports.cs), so EnumerateDevices omits the "ble:scan" entry and
// CreateTransport throws NotSupportedException for "ble:" keys. USB + WiFi work
// unchanged.
using System.Diagnostics;
using System.IO;
using System.IO.Ports;

namespace Shim.Hardware.Obdx;

/// <summary>
/// <see cref="ICanAdapter"/> backed by an OBDX Pro scantool over the DVI byte protocol
/// (<see cref="ObdxDvi"/>). On connect it performs the ELM->DVI handshake, puts the tool into
/// raw HS-CAN monitor + faithful-TX mode (<see cref="ObdxDvi.RawCanInit"/>), then pumps the
/// inbound stream into <see cref="ObdxFrameParser"/> and republishes CAN frames.
///
/// Transport (USB serial / WiFi TCP) is chosen from the device key; all DVI framing lives in
/// <see cref="ObdxDvi"/>. Timestamps are taken on arrival (RX-timestamp option left off), and
/// extended is inferred from the ID, matching the Ixxat/legacy path.
/// </summary>
public sealed class ObdxCanAdapter : ICanAdapter
{
    private readonly object _writeLock = new();   // serialises transport writes
    private readonly object _cyclicLock = new();  // guards the software-timer table
    private readonly Stopwatch _clock = new();
    private readonly Dictionary<int, Timer> _cyclic = [];
    private int _nextCyclicHandle;

    private IObdxTransport? _transport;
    private Thread? _rxThread;
    private volatile bool _running;

    public event Action<CanFrame>? FrameReceived;
    public event Action<string>? BusError;

    public bool IsConnected { get; private set; }

    // The OBDX has 8 hardware periodic-frame slots; wiring those up is a future optimisation.
    // For now cyclic TX uses a software timer over the (proven) Send path.
    public bool SupportsScheduler => false;

    /// <summary>
    /// List the OBDX candidates: each USB serial port and the WiFi SoftAP (192.168.4.1:23).
    /// (BLE is not enabled in this build - see the file header.)
    /// </summary>
    public static IReadOnlyList<CanDeviceInfo> EnumerateDevices()
    {
        var list = new List<CanDeviceInfo>();
        foreach (string port in SerialPort.GetPortNames().Distinct().OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            list.Add(new CanDeviceInfo(CanAdapterKind.Obdx, $"serial:{port}", "OBDX Pro (USB)", port));

        // OBDX WiFi SoftAP (no password) - fixed endpoint from the Wireless guide.
        list.Add(new CanDeviceInfo(CanAdapterKind.Obdx, "tcp:192.168.4.1:23", "OBDX Pro (WiFi)", "192.168.4.1:23"));
        return list;
    }

    public void Connect(CanDeviceInfo device, CanBitRate bitRate, bool listenOnly = false)
    {
        if (IsConnected)
            throw new InvalidOperationException("Already connected.");
        if (device.Adapter != CanAdapterKind.Obdx)
            throw new ArgumentException($"Not an OBDX device: {device.Adapter}.", nameof(device));

        byte baudCode = ObdxDvi.BaudCode(bitRate); // throws early on an unsupported rate
        _transport = CreateTransport(device.Key);
        try
        {
            _transport.Open();
            Handshake();

            // Send the config commands first (protocol, baud, filters, raw-mode flags), then
            // confirm the tool didn't reject any before comms are enabled. The final command is
            // the comms-enable, held back until the RX pump is running so no frame is missed.
            var seq = ObdxDvi.RawCanInit(baudCode, listenOnly);
            for (int i = 0; i < seq.Count - 1; i++)
                WriteRaw(seq[i]);
            VerifyInit(expectedAcks: seq.Count - 1, timeoutMs: 500);

            _clock.Restart();
            _running = true;
            _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "Obdx-Rx" };
            _rxThread.Start();

            WriteRaw(seq[^1]); // enable comms - frames now flow to the RX pump
            IsConnected = true;
        }
        catch
        {
            _running = false;
            SafeCloseTransport();
            throw;
        }
    }

    /// <summary>
    /// Parse a device key into a transport: <c>serial:COM5</c> or <c>tcp:host:port</c>.
    /// <c>ble:</c> keys are rejected - the BLE transport is not enabled in this build.
    /// </summary>
    private static IObdxTransport CreateTransport(string key)
    {
        if (key.StartsWith("serial:", StringComparison.OrdinalIgnoreCase))
            return new SerialObdxTransport(key["serial:".Length..]);

        if (key.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            string rest = key["tcp:".Length..];
            int colon = rest.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(rest[(colon + 1)..], out int port))
                throw new ArgumentException($"Bad OBDX key '{key}'. Expected tcp:host:port.");
            return new TcpObdxTransport(rest[..colon], port);
        }

        if (key.StartsWith("ble:", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                "OBDX BLE is not enabled in the simulator build (it needs the WinRT Bluetooth TFM). " +
                "Use a USB (serial:) or WiFi (tcp:) OBDX connection.");

        throw new ArgumentException($"Unsupported OBDX device key '{key}'.");
    }

    /// <summary>
    /// Drain the tool's config-command acks after init. Returns once <paramref name="expectedAcks"/>
    /// responses have arrived or the timeout elapses; throws if the tool reports an error frame, so a
    /// rejected protocol/baud/filter fails Connect loudly instead of silently reading nothing. (If the
    /// tool has config responses disabled, no acks arrive and it simply waits out the timeout.)
    /// </summary>
    private void VerifyInit(int expectedAcks, int timeoutMs)
    {
        var parser = new ObdxFrameParser();
        var buf = new byte[512];
        int acks = 0;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs && acks < expectedAcks)
        {
            int n = _transport!.Read(buf);
            if (n <= 0)
                continue;
            parser.Append(buf.AsSpan(0, n));
            while (parser.TryRead(out DviMessage msg))
            {
                if (msg.Command == ObdxDvi.CmdError)
                    throw new IOException(
                        $"OBDX rejected init (error 0x{(msg.Data.Length >= 2 ? msg.Data[1] : 0):X2}).");
                // 0x41 responds to 0x31 (set protocol/comms); 0x44 responds to 0x34 (CAN settings).
                if (msg.Command is 0x41 or 0x44)
                    acks++;
            }
        }
    }

    /// <summary>ELM->DVI: turn echo off, switch to the byte protocol, and drain the ASCII replies.</summary>
    private void Handshake()
    {
        WriteRaw(ObdxDvi.EchoOffAscii());
        DrainAscii(300);
        WriteRaw(ObdxDvi.EnterDviAscii());
        DrainAscii(300);
    }

    /// <summary>Consume the ELM ASCII reply, stopping at the idle '&gt;' prompt or after a timeout.</summary>
    private void DrainAscii(int timeoutMs)
    {
        var buf = new byte[256];
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            int n = _transport!.Read(buf);
            for (int i = 0; i < n; i++)
                if (buf[i] == (byte)'>')
                    return;
        }
    }

    private void WriteRaw(byte[] data)
    {
        lock (_writeLock)
            _transport!.Write(data);
    }

    private void ReceiveLoop()
    {
        var parser = new ObdxFrameParser();
        var buf = new byte[2048];
        try
        {
            while (_running)
            {
                int n = _transport!.Read(buf);
                if (n <= 0)
                    continue; // idle read timeout - loop to re-check _running
                parser.Append(buf.AsSpan(0, n));
                while (parser.TryRead(out DviMessage msg))
                    Dispatch(msg);
            }
        }
        catch (Exception ex) when (_running)
        {
            BusError?.Invoke("OBDX RX stopped: " + ex.Message);
        }
    }

    private void Dispatch(in DviMessage msg)
    {
        if (msg.Command == ObdxDvi.CmdError)
        {
            byte code = msg.Data.Length >= 2 ? msg.Data[1] : (byte)0;
            BusError?.Invoke($"OBDX error frame (code 0x{code:X2}) @ {_clock.Elapsed.TotalSeconds:F3}s");
            return;
        }

        if (ObdxDvi.TryParseCanRx(msg, out uint id, out bool extended, out byte[] data))
        {
            FrameReceived?.Invoke(new CanFrame
            {
                TimeStamp = _clock.Elapsed.TotalSeconds,
                Direction = CanDirection.Rx,
                Identifier = id,
                IsExtended = extended,
                Data = data
            });
        }
        // Other command responses (acks, config echoes) are not surfaced.
    }

    public void Send(uint identifier, bool extended, byte[] data, bool remote = false)
    {
        if (!IsConnected || _transport is null)
            throw new InvalidOperationException("Not connected.");
        if (data.Length > 8)
            throw new ArgumentException("Classic CAN allows at most 8 data bytes.", nameof(data));

        WriteRaw(ObdxDvi.SendCanFrame(identifier, data));

        // OBDX gives no self-reception, so mirror the TX into the trace like the VCI path does.
        FrameReceived?.Invoke(new CanFrame
        {
            TimeStamp = _clock.Elapsed.TotalSeconds,
            Direction = CanDirection.Tx,
            Identifier = identifier,
            IsExtended = extended,
            Data = (byte[])data.Clone()
        });
    }

    // ---- Cyclic transmit (software timer; hardware periodic frames are a future optimisation) ----

    public int StartCyclic(uint identifier, bool extended, byte[] data, bool remote, double intervalMs)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Not connected.");
        if (data.Length > 8)
            throw new ArgumentException("Classic CAN allows at most 8 data bytes.", nameof(data));

        byte[] payload = (byte[])data.Clone();
        int period = (int)Math.Max(1, Math.Round(intervalMs));
        lock (_cyclicLock)
        {
            int handle = _nextCyclicHandle++;
            _cyclic[handle] = new Timer(
                _ => CyclicTick(handle, identifier, extended, payload, remote), null, 0, period);
            return handle;
        }
    }

    private void CyclicTick(int handle, uint id, bool extended, byte[] data, bool remote)
    {
        try
        {
            Send(id, extended, data, remote);
        }
        catch (Exception ex)
        {
            StopCyclic(handle);
            BusError?.Invoke("Cyclic transmit stopped: " + ex.Message);
        }
    }

    public void StopCyclic(int handle)
    {
        lock (_cyclicLock)
            if (_cyclic.Remove(handle, out Timer? timer))
                timer.Dispose();
    }

    public void StopAllCyclic()
    {
        lock (_cyclicLock)
        {
            foreach (Timer timer in _cyclic.Values)
                timer.Dispose();
            _cyclic.Clear();
        }
    }

    public void Disconnect()
    {
        _running = false;
        StopAllCyclic();

        if (_rxThread is { } t && t.IsAlive && t != Thread.CurrentThread)
            t.Join(TimeSpan.FromSeconds(2));
        _rxThread = null;

        if (_transport is not null && IsConnected)
        {
            try { WriteRaw(ObdxDvi.SetComms(ObdxDvi.CommsOff)); } catch { /* best effort */ }
        }

        SafeCloseTransport();
        IsConnected = false;
    }

    private void SafeCloseTransport()
    {
        try { _transport?.Dispose(); } catch { /* best effort */ }
        _transport = null;
    }

    public void Dispose() => Disconnect();
}

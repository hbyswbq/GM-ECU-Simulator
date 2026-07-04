// Copied from CAN-Tool (src/Can/CanBusService.cs) - the shared CAN-adapter layer.
// The Ixxat VCI4 backend. Requires the Ixxat.Vci4 NuGet at build time and the
// native VCI driver (C:\Program Files\HMS\Ixxat VCI) at run time.
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using Ixxat.Vci4;
using Ixxat.Vci4.Bal;
using Ixxat.Vci4.Bal.Can;

namespace Shim.Hardware;

/// <summary>
/// Supported CAN bit rates, mapped to the VCI CiA predefined timings.
/// </summary>
public enum CanBitRate
{
    Br10kBit,
    Br20kBit,
    Br50kBit,
    Br100kBit,
    Br125kBit,
    Br250kBit,
    Br500kBit,
    Br800kBit,
    Br1000kBit
}

/// <summary>
/// Thin wrapper over the Ixxat VCI4 .NET API. Owns the device / BAL / channel
/// lifetime and runs a dedicated background thread that drains received frames
/// and republishes them through <see cref="FrameReceived"/>.
///
/// All vendor (Ixxat.Vci4.*) types are confined to this file.
/// </summary>
public sealed class CanBusService : ICanAdapter
{
    private readonly object _sync = new();

    private IVciDevice? _device;
    private IBalObject? _bal;
    private ICanControl? _control;
    private ICanChannel? _channel;
    private ICanMessageReader? _reader;
    private ICanMessageWriter? _writer;
    private ICanScheduler? _scheduler;
    private AutoResetEvent? _rxEvent;

    private readonly Dictionary<int, ICanCyclicTXMsg> _cyclic = new();
    private readonly Dictionary<int, System.Threading.Timer> _softCyclic = new();
    private int _nextCyclicHandle;

    private Thread? _rxThread;
    private volatile bool _running;
    private readonly Stopwatch _clock = new();

    /// <summary>Raised on the RX thread for every received (or self-received) frame.</summary>
    public event Action<CanFrame>? FrameReceived;

    /// <summary>Raised on the RX thread when the bus signals an error frame / state change.</summary>
    public event Action<string>? BusError;

    public bool IsConnected { get; private set; }

    /// <summary>Enumerate all VCI devices currently plugged in.</summary>
    public static IReadOnlyList<CanDeviceInfo> EnumerateDevices()
    {
        var result = new List<CanDeviceInfo>();
        IVciDeviceManager? manager = null;
        IVciDeviceList? list = null;
        try
        {
            manager = VciServer.Instance()!.DeviceManager;
            list = manager.GetDeviceList();
            IEnumerator e = list.GetEnumerator();
            while (e.MoveNext())
            {
                var device = (IVciDevice)e.Current;
                try
                {
                    result.Add(new CanDeviceInfo(
                        Adapter: CanAdapterKind.IxxatVci,
                        Key: device.VciObjectId.ToString(CultureInfo.InvariantCulture),
                        Description: device.Description,
                        Detail: device.UniqueHardwareId?.ToString() ?? "-"));
                }
                finally
                {
                    device.Dispose();
                }
            }
        }
        finally
        {
            list?.Dispose();
            manager?.Dispose();
        }
        return result;
    }

    /// <summary>Open the given device's CAN line and start receiving.</summary>
    public void Connect(CanDeviceInfo device, CanBitRate bitRate, bool listenOnly = false)
    {
        if (device.Adapter != CanAdapterKind.IxxatVci)
            throw new ArgumentException($"Not an Ixxat VCI device: {device.Adapter}.", nameof(device));
        long deviceId = long.Parse(device.Key, CultureInfo.InvariantCulture);
        const int canLine = 0; // single-channel adapters

        lock (_sync)
        {
            if (IsConnected)
                throw new InvalidOperationException("Already connected.");

            IVciDeviceManager? manager = null;
            IVciDeviceList? list = null;
            try
            {
                manager = VciServer.Instance()!.DeviceManager;
                list = manager.GetDeviceList();
                _device = OpenDeviceById(list, deviceId)
                          ?? throw new InvalidOperationException("Device no longer present.");
                _bal = _device.OpenBusAccessLayer();

                _control = (ICanControl)_bal.OpenSocket((byte)canLine, typeof(ICanControl));
                _channel = (ICanChannel)_bal.OpenSocket((byte)canLine, typeof(ICanChannel));

                // receiveFifoSize, transmitFifoSize, exclusive
                _channel.Initialize(1024, 128, false);

                _reader = _channel.GetMessageReader();
                _reader.Threshold = 1;
                _rxEvent = new AutoResetEvent(false);
                _reader.AssignEvent(_rxEvent);

                _writer = _channel.GetMessageWriter();
                _writer.Threshold = 1;

                _channel.Activate();

                var modes = CanOperatingModes.Standard
                            | CanOperatingModes.Extended
                            | CanOperatingModes.ErrFrame;
                if (listenOnly)
                    modes |= CanOperatingModes.ListenOnly;

                _control.InitLine(modes, ToVciBitrate(bitRate));
                // Accept everything; filtering happens on the sim side.
                _control.SetAccFilter(CanFilter.Std, (uint)CanAccCode.All, (uint)CanAccMask.All);
                _control.SetAccFilter(CanFilter.Ext, (uint)CanAccCode.All, (uint)CanAccMask.All);
                _control.StartLine();

                // Open the hardware cyclic-message scheduler if the adapter
                // supports it; used for jitter-free repeat transmission.
                if (((ICanSocket)_control).SupportsCyclicMessageScheduler)
                {
                    _scheduler = (ICanScheduler)_bal.OpenSocket((byte)canLine, typeof(ICanScheduler));
                    _scheduler.Reset();
                    _scheduler.Resume();
                }

                _clock.Restart();
                _running = true;
                _rxThread = new Thread(ReceiveLoop)
                {
                    IsBackground = true,
                    Name = "Vci-Rx"
                };
                _rxThread.Start();

                IsConnected = true;
            }
            catch
            {
                CleanupUnlocked();
                throw;
            }
            finally
            {
                list?.Dispose();
                manager?.Dispose();
            }
        }
    }

    /// <summary>
    /// The VCI device manager has no "open by id"; instead we walk the device
    /// list, keep the device whose <see cref="IVciDevice.VciObjectId"/> matches,
    /// and dispose the rest.
    /// </summary>
    private static IVciDevice? OpenDeviceById(IVciDeviceList list, long deviceId)
    {
        IVciDevice? match = null;
        IEnumerator e = ((IEnumerable)list).GetEnumerator();
        while (e.MoveNext())
        {
            var device = (IVciDevice)e.Current;
            if (match is null && device.VciObjectId == deviceId)
                match = device;
            else
                device.Dispose();
        }
        return match;
    }

    public void Disconnect()
    {
        lock (_sync)
        {
            CleanupUnlocked();
        }
    }

    /// <summary>Transmit a single CAN frame.</summary>
    public void Send(uint identifier, bool extended, byte[] data, bool remote = false)
    {
        if (data.Length > 8)
            throw new ArgumentException("Classic CAN allows at most 8 data bytes.", nameof(data));

        ICanMessageWriter writer;
        lock (_sync)
        {
            if (!IsConnected || _writer is null)
                throw new InvalidOperationException("Not connected.");
            writer = _writer;
        }

        var factory = VciServer.Instance()!.MsgFactory;
        var msg = (ICanMessage)factory.CreateMsg(typeof(ICanMessage));
        msg.TimeStamp = 0;
        msg.Identifier = identifier;
        msg.FrameType = CanMsgFrameType.Data;
        msg.ExtendedFrameFormat = extended;
        msg.RemoteTransmissionRequest = remote;
        msg.SelfReceptionRequest = true; // so the frame also appears in the RX trace
        msg.DataLength = (byte)data.Length;
        for (int i = 0; i < data.Length; i++)
            msg[i] = data[i];

        writer.SendMessage(msg);
    }

    /// <summary>True when the connected adapter has a hardware cyclic scheduler.</summary>
    public bool SupportsScheduler
    {
        get { lock (_sync) return _scheduler is not null; }
    }

    /// <summary>
    /// Start a cyclic transmit message and return a handle for <see cref="StopCyclic"/>.
    /// Uses the adapter's hardware scheduler when available (jitter-free); otherwise
    /// falls back to a software timer that re-sends the frame.
    /// </summary>
    public int StartCyclic(uint identifier, bool extended, byte[] data, bool remote, double intervalMs)
    {
        if (data.Length > 8)
            throw new ArgumentException("Classic CAN allows at most 8 data bytes.", nameof(data));

        lock (_sync)
        {
            if (!IsConnected)
                throw new InvalidOperationException("Not connected.");

            int handle = _nextCyclicHandle++;

            if (_scheduler is not null)
            {
                var msg = _scheduler.AddMessage();
                msg.Identifier = identifier;
                msg.FrameType = CanMsgFrameType.Data;
                msg.ExtendedFrameFormat = extended;
                msg.RemoteTransmissionRequest = remote;
                msg.SelfReceptionRequest = true; // echo into the RX trace
                msg.DataLength = (byte)data.Length;
                for (int i = 0; i < data.Length; i++)
                    msg[i] = data[i];

                msg.AutoIncrementMode = CanCyclicTXIncMode.NoInc;
                msg.CycleTicks = IntervalToTicks(intervalMs);
                msg.Start(0); // 0 = transmit continuously
                _scheduler.Resume();

                _cyclic[handle] = msg;
            }
            else
            {
                // Software fallback: copy the payload so the caller can't mutate it.
                byte[] payload = (byte[])data.Clone();
                int period = (int)Math.Max(1, Math.Round(intervalMs));
                _softCyclic[handle] = new System.Threading.Timer(
                    _ => SoftCyclicTick(handle, identifier, extended, payload, remote),
                    null, 0, period);
            }

            return handle;
        }
    }

    private void SoftCyclicTick(int handle, uint id, bool extended, byte[] data, bool remote)
    {
        try
        {
            Send(id, extended, data, remote);
        }
        catch (Exception ex)
        {
            // Bus dropped or disconnected: stop this stream and report once.
            StopCyclic(handle);
            BusError?.Invoke("Cyclic transmit stopped: " + ex.Message);
        }
    }

    public void StopCyclic(int handle)
    {
        lock (_sync)
        {
            if (_cyclic.Remove(handle, out var msg))
                try { msg.Stop(); } catch { /* best effort */ }
            if (_softCyclic.Remove(handle, out var timer))
                timer.Dispose();
        }
    }

    public void StopAllCyclic()
    {
        lock (_sync)
        {
            StopAllCyclicUnlocked();
        }
    }

    private void StopAllCyclicUnlocked()
    {
        foreach (var msg in _cyclic.Values)
            try { msg.Stop(); } catch { /* best effort */ }
        _cyclic.Clear();

        foreach (var timer in _softCyclic.Values)
            timer.Dispose();
        _softCyclic.Clear();
    }

    /// <summary>Convert a millisecond period to scheduler ticks for this adapter.</summary>
    private ushort IntervalToTicks(double intervalMs)
    {
        var socket = (ICanSocket)_scheduler!;
        double ticksPerSecond = (double)socket.ClockFrequency / socket.CyclicMessageTimerDivisor;
        long ticks = (long)Math.Round(intervalMs / 1000.0 * ticksPerSecond);
        long max = Math.Min(socket.MaxCyclicMessageTicks, ushort.MaxValue);
        return (ushort)Math.Clamp(ticks, 1, max);
    }

    private void ReceiveLoop()
    {
        var rxEvent = _rxEvent!;
        var reader = _reader!;
        try
        {
            while (_running)
            {
                if (!rxEvent.WaitOne(100))
                    continue;

                while (reader.ReadMessage(out ICanMessage msg))
                {
                    if (!_running)
                        return;

                    switch (msg.FrameType)
                    {
                        case CanMsgFrameType.Data:
                            FrameReceived?.Invoke(ToFrame(msg, CanDirection.Rx));
                            break;
                        case CanMsgFrameType.Error:
                            BusError?.Invoke($"Bus error frame @ {_clock.Elapsed.TotalSeconds:F3}s");
                            break;
                    }
                }
            }
        }
        catch (Exception ex) when (_running)
        {
            BusError?.Invoke("RX loop stopped: " + ex.Message);
        }
    }

    private CanFrame ToFrame(ICanMessage msg, CanDirection direction)
    {
        var len = msg.DataLength;
        var data = new byte[len];
        for (int i = 0; i < len; i++)
            data[i] = msg[i];

        return new CanFrame
        {
            TimeStamp = _clock.Elapsed.TotalSeconds,
            Direction = msg.SelfReceptionRequest ? CanDirection.Tx : direction,
            Identifier = msg.Identifier,
            IsExtended = msg.ExtendedFrameFormat,
            IsRemote = msg.RemoteTransmissionRequest,
            Data = data
        };
    }

    private static CanBitrate ToVciBitrate(CanBitRate br) => br switch
    {
        CanBitRate.Br10kBit => CanBitrate.Cia10KBit,
        CanBitRate.Br20kBit => CanBitrate.Cia20KBit,
        CanBitRate.Br50kBit => CanBitrate.Cia50KBit,
        CanBitRate.Br100kBit => CanBitrate._100KBit,
        CanBitRate.Br125kBit => CanBitrate.Cia125KBit,
        CanBitRate.Br250kBit => CanBitrate.Cia250KBit,
        CanBitRate.Br500kBit => CanBitrate.Cia500KBit,
        CanBitRate.Br800kBit => CanBitrate.Cia800KBit,
        CanBitRate.Br1000kBit => CanBitrate.Cia1000KBit,
        _ => CanBitrate.Cia500KBit
    };

    private void CleanupUnlocked()
    {
        _running = false;
        IsConnected = false;

        // Wake the RX thread so it can observe _running == false and exit.
        _rxEvent?.Set();
        if (_rxThread is { } t && t.IsAlive && t != Thread.CurrentThread)
            t.Join(TimeSpan.FromSeconds(2));
        _rxThread = null;

        StopAllCyclicUnlocked();
        try { _scheduler?.Reset(); } catch { /* best effort */ }
        try { _control?.StopLine(); } catch { /* best effort */ }

        _scheduler?.Dispose();
        _writer?.Dispose();
        _reader?.Dispose();
        _channel?.Dispose();
        _control?.Dispose();
        _bal?.Dispose();
        _device?.Dispose();
        _rxEvent?.Dispose();

        _scheduler = null;
        _writer = null;
        _reader = null;
        _channel = null;
        _control = null;
        _bal = null;
        _device = null;
        _rxEvent = null;
    }

    public void Dispose() => Disconnect();
}

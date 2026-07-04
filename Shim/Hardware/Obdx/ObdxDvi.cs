// Copied from CAN-Tool (src/Can/Obdx/ObdxDvi.cs) - the shared CAN-adapter layer.
using System.Text;

namespace Shim.Hardware.Obdx;

/// <summary>
/// OBDX Pro <b>DVI</b> (Direct Vehicle Interface) byte protocol - the fast binary
/// command set documented in the OBDX Pro Command Set Reference Guide. Every frame is
/// <c>[command][length][data...][checksum]</c>; the length counts only the data bytes and
/// the two "large" commands (0x09 receive, 0x11 send) use a 16-bit big-endian length.
///
/// This type is pure (no I/O): it builds command frames and parses the inbound byte
/// stream. A transport (serial / TCP) feeds bytes to <see cref="ObdxFrameParser"/>
/// and writes the byte[]s produced by the builders. Verified against the manual's worked
/// examples (see the ObdxDvi tests in CAN-Tool).
/// </summary>
public static class ObdxDvi
{
    // Command bytes (PC -> tool request; tool -> PC responses are request + 0x10, except 0x08/0x09/0x7F/0x06).
    public const byte CmdReceiveNormal = 0x08; // async: a CAN frame received from the bus
    public const byte CmdReceiveLarge = 0x09;  // async: a received frame with a 16-bit length
    public const byte CmdSendNormal = 0x10;    // write a frame to the bus
    public const byte CmdWriteAck = 0x20;      // tool ack of a successful write (0x10 + 0x10)
    public const byte CmdScantoolInfo = 0x22;
    public const byte CmdSettings = 0x24;
    public const byte CmdSetProtocol = 0x31;
    public const byte CmdCanSettings = 0x34;
    public const byte CmdError = 0x7F;         // fault: 7F 02 <cmd> <code> YY
    public const byte CmdNotify = 0x06;        // flow-control-incoming notification

    // OBD protocol selector for 0x31 sub 0x01.
    public const byte ProtoHsCan = 0x02;
    public const byte ProtoGmCan = 0x03; // GMLAN single-wire

    // Network communication mode for 0x31 sub 0x02.
    public const byte CommsOff = 0x00;
    public const byte CommsOn = 0x01;
    public const byte CommsListenOnly = 0x02;

    /// <summary>The one ASCII command that switches the tool from ELM into DVI byte mode.</summary>
    public static byte[] EnterDviAscii() => Encoding.ASCII.GetBytes("DXDP1\r");

    /// <summary>ELM echo-off, sent before <see cref="EnterDviAscii"/> to simplify the handshake.</summary>
    public static byte[] EchoOffAscii() => Encoding.ASCII.GetBytes("ATE0\r");

    /// <summary>DVI checksum: bitwise-NOT of the low byte of the sum of every preceding byte.</summary>
    public static byte Checksum(ReadOnlySpan<byte> bytes)
    {
        int sum = 0;
        foreach (byte b in bytes)
            sum += b;
        return (byte)(~sum & 0xFF);
    }

    /// <summary>
    /// Assemble a single-byte-length command: <c>[cmd][len][data...][checksum]</c>.
    /// All PC->tool commands used here fit the 1-byte length form (data &lt;= 255 bytes).
    /// </summary>
    public static byte[] Command(byte cmd, params byte[] data)
    {
        var frame = new byte[data.Length + 3];
        frame[0] = cmd;
        frame[1] = (byte)data.Length;
        Array.Copy(data, 0, frame, 2, data.Length);
        frame[^1] = Checksum(frame.AsSpan(0, frame.Length - 1));
        return frame;
    }

    // ---- Configuration command builders (sub-commands per the DVI manual) ----

    /// <summary>0x31/0x01 - set the OBD protocol (0x02 = HS CAN, 0x03 = GM CAN).</summary>
    public static byte[] SetProtocol(byte proto) => Command(CmdSetProtocol, 0x01, proto);

    /// <summary>0x31/0x02 - enable/disable network comms (Off / On / ListenOnly). Frames flow once On.</summary>
    public static byte[] SetComms(byte mode) => Command(CmdSetProtocol, 0x02, mode);

    /// <summary>0x34/0x15 - predefined CAN baud (see <see cref="BaudCode"/>; 0x06 = 500 kbit/s).</summary>
    public static byte[] SetCanBaud(byte code) => Command(CmdCanSettings, 0x15, code);

    /// <summary>0x34/0x24 - clear all CAN filters (with none set + comms On, the tool passes every frame).</summary>
    public static byte[] ClearAllFilters() => Command(CmdCanSettings, 0x24);

    /// <summary>0x34/0x0B - automatic ISO-TP reassembly of received frames. Turn OFF for raw sniffing.</summary>
    public static byte[] SetAutoProcessing(bool on) => Command(CmdCanSettings, 0x0B, (byte)(on ? 1 : 0));

    /// <summary>0x34/0x0F - auto length/flow-control formatting on write. Turn OFF to send exact raw bytes.</summary>
    public static byte[] SetAutoFormatting(bool on) => Command(CmdCanSettings, 0x0F, (byte)(on ? 1 : 0));

    /// <summary>0x10 - write a raw CAN frame (32-bit big-endian ID + data). Auto-formatting must be OFF.</summary>
    public static byte[] SendCanFrame(uint id, ReadOnlySpan<byte> data)
    {
        var payload = new byte[4 + data.Length];
        payload[0] = (byte)(id >> 24);
        payload[1] = (byte)(id >> 16);
        payload[2] = (byte)(id >> 8);
        payload[3] = (byte)id;
        data.CopyTo(payload.AsSpan(4));
        return Command(CmdSendNormal, payload);
    }

    /// <summary>
    /// The command sequence that puts the tool into "raw monitor + faithful TX" for a HS-CAN bus:
    /// set protocol -> baud -> clear filters (monitor all) -> disable ISO-TP reassembly -> disable write
    /// formatting -> enable comms. Send these (in order) after the ELM->DVI handshake.
    /// </summary>
    public static IReadOnlyList<byte[]> RawCanInit(byte baudCode, bool listenOnly) =>
    [
        SetProtocol(ProtoHsCan),
        SetCanBaud(baudCode),
        ClearAllFilters(),
        SetAutoProcessing(false),
        SetAutoFormatting(false),
        SetComms(listenOnly ? CommsListenOnly : CommsOn),
    ];

    /// <summary>Map a bit rate to the OBDX predefined baud code (0x34/0x15). Throws if unsupported.</summary>
    public static byte BaudCode(CanBitRate bitRate) => bitRate switch
    {
        CanBitRate.Br50kBit => 0x01,
        CanBitRate.Br100kBit => 0x03,
        CanBitRate.Br125kBit => 0x04,
        CanBitRate.Br250kBit => 0x05,
        CanBitRate.Br500kBit => 0x06,
        CanBitRate.Br1000kBit => 0x07,
        _ => throw new NotSupportedException($"OBDX has no predefined baud for {bitRate}.")
    };

    /// <summary>
    /// Interpret a received-network message (0x08 / 0x09) as a raw CAN frame. Assumes the RX
    /// timestamp and IDE bytes are OFF (defaults), so the data is <c>[ID x4][payload...]</c>.
    /// </summary>
    public static bool TryParseCanRx(in DviMessage m, out uint id, out bool extended, out byte[] data)
    {
        id = 0;
        extended = false;
        data = [];
        if (m.Command is not (CmdReceiveNormal or CmdReceiveLarge) || m.Data.Length < 4)
            return false;

        id = (uint)(m.Data[0] << 24 | m.Data[1] << 16 | m.Data[2] << 8 | m.Data[3]);
        extended = id > 0x7FF; // a standard frame can't exceed 0x7FF; matches the VCI/legacy heuristic
        data = m.Data[4..];
        return true;
    }
}

/// <summary>One decoded DVI message: its command byte and the data bytes (length + checksum stripped).</summary>
public readonly record struct DviMessage(byte Command, byte[] Data);

/// <summary>
/// Incremental parser for the inbound DVI byte stream. Feed it whatever bytes arrive from the
/// transport with <see cref="Append"/>, then drain complete messages with <see cref="TryRead"/>.
/// Frames are <c>[cmd][len][data][checksum]</c>, with a 16-bit length for the 0x09/0x11 commands.
/// A bad checksum drops one byte and resyncs so a single glitch can't wedge the stream.
/// </summary>
public sealed class ObdxFrameParser
{
    // Unread bytes live in _buf[_head.._tail). Consuming advances _head (O(1)); the buffer
    // is compacted (shifted to the front) only when it fills or fully drains - so sustained
    // high-rate parsing stays O(1) amortised instead of the O(n) shift a List.RemoveRange costs.
    private byte[] _buf = new byte[4096];
    private int _head;
    private int _tail;

    private int Available => _tail - _head;

    public void Append(ReadOnlySpan<byte> bytes)
    {
        EnsureRoom(bytes.Length);
        bytes.CopyTo(_buf.AsSpan(_tail));
        _tail += bytes.Length;
    }

    /// <summary>True if a complete, checksum-valid message was dequeued into <paramref name="msg"/>.</summary>
    public bool TryRead(out DviMessage msg)
    {
        msg = default;
        while (Available >= 2)
        {
            byte cmd = _buf[_head];
            int lenSize = cmd is ObdxDvi.CmdReceiveLarge or 0x11 ? 2 : 1;
            if (Available < 1 + lenSize)
                break; // need the length field

            int dataLen = lenSize == 1
                ? _buf[_head + 1]
                : _buf[_head + 1] << 8 | _buf[_head + 2];
            int total = 1 + lenSize + dataLen + 1; // cmd + len field + data + checksum
            if (Available < total)
                break; // wait for the rest of the frame

            byte expected = ObdxDvi.Checksum(_buf.AsSpan(_head, total - 1));
            if (_buf[_head + total - 1] != expected)
            {
                _head++; // resync past the bad byte and try again
                continue;
            }

            var data = new byte[dataLen];
            Array.Copy(_buf, _head + 1 + lenSize, data, 0, dataLen);
            _head += total;
            msg = new DviMessage(cmd, data);
            return true;
        }

        if (_head == _tail)
            _head = _tail = 0; // fully drained - reset cheaply
        return false;
    }

    private void EnsureRoom(int extra)
    {
        if (_tail + extra <= _buf.Length)
            return;

        // Reclaim consumed space first; only grow if the live window still won't fit.
        if (_head > 0)
        {
            int live = Available;
            if (live > 0)
                Array.Copy(_buf, _head, _buf, 0, live);
            _head = 0;
            _tail = live;
        }
        if (_tail + extra > _buf.Length)
            Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _tail + extra));
    }
}

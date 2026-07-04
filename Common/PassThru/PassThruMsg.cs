namespace Common.PassThru;

// J2534 PASSTHRU_MSG. Layout matches the C struct verbatim - header is fixed
// 24 bytes, followed by up to 4128 data bytes. We never marshal the full 4128
// across IPC; only the prefix [0..DataSize) is transmitted.
public sealed class PassThruMsg
{
    public const int MaxDataSize = 4128;

    public ProtocolID ProtocolID;
    public RxStatus RxStatus;
    public TxFlag TxFlags;
    public uint Timestamp;          // microseconds since channel open
    public uint ExtraDataIndex;
    public byte[] Data = [];        // Length == DataSize on the wire

    // In-memory only (never marshalled across IPC): set when this frame was
    // pushed via IFrameBroadcaster.BroadcastFrame - i.e. unsolicited broadcast
    // traffic (DBC scheduler or a protocol stack's UUDT stream) rather than a directed
    // diagnostic response. Lets the UI's "Hide broadcasts" filter drop it from
    // the live log regardless of CAN ID. Delivery to the host is unaffected.
    public bool IsBroadcast;

    public uint DataSize => (uint)Data.Length;
}

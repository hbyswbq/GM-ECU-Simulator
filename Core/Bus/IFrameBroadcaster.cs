namespace Core.Bus;

// Cross-channel raw-CAN broadcast hook. The simulator's normal response
// path (Fragmenter.EnqueueResponse / ChannelSession.EnqueueRx) targets one
// channel at a time, so the J2534 host's filters on THAT channel govern
// delivery. For Ford SCP's UUDT broadcasts at CAN ID 0x6A0/0x6A1, PCMTec
// installs its PASS_FILTER on a *different* channel than the one carrying
// the diagnostic request - so a frame enqueued on the request channel
// never reaches the broadcast filter and PCMTec sits waiting forever.
//
// IFrameBroadcaster lets a stack/handler shove a single raw-CAN frame at
// every active channel on the bus; each channel applies its own filter
// table and only accepts frames matching its PASS / FLOW_CONTROL filters.
// The implementation lives in Shim (where the channel collection lives);
// VirtualBus.Broadcaster is the seam Core code resolves through.
public interface IFrameBroadcaster
{
    /// <summary>
    /// Offer a raw frame (4-byte CAN ID + N-byte payload) to every channel
    /// currently open against the bus. Each channel decides delivery via
    /// its filter table. No-op when no channels are open.
    /// </summary>
    void BroadcastFrame(byte[] frame);
}

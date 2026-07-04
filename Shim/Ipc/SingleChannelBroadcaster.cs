using Common.PassThru;
using Core.Bus;

namespace Shim.Ipc;

// Single-channel IFrameBroadcaster for the non-pipe transports (RawCanTcpServer,
// HardwareCanServer). IpcSessionState is the multi-channel pipe version; the TCP
// and hardware transports each own exactly ONE channel, so this delivers the
// unsolicited broadcast stream (DBC scheduler + persona UUDT) to that one channel.
//
// Why this exists: bus.Broadcaster used to be set ONLY by IpcSessionState, so the
// DBC application broadcasts (the free-running powertrain frames a passive gauge /
// CAN-Display wants) never reached a peer on a non-pipe transport - it saw
// diagnostic responses but not the broadcast stream. Registering this as
// bus.Broadcaster on connect closes that gap.
internal sealed class SingleChannelBroadcaster : IFrameBroadcaster
{
    private readonly ChannelSession channel;

    public SingleChannelBroadcaster(ChannelSession channel) => this.channel = channel;

    // Offer the frame to the one channel; its filter table (empty for gauge /
    // hardware channels) decides delivery inside EnqueueRx.
    public void BroadcastFrame(byte[] frame)
    {
        channel.EnqueueRx(new PassThruMsg
        {
            ProtocolID = ProtocolID.CAN,
            Data = frame,
            IsBroadcast = true,   // unsolicited; lets the UI "Hide broadcasts" filter drop it
        });
    }
}

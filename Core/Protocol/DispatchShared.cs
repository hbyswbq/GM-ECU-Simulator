using Core.Bus;
using Core.Ecu;

namespace Core.Protocol;

// Dispatch-shared helpers. Both Gmw3110Dispatch and UdsKernelDispatch activate
// P3C the same way when a handler produces a positive response: store the
// channel as the most-recent enhanced channel and start (or reset) the
// tester-present timer. Lifting it here keeps the two dispatch tables free of
// bus plumbing.
internal static class DispatchShared
{
    internal static void ActivateP3C(EcuNode node, ChannelSession ch)
    {
        node.State.LastEnhancedChannel = ch;
        node.State.TesterPresent.Activate();
    }
}

using Core.Bus;
using Core.Ecu;

namespace Core.Protocol;

// One diagnostic standard's complete service contract. Self-contained and never
// shares dispatch with another stack: a look-alike service ($27, $23, $3E, $22)
// gets its own entry here even when the code is currently identical to another
// stack's, because the identity is incidental to two independent standards, not
// essential (DESIGN-Protocol-Stack-Architecture.md sections 3-4).
//
// The genuinely shared parts are injected MECHANISM (ISO-TP framing, the $27
// seed/key ciphers, PID rendering, the DTC store, TimerOnDelay) - the dispatch
// table itself is owned per stack and never shared (DESIGN doc section 7).
public interface IProtocolStack
{
    // Registry key and display id: "GMW3110", "UDS", "J1979", "KWP2000", ...
    string Standard { get; }

    NrcProfile      Nrc        { get; }   // bad-length $12 (GM) vs $13 (UDS), etc.
    SessionModel    Sessions   { get; }   // GM binary active/inactive vs UDS numbered
    TimingProfile   Timing     { get; }   // GM P2C/P3C vs UDS P2/P2*/S3
    AddressingModel Addressing { get; }   // conventional default IDs for the standard

    // The canonical catalog (the "gospel"): every SID the standard defines and
    // this stack implements, with names. The editor renders this; config stores
    // only the allow-list. (The design doc sketches this as IReadOnlyList<
    // ServiceDescriptor>; the richer ServiceCatalog value is used so dispatch
    // resolution can ask Catalog.Contains(sid) directly - see StackBinding.Owns.)
    ServiceCatalog Catalog { get; }

    // True if this stack owns the SID and enqueued a response (positive or a
    // stack-correct NRC); false if the SID is not in this stack's catalog (the
    // caller then emits THIS stack's serviceNotSupported NRC, physical only).
    // There is no cross-stack fallback.
    //
    // VirtualBus.DispatchUsdt routes every request through EcuNode.Resolve(canId,
    // sid) -> StackBinding -> this Dispatch; the persona model it replaced is gone.
    bool Dispatch(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch,
                  bool isFunctional, double nowMs, StackContext ctx);
}

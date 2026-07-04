using Core.Bus;
using Core.Ecu;

namespace Core.Protocol;

// Concrete IProtocolStack: a catalog + profiles plus an OPTIONAL dispatch
// delegate. The delegate is how each built stack (J1979/GMW3110/UDS/Ford/
// UDS-kernel in ProtocolStacks) wires the existing service handlers in without
// rewriting this class. Every stack ProtocolStacks builds supplies a dispatcher;
// the throw is the guard for a stack constructed without one, which VirtualBus
// never reaches because EcuNode.Resolve only binds the dispatcher-carrying stacks.
public sealed class ProtocolStack : IProtocolStack
{
    public delegate bool DispatchFn(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch,
                                    bool isFunctional, double nowMs, StackContext ctx);

    private readonly DispatchFn? dispatch;

    public ProtocolStack(string standard, ServiceCatalog catalog, NrcProfile nrc,
                         SessionModel sessions, TimingProfile timing, AddressingModel addressing,
                         DispatchFn? dispatch = null)
    {
        Standard      = standard;
        Catalog       = catalog;
        Nrc           = nrc;
        Sessions      = sessions;
        Timing        = timing;
        Addressing    = addressing;
        this.dispatch = dispatch;
    }

    public string Standard { get; }
    public ServiceCatalog Catalog { get; }
    public NrcProfile Nrc { get; }
    public SessionModel Sessions { get; }
    public TimingProfile Timing { get; }
    public AddressingModel Addressing { get; }

    public bool Dispatch(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch,
                         bool isFunctional, double nowMs, StackContext ctx)
        => dispatch is not null
            ? dispatch(node, usdt, ch, isFunctional, nowMs, ctx)
            : throw new NotSupportedException(
                $"Stack '{Standard}' has no dispatch wired yet (protocol-stack migration step 2/4).");
}

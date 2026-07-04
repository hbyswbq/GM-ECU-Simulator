namespace Core.Protocol;

// Binds one stack to an ECU on a specific set of CAN IDs with a specific enabled
// allow-list. An ECU's Stacks list is ordered; dispatch resolves (canId, sid) to
// the first binding that Owns it (DESIGN doc section 5). The same IProtocolStack
// instance can be bound twice on different CanIds with different Enabled subsets -
// which is exactly the real E38/E67 dual dispatcher (full GMW3110 set on the OBD
// pair, restricted set on the GMLAN enhanced pair) with no special case.
public sealed record StackBinding(IProtocolStack Stack, AddressingModel CanIds, IServiceFilter Enabled)
{
    // Instrumentation escape hatch: when true the binding owns EVERY SID on its CAN ids,
    // bypassing the catalog and allow-list. Used only by the Ford-UDS capture stack, which is a
    // log-everything-then-answer-a-whitelist tool (its purpose is observing an unknown tester's
    // probe stream, so it must SEE every SID, including ones it ultimately NRCs). Off for real
    // ECUs, whose ownership is the faithful catalog.
    public bool CatchAll { get; init; }

    // A binding OWNS a SID when its CAN IDs match the request and EITHER it is a catch-all
    // logger OR the stack's catalog defines the SID (the gospel) AND the allow-list enables it.
    // The catalog check is what makes a "*" (AllServices) binding never claim a SID the standard
    // does not define - so J1979 owns $01 while a "*" GMW3110 binding on the same CAN ID does
    // not (its catalog has no $01).
    public bool Owns(uint canId, byte sid) =>
        CanIds.Match(canId) && (CatchAll || (Stack.Catalog.Contains(sid) && Enabled.Allows(sid)));
}

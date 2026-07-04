namespace Core.Protocol;

// How a stack models diagnostic sessions. GM (GMW3110) has no numbered-session
// concept: communication is binary - normal messaging is active or disabled
// ($28 / $20), and $10 sets DTC/comms flags rather than selecting a session
// level. UDS uses numbered sessions (default / programming / extended via the
// $10 sub-function). Modelled as a kind here; the per-stack session STATE still
// lives on NodeState (DESIGN doc section 7).
public enum SessionKind
{
    // GMW3110: active/inactive normal communication, no numbered session.
    BinaryActiveInactive,

    // ISO 14229: numbered sessions selected by the $10 sub-function.
    NumberedSessions,
}

public sealed record SessionModel(SessionKind Kind)
{
    public static readonly SessionModel Gm = new(SessionKind.BinaryActiveInactive);
    public static readonly SessionModel Uds = new(SessionKind.NumberedSessions);
}

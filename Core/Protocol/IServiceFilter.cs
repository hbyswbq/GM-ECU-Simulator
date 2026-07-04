namespace Core.Protocol;

// A binding's enabled-service allow-list. The catalog is the gospel (every SID
// the standard defines); the filter is the per-ECU dispatch authority deciding
// which of those SIDs this binding actually answers - which is how real-silicon
// fidelity like "the GMLAN-enhanced dispatcher NRC-$11s $22" is modelled: leave
// $22 off that binding's list (DESIGN doc section 6).
//
// Whether a binding OWNS a SID is "filter allows it AND the catalog defines it"
// (StackBinding.Owns). AllServices defers entirely to catalog membership, so a
// "*" binding never claims a SID the standard does not define.
public interface IServiceFilter
{
    bool Allows(byte sid);

    // For the editor's render-full / store-delta view (DESIGN doc section 6):
    // true when this filter is the wildcard, false when it is an explicit list.
    bool IsWildcard { get; }
}

// "services": "*" - everything the bound stack's catalog implements.
public sealed class AllServices : IServiceFilter
{
    public static readonly AllServices Instance = new();
    private AllServices() { }

    // Catalog membership is the real gate (see StackBinding.Owns), so the
    // wildcard says yes to every SID and lets the catalog decide.
    public bool Allows(byte sid) => true;
    public bool IsWildcard => true;
}

// An explicit SID allow-list - mirrors a real dispatcher's jump table exactly.
// The enabled SIDs are expected to be a subset of the bound stack's catalog.
public sealed class SidAllowList : IServiceFilter
{
    private readonly HashSet<byte> sids;

    public SidAllowList(IEnumerable<byte> sids) => this.sids = new HashSet<byte>(sids);

    public bool Allows(byte sid) => sids.Contains(sid);
    public bool IsWildcard => false;
    public IReadOnlyCollection<byte> Sids => sids;
}

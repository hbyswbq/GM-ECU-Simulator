namespace Core.Protocol;

// An immutable, SID-keyed service catalog. Each diagnostic standard's full
// service set is one ServiceCatalog value; sibling dialects are built by
// DERIVING from a shared spine (KWP2000) through explicit Add/Replace/Remove
// transforms rather than subclassing - a redefinition is a visible, testable
// Replace, never a silent override (DESIGN doc principle 4).
//
// Transform guards keep the gospel self-validating:
//   Of      - throws on a duplicate SID.
//   Add     - throws if the SID already exists (use Replace to redefine).
//   Replace - throws if the SID is absent (cannot redefine what isn't there).
//   Remove  - throws if the SID is absent (catches a stale Remove when the
//             spine changes underneath a derived catalog).
// Every transform returns a NEW catalog; the receiver is never mutated, so the
// spine stays pristine across all dialects derived from it.
public sealed class ServiceCatalog
{
    private readonly Dictionary<byte, ServiceDescriptor> byId;

    private ServiceCatalog(Dictionary<byte, ServiceDescriptor> byId) => this.byId = byId;

    public static ServiceCatalog Of(params (byte Sid, string Name)[] services)
    {
        var map = new Dictionary<byte, ServiceDescriptor>(services.Length);
        foreach (var (sid, name) in services)
        {
            if (!map.TryAdd(sid, new ServiceDescriptor(sid, name)))
                throw new ArgumentException($"Duplicate SID 0x{sid:X2} in catalog");
        }
        return new ServiceCatalog(map);
    }

    /// <summary>Services ordered by SID - the editor renders this; tests lock it.</summary>
    public IReadOnlyList<ServiceDescriptor> Services => byId.Values.OrderBy(d => d.Sid).ToArray();

    /// <summary>The SIDs this catalog defines, ascending.</summary>
    public IReadOnlyList<byte> Sids => byId.Keys.OrderBy(b => b).ToArray();

    public int Count => byId.Count;

    public bool Contains(byte sid) => byId.ContainsKey(sid);

    public bool TryGet(byte sid, out ServiceDescriptor? descriptor)
    {
        if (byId.TryGetValue(sid, out var d)) { descriptor = d; return true; }
        descriptor = null;
        return false;
    }

    public ServiceCatalog Add(byte sid, string name, string? group = null)
    {
        if (byId.ContainsKey(sid))
            throw new ArgumentException($"Add: SID 0x{sid:X2} already present - use Replace to redefine");
        var map = new Dictionary<byte, ServiceDescriptor>(byId) { [sid] = new ServiceDescriptor(sid, name, group) };
        return new ServiceCatalog(map);
    }

    public ServiceCatalog Replace(byte sid, string name, string? group = null)
    {
        if (!byId.ContainsKey(sid))
            throw new ArgumentException($"Replace: SID 0x{sid:X2} absent - use Add for a new service");
        var map = new Dictionary<byte, ServiceDescriptor>(byId) { [sid] = new ServiceDescriptor(sid, name, group) };
        return new ServiceCatalog(map);
    }

    public ServiceCatalog Remove(byte sid)
    {
        if (!byId.ContainsKey(sid))
            throw new ArgumentException($"Remove: SID 0x{sid:X2} absent");
        var map = new Dictionary<byte, ServiceDescriptor>(byId);
        map.Remove(sid);
        return new ServiceCatalog(map);
    }
}

using Core.Protocol;
using Xunit;

namespace EcuSimulator.Tests.Protocol;

// Locks the EXACT membership of every built-in catalog. These are the gospel
// (DESIGN doc principle 5); a change to any catalog must be a deliberate edit to
// the expected set here, with a source. The GMW3110 set is the authoritative
// silicon ground truth (memory project_dual_diag_stack_e38_e67): the 19-SID
// E38/E67 OBD dispatcher = 14 KWP-derived core + 5 GMLAN-enhanced.
public sealed class StandardCatalogMembershipTests
{
    [Fact]
    public void Gmw3110_Membership_MatchesSilicon19()
    {
        // The 19 SIDs confirmed on real E38/E67 silicon: 14 KWP-core + 5 GMLAN-enhanced.
        Assert.Equal(
            new byte[] { 0x10, 0x12, 0x1A, 0x20, 0x22, 0x23, 0x27, 0x28, 0x2C, 0x2D,
                         0x34, 0x36, 0x3B, 0x3E, 0xA2, 0xA5, 0xA9, 0xAA, 0xAE },
            StandardCatalogs.Gmw3110.Sids);
    }

    [Fact]
    public void Gmw3110_HasNoUdsOnlyOrKwpOnlyBytes()
    {
        var gm = StandardCatalogs.Gmw3110;
        // $11 ECUReset: real silicon NRCs it. $2E/$19/$2A/$2F: UDS forms GM does
        // not speak. $14/$29/$17/$18: KWP forms GM dropped.
        foreach (byte sid in new byte[] { 0x11, 0x2E, 0x19, 0x2A, 0x2F, 0x14, 0x29, 0x17, 0x18, 0x31 })
            Assert.False(gm.Contains(sid), $"GMW3110 should not contain 0x{sid:X2}");
    }

    [Fact]
    public void Gmw3110_CarriesNoDisplayGroups()
    {
        // The GMW3110 section renders as one flat 19-SID list; no entry carries a
        // display group (the GMLAN-enhanced sub-header was removed). Group tagging
        // remains a Ford-only concern (OBD vs UDS vs proprietary sections).
        var gm = StandardCatalogs.Gmw3110;
        foreach (byte sid in gm.Sids)
        {
            Assert.True(gm.TryGet(sid, out var d));
            Assert.Null(d!.Group);
        }
    }

    [Fact]
    public void Uds_Membership()
    {
        Assert.Equal(
            new byte[] { 0x10, 0x11, 0x14, 0x19, 0x22, 0x23, 0x24, 0x27, 0x28, 0x29, 0x2A,
                         0x2C, 0x2E, 0x2F, 0x31, 0x34, 0x35, 0x36, 0x37, 0x3D, 0x3E, 0x85 },
            StandardCatalogs.Uds.Sids);
    }

    [Fact]
    public void Uds_IsSupersetOfModelledFordPcmAllowList()
    {
        // The Ford PCM (DS-EB3G-12A650-CD) enabled set from the design doc section 6.
        // The catalog is the gospel; a specific ECU enables a subset, so the catalog
        // must contain every SID that ECU enables.
        byte[] fordEnabled = { 0x10, 0x11, 0x14, 0x19, 0x22, 0x23, 0x27, 0x2A,
                               0x2C, 0x2E, 0x2F, 0x31, 0x34, 0x36, 0x37, 0x3E, 0x85 };
        foreach (byte sid in fordEnabled)
            Assert.True(StandardCatalogs.Uds.Contains(sid), $"UDS catalog missing Ford SID 0x{sid:X2}");
    }

    [Fact]
    public void Uds_HasNoGmOnlyBytes()
    {
        var uds = StandardCatalogs.Uds;
        // $3B/$1A: KWP forms UDS dropped. $AA/$AE/$A9/$A2/$A5/$2D/$20/$12: GMW3110/GMLAN only.
        foreach (byte sid in new byte[] { 0x3B, 0x1A, 0xAA, 0xAE, 0xA9, 0xA2, 0xA5, 0x2D, 0x20, 0x12 })
            Assert.False(uds.Contains(sid), $"UDS should not contain 0x{sid:X2}");
    }

    [Fact]
    public void J1979_Membership()
    {
        Assert.Equal(
            new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A },
            StandardCatalogs.J1979.Sids);
    }

    [Fact]
    public void Ford_Membership_IsObdPlusUdsPlusProprietary()
    {
        // The Ford capture catalog is the UNION of the J1979 OBD modes, the UDS gospel, and the
        // Ford-proprietary $A0/$A1/$B1 - the full MENU the user ticks support from (not a code-derived
        // "implemented" subset). Grouped OBD / UDS / proprietary for the editor's three sections.
        var ford = StandardCatalogs.Ford;
        foreach (var sid in StandardCatalogs.J1979.Sids)
            Assert.True(ford.Contains(sid), $"Ford catalog missing OBD SID 0x{sid:X2}");
        foreach (var sid in StandardCatalogs.Uds.Sids)
            Assert.True(ford.Contains(sid), $"Ford catalog missing UDS SID 0x{sid:X2}");
        foreach (var sid in new byte[] { 0xA0, 0xA1, 0xB1 })
            Assert.True(ford.Contains(sid), $"Ford catalog missing proprietary SID 0x{sid:X2}");

        // Exactly the union, nothing else (the three sets are disjoint).
        Assert.Equal(StandardCatalogs.J1979.Sids.Count + StandardCatalogs.Uds.Sids.Count + 3, ford.Count);

        // Group tags drive the editor's three sections.
        Assert.True(ford.TryGet(0x09, out var obd));
        Assert.Equal(StandardCatalogs.FordObdGroup, obd!.Group);
        Assert.True(ford.TryGet(0xA1, out var prop));
        Assert.Equal(StandardCatalogs.FordProprietaryGroup, prop!.Group);
        Assert.True(ford.TryGet(0x27, out var uds));
        Assert.Null(uds!.Group);
    }
}

using Core.Protocol;
using Xunit;

namespace EcuSimulator.Tests.Protocol;

// The Add/Replace/Remove transform builder and its drift guards. These lock the
// mechanism the dialect catalogs are built from (DESIGN doc principle 4): a
// redefinition is a visible, throwing-on-misuse Replace, never a silent override,
// and the spine is never mutated by deriving from it.
public sealed class ServiceCatalogTests
{
    [Fact]
    public void Of_DuplicateSid_Throws()
        => Assert.Throws<ArgumentException>(() => ServiceCatalog.Of((0x10, "A"), (0x10, "B")));

    [Fact]
    public void Add_NewSid_AddsIt()
    {
        var c = ServiceCatalog.Of((0x10, "A")).Add(0x22, "B");
        Assert.True(c.Contains(0x22));
        Assert.Equal(2, c.Count);
    }

    [Fact]
    public void Add_ExistingSid_Throws()
        => Assert.Throws<ArgumentException>(() => ServiceCatalog.Of((0x10, "A")).Add(0x10, "B"));

    [Fact]
    public void Replace_PresentSid_KeepsMembershipChangesName()
    {
        var c = ServiceCatalog.Of((0x10, "Old")).Replace(0x10, "New");
        Assert.Equal(1, c.Count);
        Assert.True(c.TryGet(0x10, out var d));
        Assert.Equal("New", d!.Name);
    }

    [Fact]
    public void Replace_AbsentSid_Throws()
        => Assert.Throws<ArgumentException>(() => ServiceCatalog.Of((0x10, "A")).Replace(0x22, "B"));

    [Fact]
    public void Remove_PresentSid_DropsIt()
    {
        var c = ServiceCatalog.Of((0x10, "A"), (0x22, "B")).Remove(0x22);
        Assert.False(c.Contains(0x22));
        Assert.True(c.Contains(0x10));
    }

    [Fact]
    public void Remove_AbsentSid_Throws()
        => Assert.Throws<ArgumentException>(() => ServiceCatalog.Of((0x10, "A")).Remove(0x99));

    [Fact]
    public void Transforms_DoNotMutateReceiver()
    {
        var spine = ServiceCatalog.Of((0x10, "A"), (0x11, "B"));
        _ = spine.Remove(0x11).Add(0x22, "C").Replace(0x10, "Z");
        // The receiver is untouched: deriving a dialect never mutates the spine.
        Assert.Equal(new byte[] { 0x10, 0x11 }, spine.Sids);
        Assert.True(spine.TryGet(0x10, out var d));
        Assert.Equal("A", d!.Name);
    }

    [Fact]
    public void Services_OrderedBySid()
    {
        var c = ServiceCatalog.Of((0x22, "B"), (0x10, "A"), (0x3E, "C"));
        Assert.Equal(new byte[] { 0x10, 0x22, 0x3E }, c.Services.Select(s => s.Sid).ToArray());
    }
}

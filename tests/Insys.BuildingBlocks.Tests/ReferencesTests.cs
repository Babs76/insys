using Insys.BuildingBlocks.Domain;

namespace Insys.BuildingBlocks.Tests;

public sealed class ReferencesTests
{
    /// <summary>UUIDv7 IDs made in the same millisecond share their leading digits; their references must not.</summary>
    [Fact]
    public void References_FromIdsMadeTogether_AreDistinct_AndKeepThePrefixAndLength()
    {
        var references = Enumerable.Range(0, 5_000).Select(_ => References.Of("HM", Guid.CreateVersion7(), 12)).ToList();
        references.Distinct().Count().ShouldBe(5_000);
        references.ShouldAllBe(r => r.Length == 12 && r.StartsWith("HM", StringComparison.Ordinal) && r == r.ToUpperInvariant());
        References.Of("A-VERY-LONG-PREFIX", Guid.CreateVersion7(), 10).ShouldBe("A-VERY-LON");
        References.Of("MC", Guid.CreateVersion7(), 25).Length.ShouldBe(25);
    }
}

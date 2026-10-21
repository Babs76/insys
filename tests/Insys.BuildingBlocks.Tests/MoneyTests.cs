using Insys.BuildingBlocks.Domain;

namespace Insys.BuildingBlocks.Tests;

public sealed class MoneyTests
{
    [Fact]
    public void Money_Add_SameCurrency_SumsAmounts_DBA012()
    {
        var total = Money.Of(100.10m, "ZAR").Add(Money.Of(0.25m, "ZAR"));

        total.IsSuccess.ShouldBeTrue();
        total.Value.ShouldBe(Money.Of(100.35m, "ZAR"));
    }

    [Fact]
    public void Money_Add_DifferentCurrency_IsRejected_DBA012()
    {
        var total = Money.Of(1m, "ZAR").Add(Money.Of(1m, "USD"));

        total.IsFailure.ShouldBeTrue();
        total.Error!.Code.ShouldBe(ErrorCodes.CurrencyMismatch);
    }

    [Theory]
    [InlineData(2.345, 2, MidpointRounding.ToEven, 2.34)]
    [InlineData(2.345, 2, MidpointRounding.AwayFromZero, 2.35)]
    [InlineData(1234.5, 0, MidpointRounding.ToEven, 1234)]
    public void Money_Round_UsesTheGivenRule(decimal amount, int places, MidpointRounding mode, decimal expected) =>
        Money.Of(amount, "ZAR").Round(RoundingRule.Of(places, mode)).Amount.ShouldBe(expected);

    [Theory]
    [InlineData("zar")]
    [InlineData("ZA")]
    [InlineData("ZARR")]
    [InlineData("Z1R")]
    public void CurrencyCode_MustBeThreeUpperCaseLetters_ISO4217(string code) =>
        Should.Throw<ArgumentException>(() => new CurrencyCode(code));

    [Fact]
    public void Money_HasNoFloatingPointMembers_DBA012() =>
        typeof(Money).GetProperties().Select(p => p.PropertyType).ShouldNotContain(t => t == typeof(double) || t == typeof(float));
}

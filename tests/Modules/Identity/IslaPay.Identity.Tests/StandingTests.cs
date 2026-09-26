using IslaPay.Identity.Contracts;
using IslaPay.Platform;
using IslaPay.TestSupport;

namespace IslaPay.Identity.Tests;

/// <summary>The one rule every money-moving module asks before it moves.</summary>
public class StandingTests
{
    private static Money EIsla(string amount) => Money.Parse(amount, TestCurrencies.EIsla);

    [Fact]
    public void A_frozen_account_is_refused_whatever_the_amount()
    {
        var frozen = new DirectoryUser("u", "u@x.cu", "U", PhoneVerified: true, Frozen: true);

        Assert.Equal(Standing.AccountFrozen, Standing.Check(frozen)?.Code);
        Assert.Equal(Standing.AccountFrozen, Standing.Check(frozen, EIsla("0.01"))?.Code);
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 2)]
    [InlineData(false, true, 0)]
    public void The_level_follows_the_phone_then_the_identity(bool phone, bool identity, int level) =>
        Assert.Equal(level, new DirectoryUser("u", "u@x.cu", "U", phone, identity).Level);

    [Fact]
    public void Level_one_moves_up_to_a_thousand_at_once()
    {
        var user = new DirectoryUser("u", "u@x.cu", "U", PhoneVerified: true);

        Assert.Null(Standing.Check(user, EIsla("1000.00")));
        var refusal = Standing.Check(user, EIsla("1000.01"));
        Assert.Equal(Standing.LimitExceeded, refusal?.Code);
        Assert.Equal("1000", refusal!.Facts["limit"]);
        Assert.Equal("EISLA", refusal.Facts["currency"]);
    }

    [Fact]
    public void Level_two_moves_more()
    {
        var user = new DirectoryUser("u", "u@x.cu", "U", PhoneVerified: true, IdentityVerified: true);

        Assert.Null(Standing.Check(user, EIsla("25000.00")));
        Assert.Equal(Standing.LimitExceeded, Standing.Check(user, EIsla("25000.01"))?.Code);
    }

    [Fact]
    public void An_unverified_phone_is_left_to_the_module_that_already_refuses_it()
    {
        // Each module refuses an unproved phone with its own switch, which
        // tests turn off; this rule must not refuse it a second way.
        var user = new DirectoryUser("u", "u@x.cu", "U", PhoneVerified: false);
        Assert.Null(Standing.Check(user, EIsla("5000.00")));
    }
}

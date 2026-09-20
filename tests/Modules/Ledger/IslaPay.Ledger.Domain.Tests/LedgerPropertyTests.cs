using CsCheck;
using IslaPay.Platform;

namespace IslaPay.Ledger.Domain.Tests;

/// <summary>
/// Properties, not examples.
/// </summary>
/// <remarks>
/// A ledger fails on the sequence nobody thought to write down: the conversion
/// that empties an account to exactly zero, the transfer whose fee rounds to
/// nothing, the retry that lands between two other postings. Chosen examples
/// only cover the cases someone imagined; these state the rules and let CsCheck
/// hunt for a sequence that breaks them, shrinking whatever it finds to the
/// smallest counterexample.
/// </remarks>
public class LedgerPropertyTests
{
    private static readonly Gen<Currency> AnyCurrency =
        Gen.OneOfConst(Currency.Usd, Currency.Usdc, Currency.Usdt);

    /// <summary>Amounts up to ~10,000 units, never zero.</summary>
    private static Gen<Money> AmountIn(Currency currency) =>
        Gen.Long[1, 10_000 * (long)Math.Pow(10, currency.Scale())]
            .Select(units => Money.FromMinorUnits(units, currency));

    private static readonly Gen<string> AnyUser =
        Gen.Int[1, 12].Select(i => $"user{i}");

    private static readonly DateTimeOffset Epoch =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int i) => Epoch + TimeSpan.FromSeconds(i);

    // ---------------------------------------------------------------- the rules

    [Fact]
    public void Every_ledger_sums_to_zero_in_every_currency()
    {
        // The property the whole design exists for: money is never created or
        // destroyed, whatever sequence of operations runs.
        Gen.Select(AnyCurrency, AnyUser, AnyUser, Gen.Int[1, 25])
            .Sample((currency, from, to, count) =>
            {
                var ledger = new Ledger();
                Fund(ledger, from, currency, "1000000", 0);

                for (var i = 0; i < count; i++)
                {
                    var amount = Money.FromMinorUnits(
                        1 + i * 37 % 5000,
                        currency);
                    try
                    {
                        ledger.Post(Conversions.BuildTransfer(
                            Guid.NewGuid(), from, to == from ? from + "b" : to,
                            amount, At(i + 1)));
                    }
                    catch (InsufficientFundsException)
                    {
                        // A rejected posting is a valid outcome, not a failure
                        // of the property — and must leave nothing behind.
                    }
                }

                foreach (var (_, total) in ledger.TotalsByCurrency())
                {
                    Assert.True(total.IsZero, $"ledger does not balance: {total}");
                }
            }, iter: 500);
    }

    [Fact]
    public void A_balance_is_always_the_sum_of_its_own_entries()
    {
        // Balances are a projection. If a stored balance can disagree with the
        // entries behind it, every report built on it is suspect.
        Gen.Select(AnyCurrency, Gen.Int[1, 20])
            .Sample((currency, count) =>
            {
                var ledger = new Ledger();
                Fund(ledger, "alice", currency, "500000", 0);

                for (var i = 0; i < count; i++)
                {
                    try
                    {
                        ledger.Post(Conversions.BuildTransfer(
                            Guid.NewGuid(), "alice", "bob",
                            Money.FromMinorUnits(1 + i * 13 % 900, currency),
                            At(i + 1)));
                    }
                    catch (InsufficientFundsException) { }
                }

                foreach (var account in new[]
                         {
                             AccountId.User("alice", currency),
                             AccountId.User("bob", currency),
                         })
                {
                    var folded = ledger.EntriesFor(account).Aggregate(
                        Money.Zero(currency), (sum, e) => sum + e.Amount);
                    Assert.Equal(folded, ledger.BalanceOf(account));
                }
            }, iter: 500);
    }

    [Fact]
    public void A_customer_account_never_goes_negative()
    {
        Gen.Select(AnyCurrency, Gen.Int[1, 30])
            .Sample((currency, count) =>
            {
                var ledger = new Ledger();
                Fund(ledger, "alice", currency, "100", 0);

                for (var i = 0; i < count; i++)
                {
                    try
                    {
                        // Deliberately larger than the funding, so most of
                        // these must be refused.
                        ledger.Post(Conversions.BuildTransfer(
                            Guid.NewGuid(), "alice", "bob",
                            Money.FromMinorUnits(
                                (1 + i * 7919 % 4000) * (long)Math.Pow(10, currency.Scale() - 2),
                                currency),
                            At(i + 1)));
                    }
                    catch (InsufficientFundsException) { }
                }

                Assert.False(ledger.BalanceOf(AccountId.User("alice", currency)).IsNegative);
                Assert.False(ledger.BalanceOf(AccountId.User("bob", currency)).IsNegative);
            }, iter: 500);
    }

    [Fact]
    public void A_refused_posting_leaves_no_trace()
    {
        // Atomicity. A posting that overdraws on its third leg must not have
        // written the first two — otherwise the ledger holds a state no rule
        // allows and the next reconciliation is the first anyone hears of it.
        Gen.Select(AnyCurrency, AmountIn(Currency.Usd))
            .Sample((currency, _) =>
            {
                var ledger = new Ledger();
                Fund(ledger, "alice", currency, "10", 0);

                var before = ledger.Entries.Count;
                var balanceBefore = ledger.BalanceOf(AccountId.User("alice", currency));

                Assert.Throws<InsufficientFundsException>(() =>
                    ledger.Post(Conversions.BuildTransfer(
                        Guid.NewGuid(), "alice", "bob",
                        Money.FromMinorUnits(
                            100 * (long)Math.Pow(10, currency.Scale()), currency),
                        At(1))));

                Assert.Equal(before, ledger.Entries.Count);
                Assert.Equal(balanceBefore, ledger.BalanceOf(AccountId.User("alice", currency)));
                Assert.True(ledger.BalanceOf(AccountId.User("bob", currency)).IsZero);
            }, iter: 200);
    }

    [Fact]
    public void Replaying_an_idempotency_key_never_moves_money_twice()
    {
        Gen.Select(AnyCurrency, Gen.Int[2, 8])
            .Sample((currency, replays) =>
            {
                var ledger = new Ledger();
                Fund(ledger, "alice", currency, "1000", 0);

                var amount = Money.FromMinorUnits(
                    5 * (long)Math.Pow(10, currency.Scale()), currency);
                var key = "retry-me";

                var first = ledger.Post(Conversions.BuildTransfer(
                    Guid.NewGuid(), "alice", "bob", amount, At(1), idempotencyKey: key));
                var after = ledger.BalanceOf(AccountId.User("alice", currency));

                for (var i = 0; i < replays; i++)
                {
                    var again = ledger.Post(Conversions.BuildTransfer(
                        Guid.NewGuid(), "alice", "bob", amount, At(i + 2),
                        idempotencyKey: key));

                    // Same answer, and nothing moved.
                    Assert.Equal(first.Count, again.Count);
                    Assert.Equal(after, ledger.BalanceOf(AccountId.User("alice", currency)));
                }
            }, iter: 300);
    }

    [Fact]
    public void Sequence_numbers_are_gapless_per_account()
    {
        // A gap is evidence an entry was removed. Append-only is only
        // verifiable if the numbering makes deletion visible.
        Gen.Select(AnyCurrency, Gen.Int[1, 25])
            .Sample((currency, count) =>
            {
                var ledger = new Ledger();
                Fund(ledger, "alice", currency, "1000000", 0);

                for (var i = 0; i < count; i++)
                {
                    try
                    {
                        ledger.Post(Conversions.BuildTransfer(
                            Guid.NewGuid(), "alice", "bob",
                            Money.FromMinorUnits(1 + i, currency), At(i + 1)));
                    }
                    catch (InsufficientFundsException) { }
                }

                foreach (var account in ledger.Entries.Select(e => e.Account).Distinct())
                {
                    var sequences = ledger.EntriesFor(account).Select(e => e.Sequence).ToList();
                    Assert.Equal(Enumerable.Range(1, sequences.Count).Select(i => (long)i), sequences);
                }
            }, iter: 400);
    }

    [Fact]
    public void A_running_balance_matches_the_fold_at_that_point()
    {
        Gen.Select(AnyCurrency, Gen.Int[1, 20])
            .Sample((currency, count) =>
            {
                var ledger = new Ledger();
                Fund(ledger, "alice", currency, "1000000", 0);

                for (var i = 0; i < count; i++)
                {
                    try
                    {
                        ledger.Post(Conversions.BuildTransfer(
                            Guid.NewGuid(), "alice", "bob",
                            Money.FromMinorUnits(1 + i * 31 % 700, currency), At(i + 1)));
                    }
                    catch (InsufficientFundsException) { }
                }

                foreach (var account in ledger.Entries.Select(e => e.Account).Distinct())
                {
                    var running = Money.Zero(account.Currency);
                    foreach (var entry in ledger.EntriesFor(account))
                    {
                        running += entry.Amount;
                        Assert.Equal(running, entry.RunningBalance);
                    }
                }
            }, iter: 400);
    }

    [Fact]
    public void A_reversal_returns_every_account_to_where_it_started()
    {
        Gen.Select(AnyCurrency, AnyUser)
            .Sample((currency, user) =>
            {
                var ledger = new Ledger();
                Fund(ledger, user, currency, "1000", 0);

                var before = ledger.BalanceOf(AccountId.User(user, currency));
                var postingId = Guid.NewGuid();

                ledger.Post(Conversions.BuildTransfer(
                    postingId, user, "counterparty",
                    Money.FromMinorUnits(
                        250 * (long)Math.Pow(10, currency.Scale() - 2), currency),
                    At(1)));

                ledger.Post(ledger.BuildReversal(postingId, Guid.NewGuid(), At(2)));

                Assert.Equal(before, ledger.BalanceOf(AccountId.User(user, currency)));
                Assert.True(ledger.BalanceOf(AccountId.User("counterparty", currency)).IsZero);
                // The error and its correction both remain visible.
                Assert.Equal(4, ledger.Entries.Count(e => e.PostedAt > At(0)));
            }, iter: 300);
    }

    [Fact]
    public void A_conversion_balances_in_both_currencies_at_any_amount()
    {
        Gen.Select(AmountIn(Currency.Usd), Gen.OneOfConst(Currency.Usdc, Currency.Usdt))
            .Sample((amount, to) =>
            {
                var posting = Conversions.BuildConversion(
                    Guid.NewGuid(), "alice", amount, to, "1.002", At(1));

                foreach (var currency in posting.Currencies)
                {
                    var sum = posting.Legs
                        .Where(l => l.Amount.Currency == currency)
                        .Aggregate(Money.Zero(currency), (s, l) => s + l.Amount);
                    Assert.True(sum.IsZero, $"{currency.Code()} legs sum to {sum}");
                }
            }, iter: 500);
    }

    [Fact]
    public void A_conversion_fee_is_never_more_than_the_amount_converted()
    {
        Gen.Select(AmountIn(Currency.Usd), Gen.OneOfConst(Currency.Usdc, Currency.Usdt))
            .Sample((amount, to) =>
            {
                var posting = Conversions.BuildConversion(
                    Guid.NewGuid(), "alice", amount, to, "1.0", At(1));

                // SingleOrDefault, not Single. Below roughly half a unit the
                // one per cent rounds to nothing and there is no fee leg at
                // all — charging zero is not the same as charging, and a leg
                // that moves nothing is refused by the domain. Assuming the
                // leg was always there is what hid that for as long as it did.
                var fee = posting.Legs
                    .Where(l => l.Account == AccountId.Fees(amount.Currency))
                    .Select(l => l.Amount)
                    .SingleOrDefault(Money.Zero(amount.Currency));

                Assert.False(fee.IsNegative);
                Assert.True(fee <= amount);
            }, iter: 500);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Seeds an account from an external mirror, which may go negative.</summary>
    private static void Fund(Ledger ledger, string user, Currency currency, string amount, int at) =>
        ledger.Post(Conversions.BuildDeposit(
            Guid.NewGuid(), user, Money.Parse(amount, currency), "bank:seed", At(at)));
}

/// <summary>
/// The amounts too small for a percentage to bite.
/// </summary>
/// <remarks>
/// Found by the property test above, which is what property tests are for: one
/// per cent of twelve cents is a hundredth of a cent, USD is accounted in
/// cents, and a fee that cannot be charged was being posted as a zero leg —
/// which the domain refuses, so the conversion threw instead of happening.
/// These pin the answer so the next person to touch rounding finds out at
/// once, with an example rather than a seed.
/// </remarks>
public class RoundingToNothingTests
{
    [Theory]
    [InlineData("0.12")]
    [InlineData("0.01")]
    [InlineData("0.49")]
    public void A_conversion_whose_fee_rounds_to_zero_still_happens(string amount)
    {
        var posting = Conversions.BuildConversion(
            Guid.NewGuid(), "u1", Money.Parse(amount, Currency.Usd),
            Currency.Usdt, "1.0000", DateTimeOffset.UtcNow);

        // No fee leg at all, rather than one that moves nothing.
        Assert.DoesNotContain(posting.Legs, l => l.Account == AccountId.Fees(Currency.Usd));
        Assert.All(posting.Legs, l => Assert.NotEqual(0, l.Amount.MinorUnits));

        // And it still balances in both currencies, which is the only thing
        // that was ever non-negotiable.
        foreach (var currency in posting.Currencies)
        {
            Assert.Equal(0, posting.Legs
                .Where(l => l.Amount.Currency == currency)
                .Sum(l => l.Amount.MinorUnits));
        }
    }

    [Fact]
    public void A_p2p_sale_whose_fee_rounds_to_zero_still_happens()
    {
        var posting = Conversions.BuildP2PSale(
            Guid.NewGuid(), "u1", Money.Parse("0.12", Currency.Usd), DateTimeOffset.UtcNow);

        Assert.DoesNotContain(posting.Legs, l => l.Account == AccountId.Fees(Currency.Usd));
        Assert.Equal(0, posting.Legs.Sum(l => l.Amount.MinorUnits));
    }

    [Fact]
    public void A_fee_that_does_round_to_something_is_still_charged()
    {
        // The guard above must not have quietly made every conversion free.
        var posting = Conversions.BuildConversion(
            Guid.NewGuid(), "u1", Money.Parse("100.00", Currency.Usd),
            Currency.Usdt, "1.0000", DateTimeOffset.UtcNow);

        var fee = posting.Legs.Single(l => l.Account == AccountId.Fees(Currency.Usd));
        Assert.Equal("1.00", fee.Amount.ToString());
    }
}

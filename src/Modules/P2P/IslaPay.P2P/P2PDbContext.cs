using IslaPay.Platform;
using Microsoft.EntityFrameworkCore;

namespace IslaPay.P2P;

/// <summary>A row of <c>p2p.methods</c>: one rail the market trades over.</summary>
public sealed class TradeMethod
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string LocalCurrencyCode { get; set; } = string.Empty;

    /// <summary>
    /// The currency's decimal places, stored beside its code.
    /// </summary>
    /// <remarks>
    /// A row carries what a unit is. Minor units plus a code do not say
    /// whether 1500000 is one and a half USDT or a million and a half, and the
    /// answer used to come from a compile-time enum — the thing the currency
    /// catalogue replaced.
    /// </remarks>
    public int LocalScale { get; set; }

    public string WalletCurrencyCode { get; set; } = string.Empty;

    /// <summary>See <c>LocalScale</c>.</summary>
    public int WalletScale { get; set; }

    public bool Available { get; set; }

    /// <summary>Where a buyer sends their local money. Read by a human.</summary>
    public string Instructions { get; set; } = string.Empty;

    public long MinimumMinor { get; set; }

    public long MaximumMinor { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Currency LocalCurrency => Currency.Of(LocalCurrencyCode, LocalScale);

    public Currency WalletCurrency => Currency.Of(WalletCurrencyCode, WalletScale);

    public Money Minimum => Money.FromMinorUnits(MinimumMinor, WalletCurrency);

    public Money Maximum => Money.FromMinorUnits(MaximumMinor, WalletCurrency);
}

/// <summary>A row of <c>p2p.rates</c>: a price, and when it started applying.</summary>
public sealed class TradeRate
{
    public long Id { get; set; }

    public string MethodId { get; set; } = string.Empty;

    public string Side { get; set; } = string.Empty;

    public string WalletCurrencyCode { get; set; } = string.Empty;

    /// <summary>See <c>LocalScale</c>.</summary>
    public int WalletScale { get; set; }

    /// <summary>
    /// Units of local currency per one wallet unit.
    /// </summary>
    /// <remarks>
    /// <c>decimal</c> against a <c>numeric</c> column, never a double. This
    /// number multiplies money.
    /// </remarks>
    public decimal Rate { get; set; }

    public DateTimeOffset EffectiveFrom { get; set; }

    public string SetBy { get; set; } = string.Empty;
}

/// <summary>A row of <c>p2p.trades</c>.</summary>
public sealed class Trade
{
    public Guid Id { get; set; }

    public long Seq { get; set; }

    public string UserId { get; set; } = string.Empty;

    public string UserName { get; set; } = string.Empty;

    public string Side { get; set; } = string.Empty;

    public string MethodId { get; set; } = string.Empty;

    /// <summary>Frozen: a rail renamed later must not rewrite somebody's history.</summary>
    public string MethodName { get; set; } = string.Empty;

    public string WalletCurrencyCode { get; set; } = string.Empty;

    /// <summary>See <c>LocalScale</c>.</summary>
    public int WalletScale { get; set; }

    /// <summary>The wallet-currency gross the trade is priced on, before the fee.</summary>
    public long AmountMinor { get; set; }

    public long FeeMinor { get; set; }

    public string LocalCurrencyCode { get; set; } = string.Empty;

    /// <summary>
    /// The currency's decimal places, stored beside its code.
    /// </summary>
    /// <remarks>
    /// A row carries what a unit is. Minor units plus a code do not say
    /// whether 1500000 is one and a half USDT or a million and a half, and the
    /// answer used to come from a compile-time enum — the thing the currency
    /// catalogue replaced.
    /// </remarks>
    public int LocalScale { get; set; }

    /// <summary>What the user receives (sell) or must send (buy).</summary>
    public long LocalMinor { get; set; }

    /// <summary>Frozen at creation. The arithmetic is the agreement.</summary>
    public decimal Rate { get; set; }

    public string Reference { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string? SettleIntent { get; set; }

    public string? FailureReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? SettledAt { get; set; }

    public Guid? CommitPostingId { get; set; }

    public Guid? SettlePostingId { get; set; }

    public string? OperatorId { get; set; }

    public string? OperatorReference { get; set; }

    public Currency WalletCurrency => Currency.Of(WalletCurrencyCode, WalletScale);

    public Currency LocalCurrency => Currency.Of(LocalCurrencyCode, LocalScale);

    public Money Amount => Money.FromMinorUnits(AmountMinor, WalletCurrency);

    public Money Fee => Money.FromMinorUnits(FeeMinor, WalletCurrency);

    public Money Local => Money.FromMinorUnits(LocalMinor, LocalCurrency);

    /// <summary>
    /// The wallet amount that actually crosses the currency boundary: what a
    /// seller's local payout is worth, and what a buyer ends up holding.
    /// </summary>
    public Money Net => Amount - Fee;
}

/// <summary>
/// The P2P tables, mapped.
/// </summary>
/// <remarks>
/// EF Core as a mapper and nothing else, on the same terms as Marketplace: the
/// schema is owned by <c>Migrations/001_p2p.sql</c>, every column is named
/// explicitly so the mapping cannot drift from it, and there are no EF
/// migrations.
/// </remarks>
public sealed class P2PDbContext : DbContext
{
    public P2PDbContext(DbContextOptions<P2PDbContext> options) : base(options)
    {
    }

    public DbSet<TradeMethod> Methods => Set<TradeMethod>();

    public DbSet<TradeRate> Rates => Set<TradeRate>();

    public DbSet<Trade> Trades => Set<Trade>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<TradeMethod>(method =>
        {
            method.ToTable("methods", "p2p");
            method.HasKey(m => m.Id);
            method.Property(m => m.Id).HasColumnName("id");
            method.Property(m => m.Name).HasColumnName("name");
            method.Property(m => m.LocalCurrencyCode).HasColumnName("local_currency");
            method.Property(m => m.LocalScale).HasColumnName("local_scale");
            method.Property(m => m.WalletCurrencyCode).HasColumnName("wallet_currency");
            method.Property(m => m.WalletScale).HasColumnName("wallet_scale");
            method.Property(m => m.Available).HasColumnName("available");
            method.Property(m => m.Instructions).HasColumnName("instructions");
            method.Property(m => m.MinimumMinor).HasColumnName("minimum_minor");
            method.Property(m => m.MaximumMinor).HasColumnName("maximum_minor");
            method.Property(m => m.UpdatedAt).HasColumnName("updated_at");
            method.Ignore(m => m.LocalCurrency);
            method.Ignore(m => m.WalletCurrency);
            method.Ignore(m => m.Minimum);
            method.Ignore(m => m.Maximum);
        });

        modelBuilder.Entity<TradeRate>(rate =>
        {
            rate.ToTable("rates", "p2p");
            rate.HasKey(r => r.Id);
            rate.Property(r => r.Id).HasColumnName("id").ValueGeneratedOnAdd();
            rate.Property(r => r.MethodId).HasColumnName("method_id");
            rate.Property(r => r.Side).HasColumnName("side");
            rate.Property(r => r.WalletCurrencyCode).HasColumnName("wallet_currency");
            rate.Property(r => r.WalletScale).HasColumnName("wallet_scale");
            rate.Property(r => r.Rate).HasColumnName("rate").HasPrecision(24, 8);
            rate.Property(r => r.EffectiveFrom).HasColumnName("effective_from");
            rate.Property(r => r.SetBy).HasColumnName("set_by");
        });

        modelBuilder.Entity<Trade>(trade =>
        {
            trade.ToTable("trades", "p2p");
            trade.HasKey(t => t.Id);
            trade.Property(t => t.Id).HasColumnName("id");
            trade.Property(t => t.Seq).HasColumnName("seq").ValueGeneratedOnAdd();
            trade.Property(t => t.UserId).HasColumnName("user_id");
            trade.Property(t => t.UserName).HasColumnName("user_name");
            trade.Property(t => t.Side).HasColumnName("side");
            trade.Property(t => t.MethodId).HasColumnName("method_id");
            trade.Property(t => t.MethodName).HasColumnName("method_name");
            trade.Property(t => t.WalletCurrencyCode).HasColumnName("wallet_currency");
            trade.Property(t => t.WalletScale).HasColumnName("wallet_scale");
            trade.Property(t => t.AmountMinor).HasColumnName("amount_minor");
            trade.Property(t => t.FeeMinor).HasColumnName("fee_minor");
            trade.Property(t => t.LocalCurrencyCode).HasColumnName("local_currency");
            trade.Property(t => t.LocalScale).HasColumnName("local_scale");
            trade.Property(t => t.LocalMinor).HasColumnName("local_minor");
            trade.Property(t => t.Rate).HasColumnName("rate").HasPrecision(24, 8);
            trade.Property(t => t.Reference).HasColumnName("reference");
            trade.Property(t => t.Status).HasColumnName("status");
            trade.Property(t => t.SettleIntent).HasColumnName("settle_intent");
            trade.Property(t => t.FailureReason).HasColumnName("failure_reason");
            trade.Property(t => t.CreatedAt).HasColumnName("created_at");
            trade.Property(t => t.ExpiresAt).HasColumnName("expires_at");
            trade.Property(t => t.SettledAt).HasColumnName("settled_at");
            trade.Property(t => t.CommitPostingId).HasColumnName("commit_posting_id");
            trade.Property(t => t.SettlePostingId).HasColumnName("settle_posting_id");
            trade.Property(t => t.OperatorId).HasColumnName("operator_id");
            trade.Property(t => t.OperatorReference).HasColumnName("operator_reference");
            trade.Ignore(t => t.WalletCurrency);
            trade.Ignore(t => t.LocalCurrency);
            trade.Ignore(t => t.Amount);
            trade.Ignore(t => t.Fee);
            trade.Ignore(t => t.Local);
            trade.Ignore(t => t.Net);
        });
    }
}

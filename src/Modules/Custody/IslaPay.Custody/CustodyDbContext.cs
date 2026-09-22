using IslaPay.Platform;
using Microsoft.EntityFrameworkCore;

namespace IslaPay.Custody;

/// <summary>A row of <c>custody.addresses</c>.</summary>
public sealed class DepositAddress
{
    public Guid Id { get; set; }

    public string UserId { get; set; } = string.Empty;

    public string Network { get; set; } = string.Empty;

    public string CurrencyCode { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;

    /// <summary>Opaque: whatever the custodian needs to find the key again.</summary>
    public string CustodianRef { get; set; } = string.Empty;

    public DateTimeOffset IssuedAt { get; set; }

    public Currency Currency => CurrencyExtensions.ParseCode(CurrencyCode);
}

/// <summary>A row of <c>custody.deposits</c>.</summary>
public sealed class Deposit
{
    public Guid Id { get; set; }

    public string UserId { get; set; } = string.Empty;

    public Guid AddressId { get; set; }

    public string Network { get; set; } = string.Empty;

    public string CurrencyCode { get; set; } = string.Empty;

    public string TxHash { get; set; } = string.Empty;

    public int OutputIndex { get; set; }

    public long AmountMinor { get; set; }

    public int Confirmations { get; set; }

    public int RequiredConfirmations { get; set; }

    public string Status { get; set; } = string.Empty;

    public Guid? CreditPostingId { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? CreditedAt { get; set; }

    public Currency Currency => CurrencyExtensions.ParseCode(CurrencyCode);

    public Money Amount => Money.FromMinorUnits(AmountMinor, Currency);

    public bool IsFinal => Confirmations >= RequiredConfirmations;
}

/// <summary>
/// EF Core over the <c>custody</c> schema, as a mapper only.
/// </summary>
/// <remarks>
/// The schema is the <c>.sql</c> under <c>Migrations/</c> and nothing here
/// creates or alters a table. Where a row lock is the point — claiming a
/// deposit to credit — the statement is raw SQL through <c>FromSql</c>,
/// because <c>FOR UPDATE</c> is not something a change tracker can express.
/// </remarks>
public sealed class CustodyDbContext : DbContext
{
    public CustodyDbContext(DbContextOptions<CustodyDbContext> options)
        : base(options)
    {
    }

    public DbSet<DepositAddress> Addresses => Set<DepositAddress>();

    public DbSet<Deposit> Deposits => Set<Deposit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<DepositAddress>(entity =>
        {
            entity.ToTable("addresses", "custody");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Id).HasColumnName("id");
            entity.Property(a => a.UserId).HasColumnName("user_id");
            entity.Property(a => a.Network).HasColumnName("network");
            entity.Property(a => a.CurrencyCode).HasColumnName("currency");
            entity.Property(a => a.Address).HasColumnName("address");
            entity.Property(a => a.CustodianRef).HasColumnName("custodian_ref");
            entity.Property(a => a.IssuedAt).HasColumnName("issued_at");
        });

        modelBuilder.Entity<Deposit>(entity =>
        {
            entity.ToTable("deposits", "custody");
            entity.HasKey(d => d.Id);
            entity.Property(d => d.Id).HasColumnName("id");
            entity.Property(d => d.UserId).HasColumnName("user_id");
            entity.Property(d => d.AddressId).HasColumnName("address_id");
            entity.Property(d => d.Network).HasColumnName("network");
            entity.Property(d => d.CurrencyCode).HasColumnName("currency");
            entity.Property(d => d.TxHash).HasColumnName("tx_hash");
            entity.Property(d => d.OutputIndex).HasColumnName("output_index");
            entity.Property(d => d.AmountMinor).HasColumnName("amount_minor");
            entity.Property(d => d.Confirmations).HasColumnName("confirmations");
            entity.Property(d => d.RequiredConfirmations)
                .HasColumnName("required_confirmations");
            entity.Property(d => d.Status).HasColumnName("status");
            entity.Property(d => d.CreditPostingId).HasColumnName("credit_posting_id");
            entity.Property(d => d.FirstSeenAt).HasColumnName("first_seen_at");
            entity.Property(d => d.UpdatedAt).HasColumnName("updated_at");
            entity.Property(d => d.CreditedAt).HasColumnName("credited_at");
        });
    }
}

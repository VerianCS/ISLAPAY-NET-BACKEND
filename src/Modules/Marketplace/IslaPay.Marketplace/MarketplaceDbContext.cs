using IslaPay.Platform;
using Microsoft.EntityFrameworkCore;

namespace IslaPay.Marketplace;

/// <summary>A row of <c>marketplace.listings</c>.</summary>
/// <remarks>
/// A row, not a domain object. The rules live in
/// <see cref="MarketplaceService"/>; this only knows how to be stored, which
/// is why the money is two scalar columns here and a <see cref="Money"/> only
/// where it is read.
/// </remarks>
public sealed class Listing
{
    public Guid Id { get; set; }

    /// <summary>Insertion order. Assigned by the database; the cursor is built from it.</summary>
    public long Seq { get; set; }

    public string SellerId { get; set; } = string.Empty;

    /// <summary>The seller's name as it stood when the item was listed.</summary>
    public string SellerName { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string Condition { get; set; } = string.Empty;

    public string CurrencyCode { get; set; } = string.Empty;

    public long PriceMinor { get; set; }

    public string Location { get; set; } = string.Empty;

    public IList<string> Photos { get; set; } = new List<string>();

    public string Status { get; set; } = string.Empty;

    public DateTimeOffset PublishedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The price, reassembled from the two columns that hold it.</summary>
    public Money Price => Money.FromMinorUnits(PriceMinor, CurrencyExtensions.ParseCode(CurrencyCode));
}

/// <summary>A row of <c>marketplace.orders</c>.</summary>
public sealed class Order
{
    public Guid Id { get; set; }

    public long Seq { get; set; }

    public Guid ListingId { get; set; }

    /// <summary>
    /// The foreign key's other end. Never loaded: everything a response needs
    /// about the listing is copied onto the order when the money is locked.
    /// </summary>
    public Listing? Listing { get; set; }

    public string BuyerId { get; set; } = string.Empty;

    /// <summary>
    /// Copied from the listing when the money was locked, not joined.
    /// </summary>
    /// <remarks>
    /// Who gets paid is decided at the moment of the hold. A join would let a
    /// later edit to the listing change it, which is a way to be paid for
    /// somebody else's sale.
    /// </remarks>
    public string SellerId { get; set; } = string.Empty;

    public string BuyerName { get; set; } = string.Empty;

    public string SellerName { get; set; } = string.Empty;

    /// <summary>The listing's title as it stood when the money was locked.</summary>
    public string ListingTitle { get; set; } = string.Empty;

    public string CurrencyCode { get; set; } = string.Empty;

    /// <summary>The price as it stood when the buyer committed. Also frozen deliberately.</summary>
    public long AmountMinor { get; set; }

    public long FeeMinor { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string? RefundReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? SettledAt { get; set; }

    public Guid? HoldPostingId { get; set; }

    public Guid? SettlePostingId { get; set; }

    public Money Amount => Money.FromMinorUnits(AmountMinor, CurrencyExtensions.ParseCode(CurrencyCode));

    public Money Fee => Money.FromMinorUnits(FeeMinor, CurrencyExtensions.ParseCode(CurrencyCode));

    /// <summary>What reaches the seller: the price less IslaPay's commission.</summary>
    public Money SellerReceives => Amount - Fee;
}

/// <summary>
/// The marketplace's tables, mapped.
/// </summary>
/// <remarks>
/// <para>
/// EF Core is used here as a mapper and nothing else. The schema is owned by
/// <c>Migrations/001_marketplace.sql</c> and applied by the platform's
/// migrator, so every table and column is named explicitly below rather than
/// inferred from a convention: the mapping has to follow the schema, and a
/// convention would let the two drift apart silently the first time somebody
/// renames a property.
/// </para>
/// <para>
/// There are no EF migrations and there is no <c>__EFMigrationsHistory</c>
/// table. <c>dotnet ef</c> is not part of any workflow in this repository.
/// </para>
/// <para>
/// The ledger deliberately does not work this way. Its correctness is in the
/// order rows are locked in, and that has to be visible in the code that
/// depends on it — see <c>ARCHITECTURE.md</c>.
/// </para>
/// </remarks>
public sealed class MarketplaceDbContext : DbContext
{
    public MarketplaceDbContext(DbContextOptions<MarketplaceDbContext> options)
        : base(options)
    {
    }

    public DbSet<Listing> Listings => Set<Listing>();

    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Listing>(listing =>
        {
            listing.ToTable("listings", "marketplace");
            listing.HasKey(l => l.Id);
            listing.Property(l => l.Id).HasColumnName("id");
            listing.Property(l => l.Seq).HasColumnName("seq").ValueGeneratedOnAdd();
            listing.Property(l => l.SellerId).HasColumnName("seller_id");
            listing.Property(l => l.SellerName).HasColumnName("seller_name");
            listing.Property(l => l.Title).HasColumnName("title");
            listing.Property(l => l.Description).HasColumnName("description");
            listing.Property(l => l.Category).HasColumnName("category");
            listing.Property(l => l.Condition).HasColumnName("condition");
            listing.Property(l => l.CurrencyCode).HasColumnName("currency");
            listing.Property(l => l.PriceMinor).HasColumnName("price_minor");
            listing.Property(l => l.Location).HasColumnName("location");
            listing.Property(l => l.Photos).HasColumnName("photos");
            listing.Property(l => l.Status).HasColumnName("status");
            listing.Property(l => l.PublishedAt).HasColumnName("published_at");
            listing.Property(l => l.UpdatedAt).HasColumnName("updated_at");
            listing.Ignore(l => l.Price);
        });

        modelBuilder.Entity<Order>(order =>
        {
            order.ToTable("orders", "marketplace");
            order.HasKey(o => o.Id);
            order.Property(o => o.Id).HasColumnName("id");
            order.Property(o => o.Seq).HasColumnName("seq").ValueGeneratedOnAdd();
            order.Property(o => o.ListingId).HasColumnName("listing_id");
            order.Property(o => o.BuyerId).HasColumnName("buyer_id");
            order.Property(o => o.SellerId).HasColumnName("seller_id");
            order.Property(o => o.BuyerName).HasColumnName("buyer_name");
            order.Property(o => o.SellerName).HasColumnName("seller_name");
            order.Property(o => o.ListingTitle).HasColumnName("listing_title");
            order.Property(o => o.CurrencyCode).HasColumnName("currency");
            order.Property(o => o.AmountMinor).HasColumnName("amount_minor");
            order.Property(o => o.FeeMinor).HasColumnName("fee_minor");
            order.Property(o => o.Code).HasColumnName("code");
            order.Property(o => o.Status).HasColumnName("status");
            order.Property(o => o.RefundReason).HasColumnName("refund_reason");
            order.Property(o => o.CreatedAt).HasColumnName("created_at");
            order.Property(o => o.ExpiresAt).HasColumnName("expires_at");
            order.Property(o => o.SettledAt).HasColumnName("settled_at");
            order.Property(o => o.HoldPostingId).HasColumnName("hold_posting_id");
            order.Property(o => o.SettlePostingId).HasColumnName("settle_posting_id");
            order.HasOne(o => o.Listing).WithMany().HasForeignKey(o => o.ListingId);
            order.Ignore(o => o.Amount);
            order.Ignore(o => o.Fee);
            order.Ignore(o => o.SellerReceives);
        });
    }
}

using Microsoft.EntityFrameworkCore;

namespace Spiza.Api.Data;

public sealed class SpizaDbContext(DbContextOptions<SpizaDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Store> Stores => Set<Store>();
    public DbSet<PriceObservation> PriceObservations => Set<PriceObservation>();
    public DbSet<SourceRun> SourceRuns => Set<SourceRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(entity =>
        {
            entity.HasIndex(x => new { x.Retailer, x.ExternalProductId }).IsUnique();
            entity.HasIndex(x => x.NormalizedBarcode);
            entity.HasIndex(x => x.Name);
            entity.Property(x => x.Retailer).HasMaxLength(80).IsRequired();
            entity.Property(x => x.ExternalProductId).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(500).IsRequired();
        });

        modelBuilder.Entity<Store>(entity =>
        {
            entity.HasIndex(x => new { x.Retailer, x.ExternalStoreId }).IsUnique();
            entity.HasIndex(x => x.City);
            entity.Property(x => x.Retailer).HasMaxLength(80).IsRequired();
            entity.Property(x => x.ExternalStoreId).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(300).IsRequired();
        });

        modelBuilder.Entity<PriceObservation>(entity =>
        {
            entity.HasIndex(x => new { x.ProductId, x.StoreId, x.PriceDate }).IsUnique();
            entity.HasIndex(x => x.PriceDate);
            entity.HasOne(x => x.Product).WithMany(x => x.Prices).HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Store).WithMany(x => x.Prices).HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(x => x.RegularPrice).HasColumnType("TEXT");
            entity.Property(x => x.PromotionalPrice).HasColumnType("TEXT");
            entity.Property(x => x.EffectivePrice).HasColumnType("TEXT");
            entity.Property(x => x.UnitPrice).HasColumnType("TEXT");
            entity.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            entity.Property(x => x.QualityFlagsJson).HasMaxLength(1000).IsRequired();
        });

        modelBuilder.Entity<SourceRun>(entity =>
        {
            entity.HasIndex(x => new { x.Source, x.StartedAt });
            entity.Property(x => x.Source).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(30).IsRequired();
        });
    }
}

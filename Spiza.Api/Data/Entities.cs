namespace Spiza.Api.Data;

public sealed class Product
{
    public int Id { get; set; }
    public required string Retailer { get; set; }
    public required string ExternalProductId { get; set; }
    public required string Name { get; set; }
    public string? Brand { get; set; }
    public string? Barcode { get; set; }
    public string? NormalizedBarcode { get; set; }
    public string? Category { get; set; }
    public decimal? Quantity { get; set; }
    public string? Unit { get; set; }
    public DateTime UpdatedAt { get; set; }
    public ICollection<PriceObservation> Prices { get; set; } = new List<PriceObservation>();
}

public sealed class Store
{
    public int Id { get; set; }
    public required string Retailer { get; set; }
    public required string ExternalStoreId { get; set; }
    public required string Name { get; set; }
    public string? Address { get; set; }
    public string? PostalCode { get; set; }
    public string? City { get; set; }
    public DateTime UpdatedAt { get; set; }
    public ICollection<PriceObservation> Prices { get; set; } = new List<PriceObservation>();
}

public sealed class PriceObservation
{
    public long Id { get; set; }
    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public int StoreId { get; set; }
    public Store Store { get; set; } = null!;
    public DateTime PriceDate { get; set; }
    public decimal? RegularPrice { get; set; }
    public decimal? PromotionalPrice { get; set; }
    public decimal? EffectivePrice { get; set; }
    public decimal? UnitPrice { get; set; }
    public string? UnitPriceUnit { get; set; }
    public DateTime? PromotionStart { get; set; }
    public DateTime? PromotionEnd { get; set; }
    public string Currency { get; set; } = "EUR";
    public DateTime RetrievedAt { get; set; }
    public string QualityFlagsJson { get; set; } = "[]";
}

public sealed class SourceRun
{
    public long Id { get; set; }
    public required string Source { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public required string Status { get; set; }
    public int StoreCount { get; set; }
    public int ProductCount { get; set; }
    public string? Error { get; set; }
}

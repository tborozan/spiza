using Spiza.Api.Sources;

namespace Spiza.Api.Tests;

public sealed class PublicCsvParserTests
{
    [Fact]
    public void ParseProducts_ParsesCroatianHeadersDecimalCommasAndQuotedDelimiters()
    {
        const string csv = """
            NAZIV PROIZVODA;ŠIFRA PROIZVODA;MARKA PROIZVODA;BARKOD;KATEGORIJA PROIZVODA;NETO KOLIČINA;JEDINICA MJERE;MALOPRODAJNA CIJENA;MPC ZA VRIJEME POSEBNOG OBLIKA PRODAJE;CIJENA ZA JEDINICU MJERE
            "Čokolada; 70%";SKU-1;Kakao;4006381333931;Hrana;0,5 kg;kg;2,49;1,99;3,98
            """;

        var products = PublicCsvParser.ParseProducts(csv);

        var product = Assert.Single(products);
        Assert.Equal("SKU-1", product.ExternalProductId);
        Assert.Equal("Čokolada; 70%", product.Name);
        Assert.Equal("4006381333931", product.Barcode);
        Assert.Equal(0.5m, product.Quantity);
        Assert.Equal("kg", product.Unit);
        Assert.Equal(2.49m, product.RegularPrice);
        Assert.Equal(1.99m, product.PromotionalPrice);
        Assert.Equal(3.98m, product.UnitPrice);
        Assert.Empty(product.QualityFlags);
    }

    [Fact]
    public void ParseProducts_FlagsBarcodeAndUnitProblems_AndSkipsRowsWithoutIdentity()
    {
        const string csv = """
            NAZIV;ŠIFRA;BARKOD;JEDINICA MJERE;MPC
            Invalid barcode;SKU-2;4006381333932;oz;1,20
            No ID or barcode;;;kg;1,00
            """;

        var product = Assert.Single(PublicCsvParser.ParseProducts(csv));

        Assert.Contains("barcode_mismatch", product.QualityFlags);
        Assert.Contains("unit_inconsistent", product.QualityFlags);
    }

    [Fact]
    public void ParseProducts_UsesBarcodeWhenExternalProductIdIsMissing()
    {
        const string csv = """
            NAZIV PROIZVODA;ŠIFRA PROIZVODA;BARKOD;MPC
            Mineralna voda;;4006381333931;0,79
            """;

        var product = Assert.Single(PublicCsvParser.ParseProducts(csv));

        Assert.Equal("4006381333931", product.ExternalProductId);
    }
}

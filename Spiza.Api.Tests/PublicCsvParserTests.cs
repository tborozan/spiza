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

    [Fact]
    public void ParseProducts_NormalizesLidlArchivePackageLabelsToKilograms()
    {
        const string csv = """
            NAZIV,ŠIFRA,NETO_KOLIČINA,JEDINICA_MJERE,MALOPRODAJNA_CIJENA,CIJENA_ZA_JEDINICU_MJERE,BARKOD
            "Čoksa čokolada banana-biskvit",6908145,0.068,68g,1.39,20.44,3850102001606
            "Gavrilović Jetrena pašteta, 100g",0002437,0.1,100g,1.39,13.9,3858881221543
            """;

        var products = PublicCsvParser.ParseProducts(csv);

        var chocolate = Assert.Single(products, product => product.Barcode == "3850102001606");
        Assert.Equal("Čoksa čokolada banana-biskvit", chocolate.Name);
        Assert.Equal(0.068m, chocolate.Quantity);
        Assert.Equal("kg", chocolate.Unit);
        Assert.Equal(20.44m, chocolate.UnitPrice);
        Assert.Equal("kg", chocolate.UnitPriceUnit);
        Assert.Empty(chocolate.QualityFlags);

        var pate = Assert.Single(products, product => product.Barcode == "3858881221543");
        Assert.Equal("Gavrilović Jetrena pašteta, 100g", pate.Name);
        Assert.Equal(0.1m, pate.Quantity);
        Assert.Equal("kg", pate.Unit);
        Assert.Equal(13.9m, pate.UnitPrice);
        Assert.Equal("kg", pate.UnitPriceUnit);
        Assert.Empty(pate.QualityFlags);
    }

    [Fact]
    public void ParseProducts_ConvertsExplicitGramsMillilitersAndPiecesToCanonicalUnits()
    {
        const string csv = """
            NAZIV;ŠIFRA;BARKOD;NETO KOLIČINA;JEDINICA MJERE;MPC;CIJENA ZA JEDINICU MJERE
            Flour;flour;4006381333931;500 g;g;1,20;2,40
            Juice;juice;3850102001606;500 ml;ml;1,00;2,00
            Single apple;apple;3858881221543;1 kom;kom;0,50;0,50
            """;

        var products = PublicCsvParser.ParseProducts(csv);

        Assert.Collection(products,
            flour => { Assert.Equal(0.5m, flour.Quantity); Assert.Equal("kg", flour.Unit); Assert.Equal("kg", flour.UnitPriceUnit); },
            juice => { Assert.Equal(0.5m, juice.Quantity); Assert.Equal("l", juice.Unit); Assert.Equal("l", juice.UnitPriceUnit); },
            apple => { Assert.Equal(1m, apple.Quantity); Assert.Equal("piece", apple.Unit); Assert.Equal("piece", apple.UnitPriceUnit); });
    }

    [Theory]
    [InlineData("3850102001606", "3850102001606")]
    [InlineData("385 010 200 1606", "3850102001606")]
    [InlineData("385-010-200-1606", "3850102001606")]
    [InlineData("item-3850102001606", null)]
    [InlineData("385010200160X", null)]
    public void ProductIdentity_NormalizeBarcodeRequiresAnExactNumericBarcode(string input, string? expected)
    {
        Assert.Equal(expected, ProductIdentity.NormalizeBarcode(input));
    }
}

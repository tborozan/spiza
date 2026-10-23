using System.IO.Compression;
using System.Net;
using System.Security;
using System.Text;
using Microsoft.Extensions.Configuration;
using Spiza.Api.Configuration;
using Spiza.Api.Sources;

namespace Spiza.Api.Tests;

public sealed class PublicPriceSourceAdapterTests
{
    private const string Barcode = "4006381333931";

    [Theory]
    [InlineData("konzum")]
    [InlineData("lidl")]
    [InlineData("plodine")]
    [InlineData("spar")]
    [InlineData("tommy")]
    [InlineData("kaufland")]
    [InlineData("dm")]
    [InlineData("ribola")]
    [InlineData("metro")]
    [InlineData("eurospin")]
    public async Task FetchAsync_ParsesPublicIndexAndPriceFile(string retailer)
    {
        var options = CreateOptions();
        var handler = new FixtureHandler(uri => GetFixtureResponse(retailer, uri, options));
        using var client = new HttpClient(handler);
        var source = CreateSource(retailer, client, options);

        SourceFetchResult result;
        try
        {
            result = await source.FetchAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            throw new Xunit.Sdk.XunitException($"{retailer} adapter failed: {exception.Message}\nRequested URLs:\n{string.Join('\n', handler.Requests)}");
        }

        Assert.Equal(retailer, source.Name);
        Assert.True(source.Enabled);
        var list = Assert.Single(result.PriceLists);
        var product = Assert.Single(list.Products);
        Assert.Equal("SKU-1", product.ExternalProductId);
        Assert.Equal(Barcode, product.Barcode);
        Assert.Equal(2.49m, product.RegularPrice);
        Assert.Equal(0.5m, product.Quantity);
        Assert.NotNull(list.Store.ExternalStoreId);
        if (retailer == "kaufland")
        {
            Assert.Equal("1630", list.Store.ExternalStoreId);
            Assert.Equal("Split", list.Store.City);
            Assert.Equal("114 Brigade 6", list.Store.Address);
        }
        Assert.True(handler.Requests.Count >= 2, "The adapter should request its index and at least one price file.");
    }

    [Fact]
    public async Task Plodine_FetchAndStreamAsync_StreamsParsedStoreListToConsumer()
    {
        var options = CreateOptions();
        var handler = new FixtureHandler(uri => GetFixtureResponse("plodine", uri, options));
        using var client = new HttpClient(handler);
        var source = new PlodinePublicPriceSource(client, options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PlodinePublicPriceSource>.Instance);
        var consumedCount = 0;
        string? externalProductId = null;

        var failed = await source.FetchAndStreamAsync((list, _) =>
        {
            consumedCount++;
            externalProductId = Assert.Single(list.Products).ExternalProductId;
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(0, failed);
        Assert.Equal(1, consumedCount);
        Assert.Equal("SKU-1", externalProductId);
    }

    private static IPublicPriceSource CreateSource(string retailer, HttpClient client, SpizaOptions options) => retailer switch
    {
        "konzum" => new KonzumPublicPriceSource(client, options, Microsoft.Extensions.Logging.Abstractions.NullLogger<KonzumPublicPriceSource>.Instance),
        "lidl" => new LidlPublicPriceSource(client, options),
        "plodine" => new PlodinePublicPriceSource(client, options, Microsoft.Extensions.Logging.Abstractions.NullLogger<PlodinePublicPriceSource>.Instance),
        "spar" => new SparPublicPriceSource(client, options),
        "tommy" => new TommyPublicPriceSource(client, options),
        "kaufland" => new KauflandPublicPriceSource(client, options, Microsoft.Extensions.Logging.Abstractions.NullLogger<KauflandPublicPriceSource>.Instance),
        "dm" => new DmPublicPriceSource(client, options),
        "ribola" => new RibolaPublicPriceSource(client, options),
        "metro" => new MetroPublicPriceSource(client, options),
        "eurospin" => new EurospinPublicPriceSource(client, options),
        _ => throw new ArgumentOutOfRangeException(nameof(retailer))
    };

    private static SpizaOptions CreateOptions() => SpizaOptions.FromConfiguration(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SPIZA_SOURCE_REQUEST_DELAY_MS"] = "0",
            ["SPIZA_SOURCE_RETRY_COUNT"] = "0",
            ["SPIZA_SOURCE_RETRY_DELAY_SECONDS"] = "0",
            ["SPIZA_SOURCE_REQUEST_BUDGET"] = "50"
        })
        .Build());

    private static HttpResponseMessage GetFixtureResponse(string retailer, Uri uri, SpizaOptions options)
    {
        var date = options.Today;
        var commonCsv = """
            NAZIV PROIZVODA;ŠIFRA PROIZVODA;MARKA PROIZVODA;BARKOD;KATEGORIJA PROIZVODA;NETO KOLIČINA;JEDINICA MJERE;MALOPRODAJNA CIJENA;MPC ZA VRIJEME POSEBNOG OBLIKA PRODAJE;CIJENA ZA JEDINICU MJERE
            Mineralna voda;SKU-1;VodaCo;4006381333931;Piće;0,5 l;l;2,49;1,99;4,98
            """;

        if (retailer == "konzum")
        {
            if (uri.AbsolutePath == "/cjenici")
            {
                var page = uri.Query.Contains("page=1", StringComparison.Ordinal)
                    ? "<a href=\"/cjenici/store.csv?title=Supermarket%2CUlica_1_21000_Split%2C1001%2Cfoo%2Cbar%2Cbaz\" format=\"csv\">CSV</a>"
                    : string.Empty;
                return Text(page);
            }

            return Text(commonCsv);
        }

        if (retailer == "lidl")
        {
            var file = $"Supermarket%20123__Ulica_1__10000__ZAGREB_1_{date:dd.MM.yyyy}_07.00.csv";
            return uri.AbsolutePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                ? Text(commonCsv)
                : Text($"<a href=\"https://www.lidl.hr/c/cijene/{file}\">Download</a>");
        }

        if (retailer == "plodine")
        {
            var archivePath = $"/cjenici/cjenici_{date:dd_MM_yyyy}_daily.zip";
            return uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? Bytes(CreateZip(("SUPERMARKET_ULICA_1_10000_ZAGREB_1001_1_123.csv", commonCsv)))
                : Text($"<a href=\"{archivePath}\">Preuzmi</a>");
        }

        if (retailer == "spar")
        {
            if (uri.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                return Text("{\"files\":[{\"URL\":\"/prices/store.csv\",\"name\":\"Supermarket_zagreb_ulica_1234_SPAR_store_2026.csv\"}]}");
            }

            return Text(commonCsv);
        }

        if (retailer == "tommy")
        {
            if (uri.AbsolutePath.EndsWith("store-prices-tables", StringComparison.OrdinalIgnoreCase))
            {
                return Text("{\"hydra:member\":[{\"fileName\":\"Supermarket,Ulica 1,21000 Split,1001\",\"@id\":\"/api/v2/shop/store-prices-tables/1001/download\"}]}");
            }

            return Text(commonCsv);
        }

        if (retailer == "kaufland")
        {
            if (uri.AbsolutePath.EndsWith("popis-mpc.html", StringComparison.OrdinalIgnoreCase))
            {
                return Text("<div data-component=\"AssetList\" data-props=\"{&quot;settings&quot;:{&quot;dataUrlAssets&quot;:&quot;/assets.json&quot;}}\"></div>");
            }

            if (uri.AbsolutePath.EndsWith("assets.json", StringComparison.OrdinalIgnoreCase))
            {
                return Text($"[{{\"label\":\"Hipermarket_114__Brigade_6_Split_1630_{date:ddMMyyyy}_7-30.csv\",\"path\":\"/files/kaufland.csv\"}}]");
            }

            return Text("NAZIV PROIZVODA\tŠIFRA PROIZVODA\tMARKA PROIZVODA\tBARKOD\tWG\tNETO KOLIČINA(KG)\tJEDINICA MJERE\tMALOPROD.CIJENA(EUR)\tMPC POSEB.OBLIK PROD\tCIJENA JED.MJ.(EUR)\nMineralna voda\tSKU-1\tVodaCo\t4006381333931\tPiće\t0,5 l\tl\t2,49\t1,99\t4,98");
        }

        if (retailer == "dm")
        {
            if (uri.AbsolutePath.EndsWith("2906632", StringComparison.OrdinalIgnoreCase))
            {
                return Text($"{{\"mainData\":[{{\"type\":\"CMDownload\",\"data\":{{\"headline\":\"Važeći cjenik {date:d.M.yyyy}\",\"linkTarget\":\"/assets/dm.xlsx\"}}}}]}}");
            }

            return Bytes(CreateDmWorkbook());
        }

        if (retailer == "ribola")
        {
            if (uri.AbsolutePath.EndsWith("ribola-cjenici/", StringComparison.OrdinalIgnoreCase))
            {
                return Text("<a href=\"/cjenici/split.xml\">Split</a>");
            }

            return Text("""
                <Cjenik>
                  <ProdajniObjekt><Oblik>Supermarket</Oblik><Oznaka>R-1</Oznaka><Adresa>Ulica 1 Split</Adresa></ProdajniObjekt>
                  <Proizvod><NazivProizvoda>Mineralna voda</NazivProizvoda><SifraProizvoda>SKU-1</SifraProizvoda><MarkaProizvoda>VodaCo</MarkaProizvoda><Barkod>4006381333931</Barkod><KategorijeProizvoda>Piće</KategorijeProizvoda><NetoKolicina>0,5 l</NetoKolicina><JedinicaMjere>l</JedinicaMjere><MaloprodajnaCijena>2,49</MaloprodajnaCijena><MaloprodajnaCijenaAkcija>1,99</MaloprodajnaCijenaAkcija><CijenaZaJedinicuMjere>4,98</CijenaZaJedinicuMjere></Proizvod>
                </Cjenik>
                """);
        }

        if (retailer == "metro")
        {
            if (uri.AbsolutePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                return Text("NAZIV,SIFRA,MARKA,BARKOD,KATEGORIJA,NETO_KOLICINA,JED_MJERE,MPC,POSEBNA_PRODAJA,CIJENA_PO_MJERI\nMineralna voda,SKU-1,VodaCo,4006381333931,Piće,0.5 l,l,2.49,1.99,4.98");
            }

            var filename = $"skladiste_METRO_{date:yyyyMMdd}T1149_S20_CESTA_PAPE_IVANA_PAVLA_II_3%2C_SPLIT.csv";
            return Text($"<a href=\"/{filename}\">CSV</a>");
        }

        if (retailer == "eurospin")
        {
            return uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? Bytes(CreateZip(($"cjenik-1001-Ulica_1-Zagreb-10000-{date:dd.MM.yyyy}-store.csv", commonCsv)))
                : Text($"<select><option value=\"/cjenici/cjenik-{date:dd.MM.yyyy}.zip\">Cjenik</option></select>");
        }

        throw new ArgumentOutOfRangeException(nameof(retailer));
    }

    private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, "text/plain")
    };

    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes)
    };

    private static byte[] CreateZip((string Name, string Content) entry)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry(entry.Name).Open(), Encoding.UTF8);
            writer.Write(entry.Content);
        }

        return output.ToArray();
    }

    private static byte[] CreateDmWorkbook()
    {
        var rows = new string?[][]
        {
            ["Naziv + šifra", null, "Marka", "Barkod", "Kategorija proizvoda", "Neto količina", "Jedinica mjere", "MPC", "Cijena za jedinicu mjere", "MPC za vrijeme posebnog oblika prodaje (rasprodaja proizvoda koji izlaze iz asortimana)"],
            ["Mineralna voda", "SKU-1", "VodaCo", Barcode, "Piće", "0,5 l", "l", "2,49", "4,98", "1,99"]
        };
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Prices\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            WriteEntry(archive, "xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
            var sheetRows = rows.Select((row, rowIndex) =>
            {
                var cells = row.Select((value, columnIndex) => (value, columnIndex))
                    .Where(item => item.value is not null)
                    .Select(item => $"<c r=\"{ColumnName(item.columnIndex)}{rowIndex + 1}\" t=\"inlineStr\"><is><t>{SecurityElement.Escape(item.value)}</t></is></c>");
                return $"<row r=\"{rowIndex + 1}\">{string.Concat(cells)}</row>";
            });
            WriteEntry(archive, "xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>{string.Concat(sheetRows)}</sheetData></worksheet>");
        }

        return output.ToArray();
    }

    private static string ColumnName(int index)
    {
        var result = string.Empty;
        for (index++; index > 0; index = (index - 1) / 26)
        {
            result = (char)('A' + (index - 1) % 26) + result;
        }

        return result;
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private sealed class FixtureHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI is required.");
            Requests.Add(uri);
            return Task.FromResult(respond(uri));
        }
    }
}

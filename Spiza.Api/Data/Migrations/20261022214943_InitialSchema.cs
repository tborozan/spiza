using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spiza.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Products",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Retailer = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    ExternalProductId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Brand = table.Column<string>(type: "TEXT", nullable: true),
                    Barcode = table.Column<string>(type: "TEXT", nullable: true),
                    NormalizedBarcode = table.Column<string>(type: "TEXT", nullable: true),
                    Category = table.Column<string>(type: "TEXT", nullable: true),
                    Quantity = table.Column<decimal>(type: "TEXT", nullable: true),
                    Unit = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SourceRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    StoreCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ProductCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Stores",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Retailer = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    ExternalStoreId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Address = table.Column<string>(type: "TEXT", nullable: true),
                    PostalCode = table.Column<string>(type: "TEXT", nullable: true),
                    City = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Stores", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PriceObservations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProductId = table.Column<int>(type: "INTEGER", nullable: false),
                    StoreId = table.Column<int>(type: "INTEGER", nullable: false),
                    PriceDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RegularPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    PromotionalPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    EffectivePrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    UnitPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    UnitPriceUnit = table.Column<string>(type: "TEXT", nullable: true),
                    PromotionStart = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PromotionEnd = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    RetrievedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    QualityFlagsJson = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceObservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PriceObservations_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PriceObservations_Stores_StoreId",
                        column: x => x.StoreId,
                        principalTable: "Stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PriceObservations_PriceDate",
                table: "PriceObservations",
                column: "PriceDate");

            migrationBuilder.CreateIndex(
                name: "IX_PriceObservations_ProductId_StoreId_PriceDate",
                table: "PriceObservations",
                columns: new[] { "ProductId", "StoreId", "PriceDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriceObservations_StoreId",
                table: "PriceObservations",
                column: "StoreId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_Name",
                table: "Products",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Products_NormalizedBarcode",
                table: "Products",
                column: "NormalizedBarcode");

            migrationBuilder.CreateIndex(
                name: "IX_Products_Retailer_ExternalProductId",
                table: "Products",
                columns: new[] { "Retailer", "ExternalProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceRuns_Source_StartedAt",
                table: "SourceRuns",
                columns: new[] { "Source", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Stores_City",
                table: "Stores",
                column: "City");

            migrationBuilder.CreateIndex(
                name: "IX_Stores_Retailer_ExternalStoreId",
                table: "Stores",
                columns: new[] { "Retailer", "ExternalStoreId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PriceObservations");

            migrationBuilder.DropTable(
                name: "SourceRuns");

            migrationBuilder.DropTable(
                name: "Products");

            migrationBuilder.DropTable(
                name: "Stores");
        }
    }
}

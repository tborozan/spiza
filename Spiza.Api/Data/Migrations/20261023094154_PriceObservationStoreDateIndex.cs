using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spiza.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PriceObservationStoreDateIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PriceObservations_StoreId",
                table: "PriceObservations");

            migrationBuilder.CreateIndex(
                name: "IX_PriceObservations_StoreId_PriceDate",
                table: "PriceObservations",
                columns: new[] { "StoreId", "PriceDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PriceObservations_StoreId_PriceDate",
                table: "PriceObservations");

            migrationBuilder.CreateIndex(
                name: "IX_PriceObservations_StoreId",
                table: "PriceObservations",
                column: "StoreId");
        }
    }
}

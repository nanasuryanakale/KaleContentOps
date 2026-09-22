using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KaleContentOps.Migrations
{
    /// <inheritdoc />
    public partial class AddCommerceMetricsToContentMetric : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AvgCustomers",
                table: "ContentMetrics",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ClickThroughRate",
                table: "ContentMetrics",
                type: "decimal(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "GmvAmount",
                table: "ContentMetrics",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GmvCurrency",
                table: "ContentMetrics",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HashtagsJson",
                table: "ContentMetrics",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ItemsSold",
                table: "ContentMetrics",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProductsJson",
                table: "ContentMetrics",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SkuOrders",
                table: "ContentMetrics",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AvgCustomers",
                table: "ContentMetrics");

            migrationBuilder.DropColumn(
                name: "ClickThroughRate",
                table: "ContentMetrics");

            migrationBuilder.DropColumn(
                name: "GmvAmount",
                table: "ContentMetrics");

            migrationBuilder.DropColumn(
                name: "GmvCurrency",
                table: "ContentMetrics");

            migrationBuilder.DropColumn(
                name: "HashtagsJson",
                table: "ContentMetrics");

            migrationBuilder.DropColumn(
                name: "ItemsSold",
                table: "ContentMetrics");

            migrationBuilder.DropColumn(
                name: "ProductsJson",
                table: "ContentMetrics");

            migrationBuilder.DropColumn(
                name: "SkuOrders",
                table: "ContentMetrics");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KaleContentOps.Migrations
{
    /// <inheritdoc />
    public partial class AddContentMetricVideoQualityMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "VToLClicks",
                table: "ContentMetrics",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "VToLRate",
                table: "ContentMetrics",
                type: "decimal(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "VideoFinishRate",
                table: "ContentMetrics",
                type: "decimal(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VToLClicks",
                table: "ContentMetrics");

            migrationBuilder.DropColumn(
                name: "VToLRate",
                table: "ContentMetrics");

            migrationBuilder.DropColumn(
                name: "VideoFinishRate",
                table: "ContentMetrics");
        }
    }
}

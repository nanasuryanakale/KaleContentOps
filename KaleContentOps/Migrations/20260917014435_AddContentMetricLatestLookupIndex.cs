using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KaleContentOps.Migrations
{
    /// <inheritdoc />
    public partial class AddContentMetricLatestLookupIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ContentMetrics_ContentLogId",
                table: "ContentMetrics");

            migrationBuilder.CreateIndex(
                name: "IX_ContentMetrics_ContentLogId_CapturedAt",
                table: "ContentMetrics",
                columns: new[] { "ContentLogId", "CapturedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ContentMetrics_ContentLogId_CapturedAt",
                table: "ContentMetrics");

            migrationBuilder.CreateIndex(
                name: "IX_ContentMetrics_ContentLogId",
                table: "ContentMetrics",
                column: "ContentLogId");
        }
    }
}

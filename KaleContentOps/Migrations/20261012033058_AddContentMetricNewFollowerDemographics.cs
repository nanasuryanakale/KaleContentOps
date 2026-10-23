using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KaleContentOps.Migrations
{
    /// <inheritdoc />
    public partial class AddContentMetricNewFollowerDemographics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NewFollowerDemographicsJson",
                table: "ContentMetrics",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NewFollowerDemographicsJson",
                table: "ContentMetrics");
        }
    }
}

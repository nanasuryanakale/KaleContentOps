using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KaleContentOps.Migrations
{
    /// <inheritdoc />
    public partial class AddTargetsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Targets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ContentTypeId = table.Column<int>(type: "int", nullable: false),
                    TargetUpload = table.Column<int>(type: "int", nullable: false),
                    TargetViews = table.Column<long>(type: "bigint", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveTo = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Targets", x => x.Id);
                    table.CheckConstraint("CK_Targets_EffectiveDates", "[EffectiveTo] IS NULL OR [EffectiveTo] >= [EffectiveFrom]");
                    table.CheckConstraint("CK_Targets_TargetUpload_NonNegative", "[TargetUpload] >= 0");
                    table.CheckConstraint("CK_Targets_TargetViews_NonNegative", "[TargetViews] >= 0");
                    table.ForeignKey(
                        name: "FK_Targets_ContentTypes_ContentTypeId",
                        column: x => x.ContentTypeId,
                        principalTable: "ContentTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Targets_ContentTypeId_Active",
                table: "Targets",
                column: "ContentTypeId",
                unique: true,
                filter: "[EffectiveTo] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Targets_ContentTypeId_EffectiveFrom_EffectiveTo",
                table: "Targets",
                columns: new[] { "ContentTypeId", "EffectiveFrom", "EffectiveTo" })
                .Annotation("SqlServer:Include", new[] { "TargetUpload", "TargetViews" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Targets");
        }
    }
}

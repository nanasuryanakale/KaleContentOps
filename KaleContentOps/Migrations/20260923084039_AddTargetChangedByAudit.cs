using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KaleContentOps.Migrations
{
    /// <inheritdoc />
    public partial class AddTargetChangedByAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ChangedByUserId",
                table: "Targets",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Targets_ChangedByUserId",
                table: "Targets",
                column: "ChangedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Targets_ContentTypeId_EffectiveFrom_Unique",
                table: "Targets",
                columns: new[] { "ContentTypeId", "EffectiveFrom" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Targets_AspNetUsers_ChangedByUserId",
                table: "Targets",
                column: "ChangedByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Targets_AspNetUsers_ChangedByUserId",
                table: "Targets");

            migrationBuilder.DropIndex(
                name: "IX_Targets_ChangedByUserId",
                table: "Targets");

            migrationBuilder.DropIndex(
                name: "IX_Targets_ContentTypeId_EffectiveFrom_Unique",
                table: "Targets");

            migrationBuilder.DropColumn(
                name: "ChangedByUserId",
                table: "Targets");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KaleContentOps.Migrations
{
    /// <inheritdoc />
    public partial class AddBatchSyncJobCheckpointAndItemDedup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TikTokBatchSyncJobItems_TikTokBatchSyncJobId",
                table: "TikTokBatchSyncJobItems");

            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "TikTokBatchSyncJobs",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "CollectionCompleted",
                table: "TikTokBatchSyncJobs",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CurrentPageToken",
                table: "TikTokBatchSyncJobs",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextAttemptAt",
                table: "TikTokBatchSyncJobs",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "TikTokBatchSyncJobs",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "VideoId",
                table: "TikTokBatchSyncJobItems",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.CreateIndex(
                name: "IX_TikTokBatchSyncJobItems_TikTokBatchSyncJobId_VideoId",
                table: "TikTokBatchSyncJobItems",
                columns: new[] { "TikTokBatchSyncJobId", "VideoId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TikTokBatchSyncJobItems_TikTokBatchSyncJobId_VideoId",
                table: "TikTokBatchSyncJobItems");

            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "TikTokBatchSyncJobs");

            migrationBuilder.DropColumn(
                name: "CollectionCompleted",
                table: "TikTokBatchSyncJobs");

            migrationBuilder.DropColumn(
                name: "CurrentPageToken",
                table: "TikTokBatchSyncJobs");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "TikTokBatchSyncJobs");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "TikTokBatchSyncJobs");

            migrationBuilder.AlterColumn<string>(
                name: "VideoId",
                table: "TikTokBatchSyncJobItems",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(100)",
                oldMaxLength: 100);

            migrationBuilder.CreateIndex(
                name: "IX_TikTokBatchSyncJobItems_TikTokBatchSyncJobId",
                table: "TikTokBatchSyncJobItems",
                column: "TikTokBatchSyncJobId");
        }
    }
}

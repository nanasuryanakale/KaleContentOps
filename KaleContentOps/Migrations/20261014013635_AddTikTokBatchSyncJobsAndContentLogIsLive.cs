using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KaleContentOps.Migrations
{
    /// <inheritdoc />
    public partial class AddTikTokBatchSyncJobsAndContentLogIsLive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsLive",
                table: "ContentLogs",
                type: "bit",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TikTokBatchSyncJobs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TikTokShopId = table.Column<long>(type: "bigint", nullable: false),
                    StartDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TotalPagesCollected = table.Column<int>(type: "int", nullable: false),
                    TotalVideoIdsFound = table.Column<int>(type: "int", nullable: false),
                    PendingItemsCount = table.Column<int>(type: "int", nullable: false),
                    ProcessingItemsCount = table.Column<int>(type: "int", nullable: false),
                    SucceededItemsCount = table.Column<int>(type: "int", nullable: false),
                    FailedItemsCount = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TikTokBatchSyncJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TikTokBatchSyncJobs_TikTokShops_TikTokShopId",
                        column: x => x.TikTokShopId,
                        principalTable: "TikTokShops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TikTokBatchSyncJobItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TikTokBatchSyncJobId = table.Column<long>(type: "bigint", nullable: false),
                    VideoId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ProcessedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TikTokBatchSyncJobItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TikTokBatchSyncJobItems_TikTokBatchSyncJobs_TikTokBatchSyncJobId",
                        column: x => x.TikTokBatchSyncJobId,
                        principalTable: "TikTokBatchSyncJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TikTokBatchSyncJobItems_TikTokBatchSyncJobId",
                table: "TikTokBatchSyncJobItems",
                column: "TikTokBatchSyncJobId");

            migrationBuilder.CreateIndex(
                name: "IX_TikTokBatchSyncJobs_TikTokShopId",
                table: "TikTokBatchSyncJobs",
                column: "TikTokShopId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TikTokBatchSyncJobItems");

            migrationBuilder.DropTable(
                name: "TikTokBatchSyncJobs");

            migrationBuilder.DropColumn(
                name: "IsLive",
                table: "ContentLogs");
        }
    }
}

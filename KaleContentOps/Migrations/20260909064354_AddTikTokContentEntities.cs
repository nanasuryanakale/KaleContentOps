using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KaleContentOps.Migrations
{
    /// <inheritdoc />
    public partial class AddTikTokContentEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ContentLogs_VideoId",
                table: "ContentLogs");

            migrationBuilder.AddColumn<long>(
                name: "TikTokShopId",
                table: "ContentLogs",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TikTokCredentials",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AppKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    EncryptedAccessToken = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    EncryptedRefreshToken = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RefreshExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    OpenId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SellerName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Region = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TikTokCredentials", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TikTokShops",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TikTokAccountId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ShopCipher = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ShopId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ShopCode = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ShopName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Region = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    SellerType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TikTokShops", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContentLogs_TikTokShopId_VideoId",
                table: "ContentLogs",
                columns: new[] { "TikTokShopId", "VideoId" },
                unique: true,
                filter: "[TikTokShopId] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_ContentLogs_TikTokShops_TikTokShopId",
                table: "ContentLogs",
                column: "TikTokShopId",
                principalTable: "TikTokShops",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ContentLogs_TikTokShops_TikTokShopId",
                table: "ContentLogs");

            migrationBuilder.DropTable(
                name: "TikTokCredentials");

            migrationBuilder.DropTable(
                name: "TikTokShops");

            migrationBuilder.DropIndex(
                name: "IX_ContentLogs_TikTokShopId_VideoId",
                table: "ContentLogs");

            migrationBuilder.DropColumn(
                name: "TikTokShopId",
                table: "ContentLogs");

            migrationBuilder.CreateIndex(
                name: "IX_ContentLogs_VideoId",
                table: "ContentLogs",
                column: "VideoId",
                unique: true);
        }
    }
}

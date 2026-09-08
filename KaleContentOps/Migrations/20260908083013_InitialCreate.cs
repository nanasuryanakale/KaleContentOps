using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace KaleContentOps.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContentTypes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Color = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MasterPics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MasterPics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProductionMethods",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionMethods", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ContentLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    VideoId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    VideoPostTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Username = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Duration = table.Column<int>(type: "int", nullable: true),
                    VideoUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatorOpenId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatorUsername = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatorNickname = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AuthorType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ContentTypeId = table.Column<int>(type: "int", nullable: true),
                    ProductionMethodId = table.Column<int>(type: "int", nullable: true),
                    PicId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContentLogs_ContentTypes_ContentTypeId",
                        column: x => x.ContentTypeId,
                        principalTable: "ContentTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ContentLogs_MasterPics_PicId",
                        column: x => x.PicId,
                        principalTable: "MasterPics",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ContentLogs_ProductionMethods_ProductionMethodId",
                        column: x => x.ProductionMethodId,
                        principalTable: "ProductionMethods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ContentMetrics",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ContentLogId = table.Column<long>(type: "bigint", nullable: false),
                    Views = table.Column<long>(type: "bigint", nullable: true),
                    Reach = table.Column<long>(type: "bigint", nullable: true),
                    Likes = table.Column<long>(type: "bigint", nullable: true),
                    Comments = table.Column<long>(type: "bigint", nullable: true),
                    Shares = table.Column<long>(type: "bigint", nullable: true),
                    NewFollowers = table.Column<long>(type: "bigint", nullable: true),
                    AverageWatch = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    FullWatchRate = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    DemographicsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MetricStartDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MetricEndDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CapturedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentMetrics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContentMetrics_ContentLogs_ContentLogId",
                        column: x => x.ContentLogId,
                        principalTable: "ContentLogs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "ContentTypes",
                columns: new[] { "Id", "Code", "Color", "CreatedAt", "IsActive", "Name", "UpdatedAt" },
                values: new object[,]
                {
                    { 1, "KK", "yellow", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), true, "Keranjang Kuning", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified) },
                    { 2, "NON_KK", "blue", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), true, "Non-KK", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified) },
                    { 3, "AUTO_GMV_LIVE", "purple", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), true, "Auto GMV Live", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified) }
                });

            migrationBuilder.InsertData(
                table: "ProductionMethods",
                columns: new[] { "Id", "Code", "IsActive", "Name" },
                values: new object[,]
                {
                    { 1, "SELF_PRODUCE", true, "Self Produce" },
                    { 2, "AI_PRODUCE", true, "AI Produce" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContentLogs_ContentTypeId",
                table: "ContentLogs",
                column: "ContentTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_ContentLogs_PicId",
                table: "ContentLogs",
                column: "PicId");

            migrationBuilder.CreateIndex(
                name: "IX_ContentLogs_ProductionMethodId",
                table: "ContentLogs",
                column: "ProductionMethodId");

            migrationBuilder.CreateIndex(
                name: "IX_ContentLogs_VideoId",
                table: "ContentLogs",
                column: "VideoId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContentMetrics_ContentLogId",
                table: "ContentMetrics",
                column: "ContentLogId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContentMetrics");

            migrationBuilder.DropTable(
                name: "ContentLogs");

            migrationBuilder.DropTable(
                name: "ContentTypes");

            migrationBuilder.DropTable(
                name: "MasterPics");

            migrationBuilder.DropTable(
                name: "ProductionMethods");
        }
    }
}

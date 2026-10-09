using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KaleContentOps.Migrations
{
    /// <inheritdoc />
    public partial class AddMasterPicEmploymentDates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // --------------------------------------------------------------
            // Phase 2A safe (non-destructive) additive migration.
            // No table is dropped/recreated, no existing column is dropped,
            // no FK is touched, no existing row is deleted.
            //
            // The scaffolded default of 0001-01-01 was removed on purpose:
            // existing MasterPics rows must receive a meaningful legacy value,
            // never a fabricated minimum date.
            // --------------------------------------------------------------

            // 1) Add BOTH columns nullable first so the ALTER succeeds on a
            //    non-empty table without injecting a fake default value.
            migrationBuilder.AddColumn<DateOnly>(
                name: "JoinDate",
                table: "MasterPics",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ResignDate",
                table: "MasterPics",
                type: "date",
                nullable: true);

            // 2) Deterministic LEGACY backfill for JoinDate only.
            //    CreatedAt is the earliest known SYSTEM record of the PIC - an
            //    explicit approximation, NOT an assertion about the HR join date.
            //    ResignDate is deliberately left untouched: there is no
            //    authoritative resignation source for legacy rows, so it stays
            //    NULL. ResignDate is NEVER derived from UpdatedAt, CreatedAt or
            //    the current date.
            migrationBuilder.Sql("UPDATE MasterPics SET JoinDate = CAST(CreatedAt AS date) WHERE JoinDate IS NULL;");

            // 3) Only now enforce the required (NOT NULL) contract.
            migrationBuilder.AlterColumn<DateOnly>(
                name: "JoinDate",
                table: "MasterPics",
                type: "date",
                nullable: false,
                oldClrType: typeof(DateOnly),
                oldType: "date",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "JoinDate",
                table: "MasterPics");

            migrationBuilder.DropColumn(
                name: "ResignDate",
                table: "MasterPics");
        }
    }
}

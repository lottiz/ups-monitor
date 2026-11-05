using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UPSControlCenter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStartupApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "approved_at",
                schema: "ups",
                table: "orchestration_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "approved_by",
                schema: "ups",
                table: "orchestration_runs",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "approved_at",
                schema: "ups",
                table: "orchestration_runs");

            migrationBuilder.DropColumn(
                name: "approved_by",
                schema: "ups",
                table: "orchestration_runs");
        }
    }
}

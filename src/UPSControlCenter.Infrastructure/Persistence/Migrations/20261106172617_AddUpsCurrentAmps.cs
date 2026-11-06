using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UPSControlCenter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUpsCurrentAmps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "input_current_amps",
                schema: "ups",
                table: "ups_measurements",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "output_current_amps",
                schema: "ups",
                table: "ups_measurements",
                type: "numeric",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "input_current_amps",
                schema: "ups",
                table: "ups_measurements");

            migrationBuilder.DropColumn(
                name: "output_current_amps",
                schema: "ups",
                table: "ups_measurements");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ovrilo.Modules.Identity.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UserAvailability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "availability",
                schema: "identity",
                table: "users",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                // Existing accounts start online, like new ones (enum stored by name).
                defaultValue: "Online");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "availability",
                schema: "identity",
                table: "users");
        }
    }
}

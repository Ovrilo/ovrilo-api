using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ovrilo.Modules.Messaging.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DirectConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "direct_conversations",
                schema: "messaging",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_low_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_high_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_direct_conversations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_direct_conversations_user_high_id",
                schema: "messaging",
                table: "direct_conversations",
                column: "user_high_id");

            migrationBuilder.CreateIndex(
                name: "ix_direct_conversations_user_low_id_user_high_id",
                schema: "messaging",
                table: "direct_conversations",
                columns: new[] { "user_low_id", "user_high_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "direct_conversations",
                schema: "messaging");
        }
    }
}

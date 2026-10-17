using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ovrilo.Modules.Messaging.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GroupConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "group_conversations",
                schema: "messaging",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_group_conversations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "group_members",
                schema: "messaging",
                columns: table => new
                {
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_group_members", x => new { x.conversation_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_group_members_group_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalSchema: "messaging",
                        principalTable: "group_conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_group_members_user_id",
                schema: "messaging",
                table: "group_members",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "group_members",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "group_conversations",
                schema: "messaging");
        }
    }
}

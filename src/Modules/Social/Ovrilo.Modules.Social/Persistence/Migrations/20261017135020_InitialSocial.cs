using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ovrilo.Modules.Social.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSocial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "social");

            migrationBuilder.CreateTable(
                name: "blocks",
                schema: "social",
                columns: table => new
                {
                    blocker_id = table.Column<Guid>(type: "uuid", nullable: false),
                    blocked_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_blocks", x => new { x.blocker_id, x.blocked_id });
                });

            migrationBuilder.CreateTable(
                name: "friend_requests",
                schema: "social",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    requester_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_friend_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "friendships",
                schema: "social",
                columns: table => new
                {
                    user_low_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_high_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_friendships", x => new { x.user_low_id, x.user_high_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_blocks_blocked_id",
                schema: "social",
                table: "blocks",
                column: "blocked_id");

            migrationBuilder.CreateIndex(
                name: "ix_friend_requests_pending_pair",
                schema: "social",
                table: "friend_requests",
                columns: new[] { "requester_id", "recipient_id" },
                unique: true,
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_friend_requests_recipient_id_status",
                schema: "social",
                table: "friend_requests",
                columns: new[] { "recipient_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_friendships_user_high_id",
                schema: "social",
                table: "friendships",
                column: "user_high_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "blocks",
                schema: "social");

            migrationBuilder.DropTable(
                name: "friend_requests",
                schema: "social");

            migrationBuilder.DropTable(
                name: "friendships",
                schema: "social");
        }
    }
}

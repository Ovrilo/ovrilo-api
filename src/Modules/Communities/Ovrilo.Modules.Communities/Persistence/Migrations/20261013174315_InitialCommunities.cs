using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ovrilo.Modules.Communities.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCommunities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "communities");

            migrationBuilder.CreateTable(
                name: "communities",
                schema: "communities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    icon_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    visibility = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_communities", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "community_members",
                schema: "communities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    community_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    nickname = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_community_members", x => x.id);
                    table.ForeignKey(
                        name: "fk_community_members_communities_community_id",
                        column: x => x.community_id,
                        principalSchema: "communities",
                        principalTable: "communities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "invitations",
                schema: "communities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    community_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    max_uses = table.Column<int>(type: "integer", nullable: true),
                    use_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invitations", x => x.id);
                    table.ForeignKey(
                        name: "fk_invitations_communities_community_id",
                        column: x => x.community_id,
                        principalSchema: "communities",
                        principalTable: "communities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_communities_owner_id",
                schema: "communities",
                table: "communities",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "ix_community_members_community_id_user_id",
                schema: "communities",
                table: "community_members",
                columns: new[] { "community_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_community_members_user_id",
                schema: "communities",
                table: "community_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_invitations_code_hash",
                schema: "communities",
                table: "invitations",
                column: "code_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_invitations_community_id",
                schema: "communities",
                table: "invitations",
                column: "community_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "community_members",
                schema: "communities");

            migrationBuilder.DropTable(
                name: "invitations",
                schema: "communities");

            migrationBuilder.DropTable(
                name: "communities",
                schema: "communities");
        }
    }
}

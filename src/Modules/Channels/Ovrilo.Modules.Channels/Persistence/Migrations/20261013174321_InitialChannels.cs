using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ovrilo.Modules.Channels.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "channels");

            migrationBuilder.CreateTable(
                name: "channels",
                schema: "channels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    community_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    topic = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    position = table.Column<int>(type: "integer", nullable: false),
                    is_private = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_channels", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_channels_community_id_position",
                schema: "channels",
                table: "channels",
                columns: new[] { "community_id", "position" });

            migrationBuilder.CreateIndex(
                name: "ix_channels_community_id_type_normalized_name",
                schema: "channels",
                table: "channels",
                columns: new[] { "community_id", "type", "normalized_name" },
                unique: true,
                filter: "deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "channels",
                schema: "channels");
        }
    }
}

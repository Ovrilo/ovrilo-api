using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ovrilo.Modules.Media.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaAssets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "media");

            migrationBuilder.CreateTable(
                name: "media_assets",
                schema: "media",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<int>(type: "integer", nullable: false),
                    community_id = table.Column<Guid>(type: "uuid", nullable: true),
                    bucket = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    object_key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    original_filename = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    declared_content_type = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    max_bytes = table.Column<long>(type: "bigint", nullable: false),
                    upload_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    detected_content_type = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    width = table.Column<int>(type: "integer", nullable: true),
                    height = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attached_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    object_purged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_assets", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_media_assets_bucket_object_key",
                schema: "media",
                table: "media_assets",
                columns: new[] { "bucket", "object_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_media_assets_owner_id_created_at",
                schema: "media",
                table: "media_assets",
                columns: new[] { "owner_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_media_assets_status_created_at",
                schema: "media",
                table: "media_assets",
                columns: new[] { "status", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "media_assets",
                schema: "media");
        }
    }
}

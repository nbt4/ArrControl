using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArrControl.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MissingSortIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "sort_title",
                table: "media_entities",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                computedColumnSql: "lower(title)",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "ix_media_entities_sort_title_id",
                table: "media_entities",
                columns: new[] { "sort_title", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_media_entities_sort_title_id",
                table: "media_entities");

            migrationBuilder.DropColumn(
                name: "sort_title",
                table: "media_entities");
        }
    }
}

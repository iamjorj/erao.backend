using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erao.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLastContextMetadataJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastContextMetadataJson",
                table: "Conversations",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastContextMetadataJson",
                table: "Conversations");
        }
    }
}

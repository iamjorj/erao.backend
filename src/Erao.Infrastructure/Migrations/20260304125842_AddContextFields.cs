using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erao.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddContextFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContextSummary",
                table: "Conversations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomInstructions",
                table: "Conversations",
                type: "text",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SummarizedMessageCount",
                table: "Conversations",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContextSummary",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "CustomInstructions",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "SummarizedMessageCount",
                table: "Conversations");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erao.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationSourceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Conversations_UserId",
                table: "Conversations");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_UserId_AppConnectorId",
                table: "Conversations",
                columns: new[] { "UserId", "AppConnectorId" });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_UserId_DatabaseConnectionId",
                table: "Conversations",
                columns: new[] { "UserId", "DatabaseConnectionId" });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_UserId_FileDocumentId",
                table: "Conversations",
                columns: new[] { "UserId", "FileDocumentId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Conversations_UserId_AppConnectorId",
                table: "Conversations");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_UserId_DatabaseConnectionId",
                table: "Conversations");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_UserId_FileDocumentId",
                table: "Conversations");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_UserId",
                table: "Conversations",
                column: "UserId");
        }
    }
}

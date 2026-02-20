using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erao.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAppConnectors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AppConnectorId",
                table: "Conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AppConnectors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ConnectorType = table.Column<int>(type: "integer", nullable: false),
                    EncryptedCredentials = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SchemaContext = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppConnectors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppConnectors_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_AppConnectorId",
                table: "Conversations",
                column: "AppConnectorId");

            migrationBuilder.CreateIndex(
                name: "IX_AppConnectors_UserId",
                table: "AppConnectors",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Conversations_AppConnectors_AppConnectorId",
                table: "Conversations",
                column: "AppConnectorId",
                principalTable: "AppConnectors",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Conversations_AppConnectors_AppConnectorId",
                table: "Conversations");

            migrationBuilder.DropTable(
                name: "AppConnectors");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_AppConnectorId",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "AppConnectorId",
                table: "Conversations");
        }
    }
}

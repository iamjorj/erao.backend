using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erao.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFileDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "FileDocumentId",
                table: "Conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FileDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    OriginalFileName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    FileType = table.Column<int>(type: "integer", nullable: false),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StoragePath = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ParsedContent = table.Column<string>(type: "text", nullable: true),
                    SchemaInfo = table.Column<string>(type: "text", nullable: true),
                    RowCount = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FileDocuments_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_FileDocumentId",
                table: "Conversations",
                column: "FileDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_FileDocuments_UserId",
                table: "FileDocuments",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Conversations_FileDocuments_FileDocumentId",
                table: "Conversations",
                column: "FileDocumentId",
                principalTable: "FileDocuments",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Conversations_FileDocuments_FileDocumentId",
                table: "Conversations");

            migrationBuilder.DropTable(
                name: "FileDocuments");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_FileDocumentId",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "FileDocumentId",
                table: "Conversations");
        }
    }
}

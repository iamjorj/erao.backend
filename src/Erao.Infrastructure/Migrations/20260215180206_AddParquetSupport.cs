using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erao.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddParquetSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ParquetStoragePath",
                table: "FileDocuments",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SampleDataJson",
                table: "FileDocuments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TotalRowCount",
                table: "FileDocuments",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UsesParquet",
                table: "FileDocuments",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ParquetStoragePath",
                table: "FileDocuments");

            migrationBuilder.DropColumn(
                name: "SampleDataJson",
                table: "FileDocuments");

            migrationBuilder.DropColumn(
                name: "TotalRowCount",
                table: "FileDocuments");

            migrationBuilder.DropColumn(
                name: "UsesParquet",
                table: "FileDocuments");
        }
    }
}

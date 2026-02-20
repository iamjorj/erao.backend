using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erao.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectorSyncFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ParquetStoragePaths",
                table: "AppConnectors",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SampleDataJson",
                table: "AppConnectors",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SchemaInfo",
                table: "AppConnectors",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SyncErrorMessage",
                table: "AppConnectors",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SyncStatus",
                table: "AppConnectors",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "TableRowCounts",
                table: "AppConnectors",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ParquetStoragePaths",
                table: "AppConnectors");

            migrationBuilder.DropColumn(
                name: "SampleDataJson",
                table: "AppConnectors");

            migrationBuilder.DropColumn(
                name: "SchemaInfo",
                table: "AppConnectors");

            migrationBuilder.DropColumn(
                name: "SyncErrorMessage",
                table: "AppConnectors");

            migrationBuilder.DropColumn(
                name: "SyncStatus",
                table: "AppConnectors");

            migrationBuilder.DropColumn(
                name: "TableRowCounts",
                table: "AppConnectors");
        }
    }
}

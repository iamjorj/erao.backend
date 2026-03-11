using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erao.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAnalyticsModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AnalyticsDatasets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    MetadataJson = table.Column<string>(type: "text", nullable: true),
                    RecordCount = table.Column<int>(type: "integer", nullable: false),
                    LastIngestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalyticsDatasets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnalyticsDatasets_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AnalyticsMetricDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DatasetName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    MetricType = table.Column<int>(type: "integer", nullable: false),
                    MeasureField = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalyticsMetricDefinitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnalyticsMetricDefinitions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AnalyticsRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DatasetId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Dimensions = table.Column<string>(type: "jsonb", nullable: false),
                    Measures = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalyticsRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnalyticsRecords_AnalyticsDatasets_DatasetId",
                        column: x => x.DatasetId,
                        principalTable: "AnalyticsDatasets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsDatasets_UserId_Name",
                table: "AnalyticsDatasets",
                columns: new[] { "UserId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsMetricDefinitions_UserId_DatasetName_Name",
                table: "AnalyticsMetricDefinitions",
                columns: new[] { "UserId", "DatasetName", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsRecords_DatasetId",
                table: "AnalyticsRecords",
                column: "DatasetId");

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsRecords_UserId_DatasetId_OccurredAt",
                table: "AnalyticsRecords",
                columns: new[] { "UserId", "DatasetId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsRecords_UserId_OccurredAt",
                table: "AnalyticsRecords",
                columns: new[] { "UserId", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnalyticsMetricDefinitions");

            migrationBuilder.DropTable(
                name: "AnalyticsRecords");

            migrationBuilder.DropTable(
                name: "AnalyticsDatasets");
        }
    }
}

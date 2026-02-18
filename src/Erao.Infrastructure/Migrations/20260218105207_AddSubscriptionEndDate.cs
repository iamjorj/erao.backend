using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erao.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionEndDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "SubscriptionEndDate",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SubscriptionEndDate",
                table: "Users");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Efrsb.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackedCompanyMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FirstMessageDate",
                table: "TrackedCompanies",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastMessageDate",
                table: "TrackedCompanies",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastMetadataSyncAtUtc",
                table: "TrackedCompanies",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LoadedMessages",
                table: "TrackedCompanies",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TotalMessages",
                table: "TrackedCompanies",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FirstMessageDate",
                table: "TrackedCompanies");

            migrationBuilder.DropColumn(
                name: "LastMessageDate",
                table: "TrackedCompanies");

            migrationBuilder.DropColumn(
                name: "LastMetadataSyncAtUtc",
                table: "TrackedCompanies");

            migrationBuilder.DropColumn(
                name: "LoadedMessages",
                table: "TrackedCompanies");

            migrationBuilder.DropColumn(
                name: "TotalMessages",
                table: "TrackedCompanies");
        }
    }
}

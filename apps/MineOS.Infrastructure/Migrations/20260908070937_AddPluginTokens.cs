using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MineOS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPluginTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PluginEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ServerName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PlayerUuid = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    PlayerName = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Data = table.Column<string>(type: "TEXT", nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    TokenId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PluginTokens",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TokenPrefix = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ServerName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Scopes = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    AllowProxyBackends = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Revoked = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginTokens", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PluginEvents_ServerName_OccurredAt",
                table: "PluginEvents",
                columns: new[] { "ServerName", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PluginEvents_TokenId",
                table: "PluginEvents",
                column: "TokenId");

            migrationBuilder.CreateIndex(
                name: "IX_PluginTokens_ServerName",
                table: "PluginTokens",
                column: "ServerName");

            migrationBuilder.CreateIndex(
                name: "IX_PluginTokens_TokenHash",
                table: "PluginTokens",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PluginEvents");

            migrationBuilder.DropTable(
                name: "PluginTokens");
        }
    }
}

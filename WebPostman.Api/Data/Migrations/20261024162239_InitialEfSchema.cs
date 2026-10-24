using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebPostman.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialEfSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // This migration is shared by SQLite and MySQL. Explicit provider types
            // avoid carrying SQLite TEXT/INTEGER definitions into MySQL indexes.
            var mysql = ActiveProvider == "Pomelo.EntityFrameworkCore.MySql";
            var collation = mysql ? "utf8mb4_0900_bin" : null;
            migrationBuilder.CreateTable(
                name: "wp_data_imports",
                columns: table => new
                {
                    Id = table.Column<string>(type: mysql ? "varchar(100)" : "TEXT", maxLength: 100, nullable: false, collation: collation)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wp_data_imports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "wp_users",
                columns: table => new
                {
                    Id = table.Column<string>(type: mysql ? "varchar(32)" : "TEXT", maxLength: 32, nullable: false, collation: collation),
                    Email = table.Column<string>(type: mysql ? "varchar(254)" : "TEXT", maxLength: 254, nullable: false, collation: collation),
                    PasswordHash = table.Column<string>(type: mysql ? "varchar(512)" : "TEXT", maxLength: 512, nullable: false, collation: collation),
                    ShareRevision = table.Column<long>(type: mysql ? "bigint" : "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wp_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "wp_collections",
                columns: table => new
                {
                    UserId = table.Column<string>(type: mysql ? "varchar(32)" : "TEXT", maxLength: 32, nullable: false, collation: collation),
                    Id = table.Column<string>(type: mysql ? "varchar(200)" : "TEXT", maxLength: 200, nullable: false, collation: collation),
                    Json = table.Column<string>(type: mysql ? "longtext" : "TEXT", nullable: false, collation: collation),
                    Position = table.Column<int>(type: mysql ? "int" : "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wp_collections", x => new { x.UserId, x.Id });
                    table.ForeignKey(
                        name: "FK_wp_collections_wp_users_UserId",
                        column: x => x.UserId,
                        principalTable: "wp_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "wp_environments",
                columns: table => new
                {
                    UserId = table.Column<string>(type: mysql ? "varchar(32)" : "TEXT", maxLength: 32, nullable: false, collation: collation),
                    Id = table.Column<string>(type: mysql ? "varchar(200)" : "TEXT", maxLength: 200, nullable: false, collation: collation),
                    Json = table.Column<string>(type: mysql ? "longtext" : "TEXT", nullable: false, collation: collation),
                    Position = table.Column<int>(type: mysql ? "int" : "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wp_environments", x => new { x.UserId, x.Id });
                    table.ForeignKey(
                        name: "FK_wp_environments_wp_users_UserId",
                        column: x => x.UserId,
                        principalTable: "wp_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "wp_history",
                columns: table => new
                {
                    UserId = table.Column<string>(type: mysql ? "varchar(32)" : "TEXT", maxLength: 32, nullable: false, collation: collation),
                    Id = table.Column<string>(type: mysql ? "varchar(200)" : "TEXT", maxLength: 200, nullable: false, collation: collation),
                    At = table.Column<long>(type: mysql ? "bigint" : "INTEGER", nullable: false),
                    Json = table.Column<string>(type: mysql ? "longtext" : "TEXT", nullable: false, collation: collation)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wp_history", x => new { x.UserId, x.Id });
                    table.ForeignKey(
                        name: "FK_wp_history_wp_users_UserId",
                        column: x => x.UserId,
                        principalTable: "wp_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "wp_sessions",
                columns: table => new
                {
                    TokenHash = table.Column<string>(type: mysql ? "varchar(64)" : "TEXT", maxLength: 64, nullable: false, collation: collation),
                    UserId = table.Column<string>(type: mysql ? "varchar(32)" : "TEXT", maxLength: 32, nullable: false, collation: collation),
                    Expires = table.Column<long>(type: mysql ? "bigint" : "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wp_sessions", x => x.TokenHash);
                    table.ForeignKey(
                        name: "FK_wp_sessions_wp_users_UserId",
                        column: x => x.UserId,
                        principalTable: "wp_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "wp_shares",
                columns: table => new
                {
                    Id = table.Column<string>(type: mysql ? "varchar(32)" : "TEXT", maxLength: 32, nullable: false, collation: collation),
                    OwnerId = table.Column<string>(type: mysql ? "varchar(32)" : "TEXT", maxLength: 32, nullable: false, collation: collation),
                    Kind = table.Column<string>(type: mysql ? "varchar(16)" : "TEXT", maxLength: 16, nullable: false, collation: collation),
                    Title = table.Column<string>(type: mysql ? "varchar(100)" : "TEXT", maxLength: 100, nullable: false, collation: collation),
                    Description = table.Column<string>(type: mysql ? "varchar(1000)" : "TEXT", maxLength: 1000, nullable: false, collation: collation),
                    CreatedAt = table.Column<long>(type: mysql ? "bigint" : "INTEGER", nullable: false),
                    Json = table.Column<string>(type: mysql ? "longtext" : "TEXT", nullable: false, collation: collation)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wp_shares", x => x.Id);
                    table.ForeignKey(
                        name: "FK_wp_shares_wp_users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "wp_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "wp_workspaces",
                columns: table => new
                {
                    UserId = table.Column<string>(type: mysql ? "varchar(32)" : "TEXT", maxLength: 32, nullable: false, collation: collation),
                    Revision = table.Column<long>(type: mysql ? "bigint" : "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wp_workspaces", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_wp_workspaces_wp_users_UserId",
                        column: x => x.UserId,
                        principalTable: "wp_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_wp_collections_UserId_Position",
                table: "wp_collections",
                columns: new[] { "UserId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_wp_environments_UserId_Position",
                table: "wp_environments",
                columns: new[] { "UserId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_wp_history_UserId_At_Id",
                table: "wp_history",
                columns: new[] { "UserId", "At", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_wp_sessions_Expires",
                table: "wp_sessions",
                column: "Expires");

            migrationBuilder.CreateIndex(
                name: "IX_wp_sessions_UserId",
                table: "wp_sessions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_wp_shares_CreatedAt_Id",
                table: "wp_shares",
                columns: new[] { "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_wp_shares_OwnerId",
                table: "wp_shares",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_wp_users_Email",
                table: "wp_users",
                column: "Email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wp_collections");

            migrationBuilder.DropTable(
                name: "wp_data_imports");

            migrationBuilder.DropTable(
                name: "wp_environments");

            migrationBuilder.DropTable(
                name: "wp_history");

            migrationBuilder.DropTable(
                name: "wp_sessions");

            migrationBuilder.DropTable(
                name: "wp_shares");

            migrationBuilder.DropTable(
                name: "wp_workspaces");

            migrationBuilder.DropTable(
                name: "wp_users");
        }
    }
}

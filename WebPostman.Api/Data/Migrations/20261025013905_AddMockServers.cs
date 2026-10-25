using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebPostman.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMockServers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var mysql = ActiveProvider == "Pomelo.EntityFrameworkCore.MySql";
            var collation = mysql ? "utf8mb4_0900_bin" : null;
            migrationBuilder.CreateTable(
                name: "wp_mock_servers",
                columns: table => new
                {
                    Id = table.Column<string>(type: mysql ? "varchar(32)" : "TEXT", maxLength: 32, nullable: false, collation: collation),
                    UserId = table.Column<string>(type: mysql ? "varchar(32)" : "TEXT", maxLength: 32, nullable: false, collation: collation),
                    Name = table.Column<string>(type: mysql ? "varchar(100)" : "TEXT", maxLength: 100, nullable: false, collation: collation),
                    Port = table.Column<int>(type: mysql ? "int" : "INTEGER", nullable: false),
                    CorsJson = table.Column<string>(type: mysql ? "longtext" : "TEXT", nullable: false, collation: collation),
                    Revision = table.Column<long>(type: mysql ? "bigint" : "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wp_mock_servers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_wp_mock_servers_wp_users_UserId",
                        column: x => x.UserId,
                        principalTable: "wp_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "wp_mock_rules",
                columns: table => new
                {
                    Id = table.Column<string>(type: mysql ? "varchar(32)" : "TEXT", maxLength: 32, nullable: false, collation: collation),
                    ServerId = table.Column<string>(type: mysql ? "varchar(32)" : "TEXT", maxLength: 32, nullable: false, collation: collation),
                    Name = table.Column<string>(type: mysql ? "varchar(100)" : "TEXT", maxLength: 100, nullable: false, collation: collation),
                    Method = table.Column<string>(type: mysql ? "varchar(16)" : "TEXT", maxLength: 16, nullable: false, collation: collation),
                    Path = table.Column<string>(type: mysql ? "varchar(2048)" : "TEXT", maxLength: 2048, nullable: false, collation: collation),
                    Query = table.Column<string>(type: mysql ? "varchar(8192)" : "TEXT", maxLength: 8192, nullable: false, collation: collation),
                    RouteKey = table.Column<string>(type: mysql ? "varchar(64)" : "TEXT", maxLength: 64, nullable: false, collation: collation),
                    Enabled = table.Column<bool>(type: mysql ? "tinyint(1)" : "INTEGER", nullable: false),
                    ResponseJson = table.Column<string>(type: mysql ? "longtext" : "TEXT", nullable: false, collation: collation),
                    OriginalJson = table.Column<string>(type: mysql ? "longtext" : "TEXT", nullable: false, collation: collation),
                    SourceUrl = table.Column<string>(type: mysql ? "longtext" : "TEXT", nullable: false, collation: collation),
                    Revision = table.Column<long>(type: mysql ? "bigint" : "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wp_mock_rules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_wp_mock_rules_wp_mock_servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "wp_mock_servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_wp_mock_rules_ServerId_RouteKey",
                table: "wp_mock_rules",
                columns: new[] { "ServerId", "RouteKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wp_mock_servers_UserId",
                table: "wp_mock_servers",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wp_mock_rules");

            migrationBuilder.DropTable(
                name: "wp_mock_servers");
        }
    }
}

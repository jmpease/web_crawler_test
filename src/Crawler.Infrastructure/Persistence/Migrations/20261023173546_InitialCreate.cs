using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Crawler.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    url = table.Column<string>(type: "text", nullable: false),
                    start_host = table.Column<string>(type: "text", nullable: false),
                    max_depth = table.Column<int>(type: "integer", nullable: false),
                    max_pages = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    failure_reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    pages_enqueued = table.Column<int>(type: "integer", nullable: false),
                    pages_processed = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pages",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    url = table.Column<string>(type: "text", nullable: false),
                    url_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    depth = table.Column<int>(type: "integer", nullable: false),
                    parent_page_id = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    final_url = table.Column<string>(type: "text", nullable: true),
                    http_status = table.Column<int>(type: "integer", nullable: true),
                    content_type = table.Column<string>(type: "text", nullable: true),
                    outgoing_links = table.Column<int>(type: "integer", nullable: true),
                    same_domain_links = table.Column<int>(type: "integer", nullable: true),
                    domain_link_ratio = table.Column<double>(type: "double precision", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pages", x => x.id);
                    table.ForeignKey(
                        name: "fk_pages_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_pages_pages_parent_page_id",
                        column: x => x.parent_page_id,
                        principalTable: "pages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "edges",
                columns: table => new
                {
                    from_page_id = table.Column<long>(type: "bigint", nullable: false),
                    to_url_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_url = table.Column<string>(type: "text", nullable: false),
                    is_same_domain = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_edges", x => new { x.from_page_id, x.to_url_hash });
                    table.ForeignKey(
                        name: "fk_edges_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_edges_pages_from_page_id",
                        column: x => x.from_page_id,
                        principalTable: "pages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_edges_job_id",
                table: "edges",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_created_at_id",
                table: "jobs",
                columns: new[] { "created_at", "id" },
                descending: new[] { true, false });

            migrationBuilder.CreateIndex(
                name: "ix_pages_job_id_parent_page_id",
                table: "pages",
                columns: new[] { "job_id", "parent_page_id" });

            migrationBuilder.CreateIndex(
                name: "ix_pages_job_id_url_hash",
                table: "pages",
                columns: new[] { "job_id", "url_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pages_parent_page_id",
                table: "pages",
                column: "parent_page_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "edges");

            migrationBuilder.DropTable(
                name: "pages");

            migrationBuilder.DropTable(
                name: "jobs");
        }
    }
}

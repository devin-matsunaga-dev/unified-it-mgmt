using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using NpgsqlTypes;

#nullable disable

namespace Modules.Helpdesk.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP59_KnowledgeBase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "kb_articles",
                schema: "helpdesk",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence_number = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    body = table.Column<string>(type: "character varying(50000)", maxLength: 50000, nullable: false),
                    keywords = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    problem_id = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    author_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    author_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    updated_by_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    updated_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    published_by_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    published_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    search_vector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: false, computedColumnSql: "setweight(to_tsvector('english', coalesce(title, '')), 'A')\n|| setweight(to_tsvector('english', coalesce(summary, '')), 'B')\n|| setweight(to_tsvector('english', coalesce(keywords, '')), 'B')\n|| setweight(to_tsvector('english', coalesce(body, '')), 'C')", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kb_articles", x => x.id);
                    table.ForeignKey(
                        name: "fk_kb_articles_problems_problem_id",
                        column: x => x.problem_id,
                        principalSchema: "helpdesk",
                        principalTable: "problems",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_kb_articles_ticket_categories_category_id",
                        column: x => x.category_id,
                        principalSchema: "helpdesk",
                        principalTable: "ticket_categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "kb_article_revisions",
                schema: "helpdesk",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    article_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    body = table.Column<string>(type: "character varying(50000)", maxLength: 50000, nullable: false),
                    keywords = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    author_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    author_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kb_article_revisions", x => x.id);
                    table.ForeignKey(
                        name: "fk_kb_article_revisions_kb_articles_article_id",
                        column: x => x.article_id,
                        principalSchema: "helpdesk",
                        principalTable: "kb_articles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ticket_kb_articles",
                schema: "helpdesk",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    article_id = table.Column<Guid>(type: "uuid", nullable: false),
                    linked_by_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    linked_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    linked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket_kb_articles", x => x.id);
                    table.ForeignKey(
                        name: "fk_ticket_kb_articles_kb_articles_article_id",
                        column: x => x.article_id,
                        principalSchema: "helpdesk",
                        principalTable: "kb_articles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ticket_kb_articles_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalSchema: "helpdesk",
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_kb_article_revisions_article_id_version",
                schema: "helpdesk",
                table: "kb_article_revisions",
                columns: new[] { "article_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_kb_articles_category_id",
                schema: "helpdesk",
                table: "kb_articles",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_kb_articles_problem_id",
                schema: "helpdesk",
                table: "kb_articles",
                column: "problem_id");

            migrationBuilder.CreateIndex(
                name: "ix_kb_articles_search_vector",
                schema: "helpdesk",
                table: "kb_articles",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "ix_kb_articles_sequence_number",
                schema: "helpdesk",
                table: "kb_articles",
                column: "sequence_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_kb_articles_status",
                schema: "helpdesk",
                table: "kb_articles",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_kb_articles_article_id",
                schema: "helpdesk",
                table: "ticket_kb_articles",
                column: "article_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_kb_articles_ticket_id_article_id",
                schema: "helpdesk",
                table: "ticket_kb_articles",
                columns: new[] { "ticket_id", "article_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "kb_article_revisions",
                schema: "helpdesk");

            migrationBuilder.DropTable(
                name: "ticket_kb_articles",
                schema: "helpdesk");

            migrationBuilder.DropTable(
                name: "kb_articles",
                schema: "helpdesk");
        }
    }
}

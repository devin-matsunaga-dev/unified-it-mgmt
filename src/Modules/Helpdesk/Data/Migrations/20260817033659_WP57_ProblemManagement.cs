using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Modules.Helpdesk.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP57_ProblemManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "problems",
                schema: "helpdesk",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence_number = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    priority = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ci_id = table.Column<Guid>(type: "uuid", nullable: true),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    root_cause = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    workaround = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    resolution = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    assigned_technician_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    opened_by_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    opened_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    known_error_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_problems", x => x.id);
                    table.ForeignKey(
                        name: "fk_problems_ticket_categories_category_id",
                        column: x => x.category_id,
                        principalSchema: "helpdesk",
                        principalTable: "ticket_categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "problem_incidents",
                schema: "helpdesk",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    problem_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    linked_by_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    linked_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    linked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_problem_incidents", x => x.id);
                    table.ForeignKey(
                        name: "fk_problem_incidents_problems_problem_id",
                        column: x => x.problem_id,
                        principalSchema: "helpdesk",
                        principalTable: "problems",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_problem_incidents_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalSchema: "helpdesk",
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "problem_suggestions",
                schema: "helpdesk",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ci_id = table.Column<Guid>(type: "uuid", nullable: true),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    incident_count = table.Column<int>(type: "integer", nullable: false),
                    window_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    window_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    detected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_problem_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_by_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    resolved_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dismiss_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_problem_suggestions", x => x.id);
                    table.ForeignKey(
                        name: "fk_problem_suggestions_problems_created_problem_id",
                        column: x => x.created_problem_id,
                        principalSchema: "helpdesk",
                        principalTable: "problems",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_problem_suggestions_ticket_categories_category_id",
                        column: x => x.category_id,
                        principalSchema: "helpdesk",
                        principalTable: "ticket_categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_problem_incidents_problem_id",
                schema: "helpdesk",
                table: "problem_incidents",
                column: "problem_id");

            migrationBuilder.CreateIndex(
                name: "ix_problem_incidents_ticket_id",
                schema: "helpdesk",
                table: "problem_incidents",
                column: "ticket_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_problem_suggestions_created_problem_id",
                schema: "helpdesk",
                table: "problem_suggestions",
                column: "created_problem_id");

            migrationBuilder.CreateIndex(
                name: "ix_problem_suggestions_open_category",
                schema: "helpdesk",
                table: "problem_suggestions",
                column: "category_id",
                unique: true,
                filter: "status = 'Open' AND category_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_problem_suggestions_open_ci",
                schema: "helpdesk",
                table: "problem_suggestions",
                column: "ci_id",
                unique: true,
                filter: "status = 'Open' AND ci_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_problem_suggestions_status_detected_at",
                schema: "helpdesk",
                table: "problem_suggestions",
                columns: new[] { "status", "detected_at" });

            migrationBuilder.CreateIndex(
                name: "ix_problems_category_id",
                schema: "helpdesk",
                table: "problems",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_problems_ci_id",
                schema: "helpdesk",
                table: "problems",
                column: "ci_id");

            migrationBuilder.CreateIndex(
                name: "ix_problems_sequence_number",
                schema: "helpdesk",
                table: "problems",
                column: "sequence_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_problems_status",
                schema: "helpdesk",
                table: "problems",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "problem_incidents",
                schema: "helpdesk");

            migrationBuilder.DropTable(
                name: "problem_suggestions",
                schema: "helpdesk");

            migrationBuilder.DropTable(
                name: "problems",
                schema: "helpdesk");
        }
    }
}

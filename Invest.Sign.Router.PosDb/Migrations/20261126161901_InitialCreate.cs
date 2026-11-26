using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Invest.Sign.Router.PosDb.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "sign_stage_id_seq");

            migrationBuilder.CreateTable(
                name: "sign_document",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    external_doc_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    doc_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    attributes = table.Column<string>(type: "text", nullable: true),
                    content_hash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    source_created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    route_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    route_version = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status_reason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    current_stage_id = table.Column<long>(type: "bigint", nullable: true),
                    stage_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    stage_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    request_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    stage_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    stage_deadline_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    stage_finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    stage_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    stage_data = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sign_document", x => x.id);
                    table.CheckConstraint("ck_sign_document_stage", "(current_stage_id IS NULL) = (stage_key IS NULL) AND (current_stage_id IS NULL) = (stage_state IS NULL)");
                    table.CheckConstraint("ck_sign_document_stage_state", "stage_state IS NULL OR stage_state IN ('PENDING', 'REQUESTED', 'DONE', 'REJECTED', 'EXPIRED', 'FAILED')");
                    table.CheckConstraint("ck_sign_document_status", "status IN ('ACTIVE', 'COMPLETED', 'REJECTED', 'EXPIRED', 'CANCELLED', 'FAILED')");
                });

            migrationBuilder.CreateTable(
                name: "sign_document_status_history",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    document_id = table.Column<long>(type: "bigint", nullable: false),
                    event_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status_reason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    stage_id = table.Column<long>(type: "bigint", nullable: true),
                    stage_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    stage_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: true),
                    actor = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sign_document_status_history", x => x.id);
                    table.ForeignKey(
                        name: "fk_sign_document_status_history_document",
                        column: x => x.document_id,
                        principalTable: "sign_document",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sign_signature",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    document_id = table.Column<long>(type: "bigint", nullable: false),
                    stage_id = table.Column<long>(type: "bigint", nullable: false),
                    stage_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    source_ref = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: false),
                    sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    stored_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sign_signature", x => x.id);
                    table.ForeignKey(
                        name: "fk_sign_signature_document",
                        column: x => x.document_id,
                        principalTable: "sign_document",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sign_document_source_created_at",
                table: "sign_document",
                column: "source_created_at");

            migrationBuilder.CreateIndex(
                name: "ix_sign_document_status_next_attempt",
                table: "sign_document",
                columns: new[] { "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ux_sign_document_current_stage_id",
                table: "sign_document",
                column: "current_stage_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_sign_document_external_doc_id",
                table: "sign_document",
                column: "external_doc_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_sign_document_request_id",
                table: "sign_document",
                column: "request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sign_document_status_history_document",
                table: "sign_document_status_history",
                columns: new[] { "document_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_sign_signature_document",
                table: "sign_signature",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "ux_sign_signature_stage",
                table: "sign_signature",
                column: "stage_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sign_document_status_history");

            migrationBuilder.DropTable(
                name: "sign_signature");

            migrationBuilder.DropTable(
                name: "sign_document");

            migrationBuilder.DropSequence(
                name: "sign_stage_id_seq");
        }
    }
}

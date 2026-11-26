using Microsoft.EntityFrameworkCore;

namespace Invest.Sign.Router.PosDb;

/// <summary>
/// База состояния процесса подписания (code first). Схема меняется только миграциями из папки <c>Migrations</c>;
/// сервис при старте её не меняет.
/// </summary>
public sealed class SignDbContext(DbContextOptions<SignDbContext> options) : DbContext(options)
{
    /// <summary>Последовательность идентификаторов этапов: этап живёт в строке документа, но его id уникален глобально.</summary>
    public const string StageIdSequence = "sign_stage_id_seq";

    /// <summary>Время базы. <c>clock_timestamp()</c>, а не <c>now()</c>: последнее — время начала транзакции.</summary>
    internal const string DbNow = "clock_timestamp()";

    public DbSet<DocumentEntity> Documents => Set<DocumentEntity>();

    public DbSet<DocumentStatusHistoryEntity> StatusHistory => Set<DocumentStatusHistoryEntity>();

    public DbSet<SignatureEntity> Signatures => Set<SignatureEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasSequence<long>(StageIdSequence);

        modelBuilder.Entity<DocumentEntity>(e =>
        {
            e.ToTable("sign_document", t =>
            {
                t.HasCheckConstraint("ck_sign_document_status",
                    "status IN ('ACTIVE', 'COMPLETED', 'REJECTED', 'EXPIRED', 'CANCELLED', 'FAILED')");
                t.HasCheckConstraint("ck_sign_document_stage_state",
                    "stage_state IS NULL OR stage_state IN ('PENDING', 'REQUESTED', 'DONE', 'REJECTED', 'EXPIRED', 'FAILED')");
                t.HasCheckConstraint("ck_sign_document_stage",
                    "(current_stage_id IS NULL) = (stage_key IS NULL) AND (current_stage_id IS NULL) = (stage_state IS NULL)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            e.Property(x => x.ExternalDocId).HasColumnName("external_doc_id").HasMaxLength(200);
            e.Property(x => x.DocType).HasColumnName("doc_type").HasMaxLength(100);
            e.Property(x => x.Attributes).HasColumnName("attributes");
            e.Property(x => x.ContentHash).HasColumnName("content_hash").HasMaxLength(200);
            e.Property(x => x.SourceCreatedAt).HasColumnName("source_created_at");
            e.Property(x => x.RouteCode).HasColumnName("route_code").HasMaxLength(100);
            e.Property(x => x.RouteVersion).HasColumnName("route_version");
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(20);
            e.Property(x => x.StatusReason).HasColumnName("status_reason").HasMaxLength(100);
            e.Property(x => x.Attempts).HasColumnName("attempts");
            e.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at");
            e.Property(x => x.LockedBy).HasColumnName("locked_by").HasMaxLength(100);
            e.Property(x => x.LockedUntil).HasColumnName("locked_until");
            e.Property(x => x.CurrentStageId).HasColumnName("current_stage_id");
            e.Property(x => x.StageKey).HasColumnName("stage_key").HasMaxLength(100);
            e.Property(x => x.StageState).HasColumnName("stage_state").HasMaxLength(20);
            e.Property(x => x.RequestId).HasColumnName("request_id").HasMaxLength(64);
            e.Property(x => x.StageRequestedAt).HasColumnName("stage_requested_at");
            e.Property(x => x.StageDeadlineAt).HasColumnName("stage_deadline_at");
            e.Property(x => x.StageFinishedAt).HasColumnName("stage_finished_at");
            e.Property(x => x.StageReason).HasColumnName("stage_reason").HasMaxLength(2000);
            e.Property(x => x.StageData).HasColumnName("stage_data");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql(DbNow);
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql(DbNow);

            e.HasIndex(x => x.ExternalDocId).IsUnique().HasDatabaseName("ux_sign_document_external_doc_id");
            e.HasIndex(x => x.CurrentStageId).IsUnique().HasDatabaseName("ux_sign_document_current_stage_id");
            e.HasIndex(x => x.RequestId).IsUnique().HasDatabaseName("ux_sign_document_request_id");
            // Выбор кандидатов: status = 'ACTIVE' AND next_attempt_at <= now.
            e.HasIndex(x => new { x.Status, x.NextAttemptAt }).HasDatabaseName("ix_sign_document_status_next_attempt");
            // Водяной знак обнаружения: max(source_created_at).
            e.HasIndex(x => x.SourceCreatedAt).HasDatabaseName("ix_sign_document_source_created_at");
        });

        modelBuilder.Entity<DocumentStatusHistoryEntity>(e =>
        {
            e.ToTable("sign_document_status_history");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            e.Property(x => x.DocumentId).HasColumnName("document_id");
            e.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(50);
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(20);
            e.Property(x => x.StatusReason).HasColumnName("status_reason").HasMaxLength(100);
            e.Property(x => x.StageId).HasColumnName("stage_id");
            e.Property(x => x.StageKey).HasColumnName("stage_key").HasMaxLength(100);
            e.Property(x => x.StageState).HasColumnName("stage_state").HasMaxLength(20);
            e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
            e.Property(x => x.Actor).HasColumnName("actor").HasMaxLength(100);
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql(DbNow);

            e.HasOne<DocumentEntity>().WithMany().HasForeignKey(x => x.DocumentId)
                .HasConstraintName("fk_sign_document_status_history_document").OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.DocumentId, x.Id }).HasDatabaseName("ix_sign_document_status_history_document");
        });

        modelBuilder.Entity<SignatureEntity>(e =>
        {
            e.ToTable("sign_signature");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            e.Property(x => x.DocumentId).HasColumnName("document_id");
            e.Property(x => x.StageId).HasColumnName("stage_id");
            e.Property(x => x.StageKey).HasColumnName("stage_key").HasMaxLength(100);
            e.Property(x => x.SourceRef).HasColumnName("source_ref").HasMaxLength(1000);
            e.Property(x => x.Content).HasColumnName("content");
            e.Property(x => x.Sha256).HasColumnName("sha256").HasMaxLength(64).IsFixedLength();
            e.Property(x => x.SizeBytes).HasColumnName("size_bytes");
            e.Property(x => x.StoredAt).HasColumnName("stored_at").HasDefaultValueSql(DbNow);

            e.HasOne<DocumentEntity>().WithMany().HasForeignKey(x => x.DocumentId)
                .HasConstraintName("fk_sign_signature_document").OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.DocumentId).HasDatabaseName("ix_sign_signature_document");
            // Одна подпись на этап: повтор шага после сбоя не создаёт дубликат.
            e.HasIndex(x => x.StageId).IsUnique().HasDatabaseName("ux_sign_signature_stage");
        });
    }
}

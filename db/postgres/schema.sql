CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261126161901_InitialCreate') THEN
    CREATE SEQUENCE sign_stage_id_seq START WITH 1 INCREMENT BY 1 NO CYCLE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261126161901_InitialCreate') THEN
    CREATE TABLE sign_document (
        id bigint GENERATED ALWAYS AS IDENTITY,
        external_doc_id character varying(200) NOT NULL,
        doc_type character varying(100) NOT NULL,
        attributes text,
        content_hash character varying(200),
        source_created_at timestamp with time zone,
        route_code character varying(100),
        route_version integer,
        status character varying(20) NOT NULL,
        status_reason character varying(100),
        attempts integer NOT NULL,
        next_attempt_at timestamp with time zone NOT NULL,
        locked_by character varying(100),
        locked_until timestamp with time zone,
        current_stage_id bigint,
        stage_key character varying(100),
        stage_state character varying(20),
        request_id character varying(64),
        stage_requested_at timestamp with time zone,
        stage_deadline_at timestamp with time zone,
        stage_finished_at timestamp with time zone,
        stage_reason character varying(2000),
        stage_data text,
        created_at timestamp with time zone NOT NULL DEFAULT (clock_timestamp()),
        updated_at timestamp with time zone NOT NULL DEFAULT (clock_timestamp()),
        CONSTRAINT "PK_sign_document" PRIMARY KEY (id),
        CONSTRAINT ck_sign_document_stage CHECK ((current_stage_id IS NULL) = (stage_key IS NULL) AND (current_stage_id IS NULL) = (stage_state IS NULL)),
        CONSTRAINT ck_sign_document_stage_state CHECK (stage_state IS NULL OR stage_state IN ('PENDING', 'REQUESTED', 'DONE', 'REJECTED', 'EXPIRED', 'FAILED')),
        CONSTRAINT ck_sign_document_status CHECK (status IN ('ACTIVE', 'COMPLETED', 'REJECTED', 'EXPIRED', 'CANCELLED', 'FAILED'))
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261126161901_InitialCreate') THEN
    CREATE TABLE sign_document_status_history (
        id bigint GENERATED ALWAYS AS IDENTITY,
        document_id bigint NOT NULL,
        event_type character varying(50) NOT NULL,
        status character varying(20) NOT NULL,
        status_reason character varying(100),
        stage_id bigint,
        stage_key character varying(100),
        stage_state character varying(20),
        payload jsonb,
        actor character varying(100) NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (clock_timestamp()),
        CONSTRAINT "PK_sign_document_status_history" PRIMARY KEY (id),
        CONSTRAINT fk_sign_document_status_history_document FOREIGN KEY (document_id) REFERENCES sign_document (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261126161901_InitialCreate') THEN
    CREATE TABLE sign_signature (
        id bigint GENERATED ALWAYS AS IDENTITY,
        document_id bigint NOT NULL,
        stage_id bigint NOT NULL,
        stage_key character varying(100) NOT NULL,
        source_ref character varying(1000) NOT NULL,
        content bytea NOT NULL,
        sha256 character(64) NOT NULL,
        size_bytes bigint NOT NULL,
        stored_at timestamp with time zone NOT NULL DEFAULT (clock_timestamp()),
        CONSTRAINT "PK_sign_signature" PRIMARY KEY (id),
        CONSTRAINT fk_sign_signature_document FOREIGN KEY (document_id) REFERENCES sign_document (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261126161901_InitialCreate') THEN
    CREATE INDEX ix_sign_document_source_created_at ON sign_document (source_created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261126161901_InitialCreate') THEN
    CREATE INDEX ix_sign_document_status_next_attempt ON sign_document (status, next_attempt_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261126161901_InitialCreate') THEN
    CREATE UNIQUE INDEX ux_sign_document_current_stage_id ON sign_document (current_stage_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261126161901_InitialCreate') THEN
    CREATE UNIQUE INDEX ux_sign_document_external_doc_id ON sign_document (external_doc_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261126161901_InitialCreate') THEN
    CREATE UNIQUE INDEX ux_sign_document_request_id ON sign_document (request_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261126161901_InitialCreate') THEN
    CREATE INDEX ix_sign_document_status_history_document ON sign_document_status_history (document_id, id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261126161901_InitialCreate') THEN
    CREATE INDEX ix_sign_signature_document ON sign_signature (document_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261126161901_InitialCreate') THEN
    CREATE UNIQUE INDEX ux_sign_signature_stage ON sign_signature (stage_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261126161901_InitialCreate') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261126161901_InitialCreate', '10.0.10');
    END IF;
END $EF$;
COMMIT;


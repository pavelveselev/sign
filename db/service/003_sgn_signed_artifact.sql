-- SGN_SIGNED_ARTIFACT: подписанный файл, скопированный с общего ресурса. Хранится несколько лет.

CREATE TABLE sgn_signed_artifact (
    id                 NUMBER(19)               GENERATED ALWAYS AS IDENTITY,
    stage_instance_id  NUMBER(19)               NOT NULL,
    content            BLOB                     NOT NULL,
    sha256             VARCHAR2(64 CHAR)        NOT NULL,
    size_bytes         NUMBER(19)               NOT NULL,
    source_ref         VARCHAR2(1000 CHAR)      NOT NULL,
    stored_at          TIMESTAMP WITH TIME ZONE DEFAULT SYSTIMESTAMP NOT NULL,
    CONSTRAINT sgn_signed_artifact_pk PRIMARY KEY (id),
    -- Один подписанный файл на этап.
    CONSTRAINT sgn_signed_artifact_stage_uk UNIQUE (stage_instance_id),
    CONSTRAINT sgn_signed_artifact_stage_fk
        FOREIGN KEY (stage_instance_id) REFERENCES sgn_stage_instance (id),
    CONSTRAINT sgn_signed_artifact_size_ck CHECK (size_bytes >= 0)
)
LOB (content) STORE AS SECUREFILE;

COMMENT ON TABLE sgn_signed_artifact IS 'Signed file of a stage, copied from the share.';
COMMENT ON COLUMN sgn_signed_artifact.sha256 IS 'SHA-256 of content, lowercase hex.';
COMMENT ON COLUMN sgn_signed_artifact.source_ref IS 'file_ref the file was read from.';

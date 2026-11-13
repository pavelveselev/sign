-- SGN_ROUTE_INSTANCE: one route instance per external document.
-- Run as the service schema owner. Oracle 19c or newer.

CREATE TABLE sgn_route_instance (
    id                NUMBER(19)                  GENERATED ALWAYS AS IDENTITY,
    external_doc_id   VARCHAR2(200 CHAR)          NOT NULL,
    doc_type          VARCHAR2(100 CHAR)          NOT NULL,
    -- Copied from the incoming documents view as is. Deliberately no IS JSON constraint:
    -- a malformed value must not block discovery; the engine fails such a route with CONDITION_ERROR.
    attributes        CLOB,
    content_hash      VARCHAR2(200 CHAR),
    route_code        VARCHAR2(100 CHAR),
    route_version     NUMBER(10),
    status            VARCHAR2(20 CHAR)           NOT NULL,
    status_reason     VARCHAR2(100 CHAR),
    current_stage_id  NUMBER(19),
    attempts          NUMBER(10)        DEFAULT 0 NOT NULL,
    next_attempt_at   TIMESTAMP WITH TIME ZONE    NOT NULL,
    locked_by         VARCHAR2(200 CHAR),
    locked_until      TIMESTAMP WITH TIME ZONE,
    created_at        TIMESTAMP WITH TIME ZONE DEFAULT SYSTIMESTAMP NOT NULL,
    updated_at        TIMESTAMP WITH TIME ZONE DEFAULT SYSTIMESTAMP NOT NULL,
    CONSTRAINT sgn_route_instance_pk PRIMARY KEY (id),
    CONSTRAINT sgn_route_instance_doc_uk UNIQUE (external_doc_id),
    CONSTRAINT sgn_route_instance_status_ck
        CHECK (status IN ('ACTIVE', 'COMPLETED', 'REJECTED', 'EXPIRED', 'CANCELLED', 'FAILED')),
    -- Route code and version are fixed together; only an instance that never resolved a route has neither.
    CONSTRAINT sgn_route_instance_route_ck
        CHECK ((route_code IS NULL AND route_version IS NULL) OR (route_code IS NOT NULL AND route_version IS NOT NULL)),
    CONSTRAINT sgn_route_instance_active_ck
        CHECK (status <> 'ACTIVE' OR route_code IS NOT NULL),
    CONSTRAINT sgn_route_instance_attempts_ck CHECK (attempts >= 0),
    CONSTRAINT sgn_route_instance_lock_ck
        CHECK ((locked_by IS NULL AND locked_until IS NULL) OR (locked_by IS NOT NULL AND locked_until IS NOT NULL))
);

-- Candidate selection: status = 'ACTIVE' AND next_attempt_at <= SYSTIMESTAMP ORDER BY next_attempt_at.
CREATE INDEX sgn_route_instance_due_ix ON sgn_route_instance (status, next_attempt_at);

COMMENT ON TABLE sgn_route_instance IS 'Route instance: one row per document being routed for signing.';
COMMENT ON COLUMN sgn_route_instance.external_doc_id IS 'Document id in the incoming documents view.';
COMMENT ON COLUMN sgn_route_instance.attributes IS 'Document attributes JSON, copied from the incoming documents view.';
COMMENT ON COLUMN sgn_route_instance.content_hash IS 'Hash of the source document, may be empty.';
COMMENT ON COLUMN sgn_route_instance.route_code IS 'Route code fixed at start. Empty for UNKNOWN_DOC_TYPE.';
COMMENT ON COLUMN sgn_route_instance.route_version IS 'Route version fixed at start. Empty for UNKNOWN_DOC_TYPE.';
COMMENT ON COLUMN sgn_route_instance.status IS 'ACTIVE, COMPLETED, REJECTED, EXPIRED, CANCELLED, FAILED.';
COMMENT ON COLUMN sgn_route_instance.status_reason IS 'Reason code for FAILED: UNKNOWN_DOC_TYPE, ROUTE_NOT_FOUND, ARTIFACT_MISMATCH, ATTEMPTS_EXHAUSTED, CONDITION_ERROR, SIGNING_ERROR.';
COMMENT ON COLUMN sgn_route_instance.current_stage_id IS 'Current stage instance, empty before the first stage.';
COMMENT ON COLUMN sgn_route_instance.attempts IS 'Consecutive technical failures. Reset by a successful step.';
COMMENT ON COLUMN sgn_route_instance.next_attempt_at IS 'The instance is not picked up before this moment.';
COMMENT ON COLUMN sgn_route_instance.locked_by IS 'Lease owner (replica id).';
COMMENT ON COLUMN sgn_route_instance.locked_until IS 'Lease expiry. An expired lease may be taken by any replica.';

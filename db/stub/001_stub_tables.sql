-- STUB of the external side (see db/contract/README.md). For development and tests only.
-- Run as the external schema owner. Tables are an implementation detail of the stub:
-- the service never touches them, tests do (INSERT incoming documents, UPDATE request results).

CREATE TABLE stub_incoming_document (
    doc_id        VARCHAR2(200 CHAR)       NOT NULL,
    doc_type      VARCHAR2(100 CHAR)       NOT NULL,
    attributes    CLOB,
    content_hash  VARCHAR2(200 CHAR),
    created_at    TIMESTAMP WITH TIME ZONE DEFAULT SYSTIMESTAMP NOT NULL,
    CONSTRAINT stub_incoming_document_pk PRIMARY KEY (doc_id)
);

CREATE TABLE stub_sign_request (
    request_id     VARCHAR2(64 CHAR)        NOT NULL,
    doc_id         VARCHAR2(200 CHAR)       NOT NULL,
    kind           VARCHAR2(10 CHAR)        NOT NULL,
    signer         VARCHAR2(200 CHAR)       NOT NULL,
    status         VARCHAR2(20 CHAR)        DEFAULT 'PENDING' NOT NULL,
    status_reason  VARCHAR2(2000 CHAR),
    signed_at      TIMESTAMP WITH TIME ZONE,
    file_ref       VARCHAR2(1000 CHAR),
    file_hash      VARCHAR2(64 CHAR),
    file_size      NUMBER(19),
    created_at     TIMESTAMP WITH TIME ZONE DEFAULT SYSTIMESTAMP NOT NULL,
    updated_at     TIMESTAMP WITH TIME ZONE DEFAULT SYSTIMESTAMP NOT NULL,
    CONSTRAINT stub_sign_request_pk PRIMARY KEY (request_id),
    CONSTRAINT stub_sign_request_doc_fk FOREIGN KEY (doc_id) REFERENCES stub_incoming_document (doc_id),
    CONSTRAINT stub_sign_request_kind_ck CHECK (kind IN ('AUTO', 'MANUAL')),
    CONSTRAINT stub_sign_request_status_ck CHECK (status IN ('PENDING', 'SIGNED', 'REJECTED', 'ERROR'))
);

-- Enforces "a final status never changes" so tests cannot set up states the real side never produces.
CREATE OR REPLACE TRIGGER stub_sign_request_final_trg
BEFORE UPDATE OF status ON stub_sign_request
FOR EACH ROW
BEGIN
    IF :OLD.status <> 'PENDING' AND :NEW.status <> :OLD.status THEN
        RAISE_APPLICATION_ERROR(-20010,
            'Sign request ' || :OLD.request_id || ' is already ' || :OLD.status || ', a final status never changes.');
    END IF;
    :NEW.updated_at := SYSTIMESTAMP;
END;
/

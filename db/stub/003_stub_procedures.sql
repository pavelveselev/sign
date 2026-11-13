-- Contract procedures over the stub tables. No COMMIT, no autonomous transactions:
-- they run inside the caller's transaction (db/contract/README.md, "Transactions").

CREATE OR REPLACE PROCEDURE create_sign_request (
    p_request_id IN VARCHAR2,
    p_doc_id     IN VARCHAR2,
    p_kind       IN VARCHAR2,
    p_signer     IN VARCHAR2
) AS
BEGIN
    IF p_request_id IS NULL OR p_doc_id IS NULL OR p_signer IS NULL THEN
        RAISE_APPLICATION_ERROR(-20001, 'p_request_id, p_doc_id and p_signer are required.');
    END IF;

    IF p_kind IS NULL OR p_kind NOT IN ('AUTO', 'MANUAL') THEN
        RAISE_APPLICATION_ERROR(-20002, 'p_kind must be AUTO or MANUAL, got ' || NVL(p_kind, 'NULL') || '.');
    END IF;

    INSERT INTO stub_sign_request (request_id, doc_id, kind, signer)
    VALUES (p_request_id, p_doc_id, p_kind, p_signer);
EXCEPTION
    -- Idempotency: the request already exists (also after waiting on a concurrent insert).
    WHEN DUP_VAL_ON_INDEX THEN
        NULL;
    -- Unknown document.
    WHEN OTHERS THEN
        IF SQLCODE = -2291 THEN
            RAISE_APPLICATION_ERROR(-20003, 'Unknown document ' || p_doc_id || '.');
        END IF;
        RAISE;
END create_sign_request;
/

CREATE OR REPLACE PROCEDURE cancel_sign_request (
    p_request_id IN VARCHAR2
) AS
BEGIN
    -- Idempotent: final statuses and unknown ids are left alone.
    UPDATE stub_sign_request
       SET status = 'REJECTED',
           status_reason = 'CANCELLED'
     WHERE request_id = p_request_id
       AND status = 'PENDING';
END cancel_sign_request;
/

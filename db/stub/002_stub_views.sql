-- Contract views over the stub tables. Column names and types follow db/contract/README.md.

CREATE OR REPLACE VIEW sign_incoming_document_v AS
SELECT doc_id,
       doc_type,
       attributes,
       content_hash,
       created_at
  FROM stub_incoming_document;

CREATE OR REPLACE VIEW sign_request_v AS
SELECT request_id,
       status,
       status_reason,
       signed_at,
       updated_at,
       file_ref,
       file_hash,
       file_size
  FROM stub_sign_request;

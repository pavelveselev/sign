-- Grants to the service schema, as required by db/contract/README.md.
-- &&service_schema is a SQL*Plus / SQLcl substitution variable: pass it on the command line
-- or DEFINE it before running. Test runners replace it textually.

GRANT SELECT  ON sign_incoming_document_v TO &&service_schema;
GRANT SELECT  ON sign_request_v           TO &&service_schema;
GRANT EXECUTE ON create_sign_request      TO &&service_schema;
GRANT EXECUTE ON cancel_sign_request      TO &&service_schema;

-- SGN_STAGE_INSTANCE.data: own data of the stage handler (for example, an id in an external system).

ALTER TABLE sgn_stage_instance ADD (data CLOB);

COMMENT ON COLUMN sgn_stage_instance.data IS 'Stage handler data; the format is defined by the handler.';

COMMENT ON COLUMN sgn_route_instance.status_reason IS 'Reason code for FAILED: UNKNOWN_DOC_TYPE, ROUTE_NOT_FOUND, ARTIFACT_MISMATCH, ATTEMPTS_EXHAUSTED, CONDITION_ERROR, SIGNING_ERROR, STAGE_ERROR.';

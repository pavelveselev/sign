-- SGN_AUDIT_EVENT: журнал только для вставки. Записывается в той же транзакции, что и описываемый переход.

CREATE TABLE sgn_audit_event (
    id                 NUMBER(19)               GENERATED ALWAYS AS IDENTITY,
    route_instance_id  NUMBER(19)               NOT NULL,
    stage_instance_id  NUMBER(19),
    event_type         VARCHAR2(50 CHAR)        NOT NULL,
    payload            CLOB,
    actor              VARCHAR2(200 CHAR)       NOT NULL,
    created_at         TIMESTAMP WITH TIME ZONE DEFAULT SYSTIMESTAMP NOT NULL,
    CONSTRAINT sgn_audit_event_pk PRIMARY KEY (id),
    CONSTRAINT sgn_audit_event_route_fk
        FOREIGN KEY (route_instance_id) REFERENCES sgn_route_instance (id),
    CONSTRAINT sgn_audit_event_stage_fk
        FOREIGN KEY (stage_instance_id) REFERENCES sgn_stage_instance (id),
    CONSTRAINT sgn_audit_event_payload_ck CHECK (payload IS JSON)
);

CREATE INDEX sgn_audit_event_route_ix ON sgn_audit_event (route_instance_id);
CREATE INDEX sgn_audit_event_stage_ix ON sgn_audit_event (stage_instance_id);

COMMENT ON TABLE sgn_audit_event IS 'Audit journal, insert only.';
COMMENT ON COLUMN sgn_audit_event.event_type IS 'ROUTE_CREATED, ROUTE_FINISHED, STAGE_SKIPPED, STAGE_ENTERED, STAGE_REQUESTED, STAGE_DONE, STAGE_REJECTED, STAGE_EXPIRED, STAGE_FAILED, TECHNICAL_FAILURE.';
COMMENT ON COLUMN sgn_audit_event.actor IS 'Replica id that made the transition.';

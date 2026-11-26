-- ЗАГЛУШКА внешней стороны (см. db/contract/README.md). Только для разработки и тестов.
-- Выполнять от имени владельца внешней схемы. Таблица — деталь реализации заглушки:
-- сервис её никогда не трогает, тесты трогают (INSERT входящих документов).

CREATE TABLE stub_incoming_document (
    doc_id        VARCHAR2(200 CHAR)       NOT NULL,
    doc_type      VARCHAR2(100 CHAR)       NOT NULL,
    attributes    CLOB,
    content_hash  VARCHAR2(200 CHAR),
    created_at    TIMESTAMP WITH TIME ZONE DEFAULT SYSTIMESTAMP NOT NULL,
    CONSTRAINT stub_incoming_document_pk PRIMARY KEY (doc_id)
);

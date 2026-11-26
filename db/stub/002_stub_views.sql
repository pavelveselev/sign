-- Представление контракта поверх таблицы заглушки. Имена и типы столбцов соответствуют db/contract/README.md.

CREATE OR REPLACE VIEW sign_incoming_document_v AS
SELECT doc_id,
       doc_type,
       attributes,
       content_hash,
       created_at
  FROM stub_incoming_document;

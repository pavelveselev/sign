-- Права для учётной записи сервиса, как требует db/contract/README.md.
-- &&service_schema — подстановочная переменная SQL*Plus / SQLcl: передайте её в командной строке
-- или задайте через DEFINE перед запуском. Тестовые раннеры заменяют её текстуально.

GRANT SELECT ON sign_incoming_document_v TO &&service_schema;

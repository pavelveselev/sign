-- Права для схемы сервиса, как требует db/contract/README.md.
-- &&service_schema — подстановочная переменная SQL*Plus / SQLcl: передайте её в командной строке
-- или задайте через DEFINE перед запуском. Тестовые раннеры заменяют её текстуально.

GRANT SELECT  ON sign_incoming_document_v TO &&service_schema;
GRANT SELECT  ON sign_request_v           TO &&service_schema;
GRANT EXECUTE ON create_sign_request      TO &&service_schema;
GRANT EXECUTE ON cancel_sign_request      TO &&service_schema;

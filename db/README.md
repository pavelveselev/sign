# Database scripts

| Folder | Content | Run as |
|---|---|---|
| `service/` | DDL of the service schema (`SGN_*` tables) | service schema owner |
| `contract/` | Specification of the external objects the service uses | — (document) |
| `stub/` | Stub of the external side: tables, contract views and procedures, grants | external schema owner |

The service never changes the schema at startup. Scripts are applied by the Oracle developer.

## Conventions

- Scripts are numbered and run in order of their names, each exactly once.
- SQL*Plus / SQLcl syntax: a SQL statement ends with `;`, a PL/SQL block ends with `/` on its own line.
- Run with `WHENEVER SQLERROR EXIT FAILURE` so the first error stops the run.
- A new change is a new numbered script; applied scripts are never edited.

## Setting up a development database

```
-- 1. as a DBA: create the two schemas
CREATE USER sgn_ext     IDENTIFIED BY ... QUOTA UNLIMITED ON users;
CREATE USER sgn_service IDENTIFIED BY ... QUOTA UNLIMITED ON users;
GRANT CREATE SESSION, CREATE TABLE, CREATE VIEW, CREATE PROCEDURE, CREATE TRIGGER TO sgn_ext;
GRANT CREATE SESSION, CREATE TABLE, CREATE SEQUENCE TO sgn_service;  -- identity columns need CREATE SEQUENCE

-- 2. as sgn_ext:     @stub/001_stub_tables.sql ... @stub/004_stub_grants.sql  (service_schema = SGN_SERVICE)
-- 3. as sgn_service: @service/001_sgn_route_instance.sql ... @service/004_sgn_audit_event.sql
```

The service schema does not reference the external schema in DDL, so steps 2 and 3 are independent.

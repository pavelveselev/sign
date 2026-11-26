# Database scripts

| Folder | Content | Run as |
|---|---|---|
| `postgres/` | Idempotent script of the Postgres state schema, generated from EF migrations | Postgres DBA |
| `contract/` | Specification of the external Oracle objects the service uses | — (document) |
| `stub/` | Stub of the external Oracle side: table, contract view, grant | external schema owner |

The service never changes the schema at startup. In Oracle the service only reads the incoming documents view;
it has no tables of its own there.

## Conventions

- Scripts are numbered and run in order of their names, each exactly once.
- SQL*Plus / SQLcl syntax: a SQL statement ends with `;`, a PL/SQL block ends with `/` on its own line.
- Run with `WHENEVER SQLERROR EXIT FAILURE` so the first error stops the run.
- A new change is a new numbered script; applied scripts are never edited.

## Setting up a development Oracle database

```
-- 1. as a DBA: create the external schema and the service account
CREATE USER sgn_ext     IDENTIFIED BY ... QUOTA UNLIMITED ON users;
CREATE USER sgn_service IDENTIFIED BY ...;
GRANT CREATE SESSION, CREATE TABLE, CREATE VIEW TO sgn_ext;
GRANT CREATE SESSION TO sgn_service;

-- 2. as sgn_ext: @stub/001_stub_tables.sql ... @stub/003_stub_grants.sql  (service_schema = SGN_SERVICE)
```

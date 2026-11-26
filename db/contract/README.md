# External contract: source system ↔ signing router service

This document specifies the database objects the signing router service (`Invest.Sign.Router`)
uses in the external Oracle schema. The service only **reads** from Oracle: it uses exactly the
object listed here and writes nothing.

- The external schema name is a service configuration parameter (`Oracle:ExternalSchema`). The service
  always qualifies object names with it, so no synonyms are needed.
- Oracle 19c or newer.
- A reference implementation for development and tests is in [`db/stub/`](../stub).

| Object | Kind | Service needs |
|---|---|---|
| `sign_incoming_document_v` | view | `SELECT` |

## View `sign_incoming_document_v`

Documents to be routed for signing.

| Column | Type | Null | Meaning |
|---|---|---|---|
| `doc_id` | `VARCHAR2(200 CHAR)` | no | Document id, unique within the view |
| `doc_type` | `VARCHAR2(100 CHAR)` | no | Document type; selects the route |
| `attributes` | `CLOB` | yes | JSON object with document attributes used by the route (e.g. `file_name`) and its conditions |
| `content_hash` | `VARCHAR2(200 CHAR)` | yes | Hash of the source document |
| `created_at` | `TIMESTAMP WITH TIME ZONE` | no | When the document appeared |

Guarantees:

- `doc_id` is unique.
- Rows never disappear and never change once visible.
- A row becomes visible only after the document is complete (committed).
- `attributes`, if present, is a JSON object (`{...}`). Numbers should be JSON numbers; decimal
  strings with a dot separator are also accepted.

The service reads the view page by page ordered by `(created_at, doc_id)`, starting from the latest
`created_at` it already knows minus a lookback window (`Router:DiscoveryLookback`), and skips documents
it already has. Filtering by `created_at` should therefore be efficient (index on the underlying table).

## Grants

```sql
GRANT SELECT ON <external_schema>.sign_incoming_document_v TO <service_account>;
```

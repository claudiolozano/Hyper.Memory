# Knowledge projection

HyperMemory's knowledge graph is a derived, rebuildable view over immutable memory. It improves structural recall without changing the append-only contract or making Hermes depend on a graph database.

## Invariants

- `memory_atoms` and immutable event envelopes remain the source of truth.
- Projection never updates or deletes a historical memory, vector, audit row, or evidence row.
- Projection work is incremental, idempotent, bounded, and safe to retry after interruption.
- Deleting all `knowledge_*` content cannot lose historical memory.
- Assistant output is unverified unless independent evidence explicitly verifies it.
- Projector versions are recorded per memory so a future extractor can rebuild stale projections.

## Derived schema

- `knowledge_entities`: typed requests, responses, projects, sessions, sources, authors, decisions, and artifacts.
- `knowledge_mentions`: the memory version and role that introduced an entity, including evidence class and optional offsets.
- `knowledge_edges`: directional relation, source memory, evidence class, and confidence.
- `knowledge_projection_state`: projector version, completion state, and processing timestamp for each memory.

The first deterministic projector creates structural relations such as `HAS_RESPONSE`, `PART_OF_PROJECT`, `OCCURRED_IN_SESSION`, `SOURCED_FROM`, `AUTHORED_BY`, `ASSERTS_DECISION`, `REQUESTED_ARTIFACT`, and `PRODUCED_ARTIFACT`.

## Evidence classes

- `EXTRACTED`: explicitly present in a stored field or deterministic turn structure.
- `INFERRED`: a conservative deduction, such as treating a formatted response title as a produced artifact.
- `AMBIGUOUS`: preserved for a relationship that must not be presented as certain.
- `VERIFIED`: reserved for independent checks such as a file hash, execution result, or test record. The initial projector does not manufacture verified evidence.

## Operations

The background worker processes pending memories in small batches. `GET /memory/knowledge/status` reports its progress. `POST /memory/knowledge/rebuild` removes only the derived projection; the worker then reconstructs it from the immutable archive. This operation intentionally leaves `memory_atoms`, full-text indexes, vectors, evidence, audits, and event envelopes unchanged.

Hermes' completed-turn message context is also inspected for successful `write_file` and `patch` operations. A file is accepted as verified evidence only when its resolved path remains inside the active workspace, it exists as a regular file, and it is at most 32 MB. HyperMemory records a SHA-256 hash and relative path. This proves file existence and exact content at capture time, but not successful execution or behavioral correctness.

During recall, strong FTS/semantic results seed a bounded one-hop expansion through artifact, file, decision, source, and content-hash entities. Broad project and session entities are intentionally excluded from expansion to avoid topic drift. Each expanded result includes explicit knowledge reasons in the API response.

Completed foreground terminal calls are stored separately from assistant prose. HyperMemory records the redacted command, workspace-relative working directory, exit code, status, and a SHA-256 digest of the redacted output. When Hermes supplies its own `verification_evidence`, the projection also records the check kind (`test`, `build`, `lint`, `typecheck`, `verify`, etc.), result, and `targeted` or `full` scope. A background process spawn is never treated as completed execution.

Hermes receives strict completion guards during recall: a file hash proves file existence, a zero exit code proves only that command outcome, a targeted passing check proves only its target, and a full passing check remains historical evidence rather than a timeless guarantee.

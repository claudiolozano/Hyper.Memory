# HyperMemory 2.0 operational memory

## Purpose and boundaries

HyperMemory 2.0 extends the existing historical memory; it does not replace it. Hermes remains the agent and tool coordinator, the model reasons, validators produce evidence, and HyperMemory persists and retrieves state. Memory never grants permission and an LLM statement is never converted into verified evidence.

The five questions the operational layer is designed to answer are: what is the goal, what exists, what happened and why, what remains, and what is actually verified.

## Architecture and data flow

The immutable `memory_atoms`, vectors, FTS index and knowledge projection remain unchanged. A second additive journal stores scoped operational events (`workspace`, `project`, `session`, `agent`, `task`). A disposable SQLite projection derives current state from that journal. Rebuilding the projection never changes the source events.

Before a model turn, the Hermes provider asks the operational router for a bounded slice ordered as: working memory, blocking errors, tasks, validations, contracts, artifacts, decisions, goals/requirements/constraints, checkpoint, and only then relevant historical memory. After a completed turn, the provider durably queues verified file observations, foreground execution results, validator evidence and errors. Failed network delivery remains in the local outbox for retry. Session changes create a logical checkpoint when project state exists.

Hermes' published provider hook surface is used (`prefetch`, `sync_turn`, `on_session_switch`). There is no fabricated tool hook: tool results are extracted from the completed turn transcript. If Hermes later publishes a native `after_tool`/`before_completion` hook, it can call the same generic operational API without changing Core.

## Memory types

- Historical memory: immutable conversations and durable knowledge using the original retrieval path.
- Working memory: small mutable projection with TTL, priority, maximum active-item count and explicit removal. Raw mutations remain auditable.
- Project memory: artifacts, open relationships, contracts, tasks and dependencies, typed statements, decisions, errors and validations.
- Validation/evidence: `PLANNED`, `NOT_RUN`, `RUNNING`, `PASS`, `FAIL`, `BLOCKED`, `UNKNOWN`, and `STALE`. PASS/FAIL without durable evidence is reduced to UNKNOWN.
- Checkpoints: canonical project snapshots with SHA-256 hashes; they support recovery and comparison, not physical filesystem rollback.

Artifacts and relationship types are strings plus extensible metadata, so Core has no language/framework switch. Artifact deletion is retained as state and invalidates directly or transitively affected validation records. Dependencies are traversed selectively; unrelated objects are not invalidated.

Errors are deduplicated by fingerprint, preserve first/last seen timestamps and occurrence counts, cap repair attempts, and require real evidence before resolution. Decisions are superseded rather than overwritten.

Completion is advisory and explains its result. It checks explicit tasks, active dependencies, required validation/evidence, unresolved errors and optional contract validation. Its disposition is `VERIFIED_COMPLETE`, `UNVERIFIED_COMPLETE`, `INCOMPLETE`, or `BLOCKED`; UNKNOWN is preferred to a false PASS.

## Configuration

All operational features are enabled for a new 2.0 installation. They remain independently configurable under `HyperMemory:Operational`:

```json
{
  "EnableEventJournal": true,
  "EnableProjectState": true,
  "EnableValidationMemory": true,
  "EnableErrorMemory": true,
  "EnableDecisionMemory": true,
  "EnableTaskGraph": true,
  "EnableContracts": true,
  "EnableCheckpoints": true,
  "EnableSelectiveMemoryRouter": true,
  "EnableCapabilityRouting": true,
  "EnableToolEventCapture": true,
  "EnableWorkingMemory": true,
  "MaxRepairAttempts": 3,
  "WorkingMemoryDefaultTtlMinutes": 1440,
  "WorkingMemoryMaxItems": 200
}
```

Setting every new flag to `false` restores the observable 1.7 behavior. Required dependency combinations are validated at startup; inconsistent configuration fails safely before writing.

## Storage, migration, backup and rollback

Schema 5 adds `operational_events` and explicit migration history. Schema 6 adds the rebuildable project-state projection. Migrations use only additive `CREATE TABLE/INDEX IF NOT EXISTS` operations. Existing atoms, vectors, audits, FTS and knowledge data are not rewritten.

Before upgrading, close Hermes and copy the complete `Hyper_Memory` directory. SQLite backup must include the database through its online-backup mechanism or while the service is stopped. To roll back behavior without deleting data, disable the operational flags and restart; 1.7-compatible historical read/write remains available, and re-enabling 2.0 recovers the operational journal. For physical rollback, stop HyperMemory and restore the complete backed-up directory. Never copy only the main `.sqlite3` file while WAL writers are active.

The Windows uninstaller restores the prior Hermes provider and removes only installation-owned skill/plugin files. Historical storage is preserved unless the user explicitly requests erasure with exact-path confirmation.

## Retrieval, scale and retention

The router enforces a character budget and never injects the whole archive. Project projection is incremental, indexed and rebuildable. Working memory is bounded and expires; current project state consolidates event history without destroying it. Historical and raw operational events remain indefinite by default to preserve the project's zero-deletion invariant. Archive size and latency should be monitored with `/memory/scale`, `/memory/diagnostics` and the evaluation runner; no silent age-based purge occurs.

## Validator and adapter extension

Implement `IValidationAdapter` outside `HyperMemory.Core`:

1. Give the adapter a stable `ValidatorId`.
2. Make `CanValidate` inspect generic request metadata/capabilities.
3. Return evidence containing provenance, hashes/exit status where applicable, producer and timestamp.
4. Return UNKNOWN if the dependency, permission or verification mechanism is unavailable.
5. Register the adapter through dependency injection; do not add technology-specific conditionals to Core.

Capability providers implement `ICapabilityProvider`. Routing selects a compatible declared provider but neither executes it nor grants authorization. Missing mandatory capabilities are reported; optional provider failure degrades safely.

## Security and troubleshooting

Secrets are redacted both in the Hermes provider and again before operational persistence, including nested JSON and metadata fields. The service is loopback-only and `/memory/*` uses the installation token.

- Operational endpoint returns 404: confirm the corresponding flags and their prerequisites, then restart HyperMemory.
- Context has no project state: complete at least one Hermes turn with operational capture enabled and inspect the durable outbox if the service was offline.
- Validation is UNKNOWN: install/enable a suitable adapter or provide actual tool evidence; do not convert it manually to PASS.
- Validation became STALE: an observed artifact or transitive dependency changed; run the appropriate validator again.
- Projection appears inconsistent: rebuild it from immutable operational events; do not edit the journal.
- Hermes still uses another provider: reinstall through the official installer; it refuses to overwrite an unrelated provider silently.

# HyperMemory roadmap

This roadmap is the release gate for the work originally planned as HyperMemory 1.5, 1.6 and 1.7. A feature being present in an earlier development build does not make a milestone complete: every unchecked acceptance item still requires automated or recorded end-to-end evidence.

## 1.5 — Trustworthy memory

- [x] Deterministic request, response, project, decision, artifact and file projection.
- [x] Independent workspace-file hashes, execution results and scoped test evidence.
- [x] FTS5 + semantic retrieval + bounded relation expansion and project/time filters.
- [x] Topic-isolation and substantive-original regression tests.
- [x] Deterministic labelled-person and normalized-date projection.
- [x] Explicit stored, corrected and superseded version evidence.
- [x] Recorded Desktop and TUI automatic-capture acceptance run.

## 1.6 — Multi-year scale

- [x] Incremental bounded projection, complete FTS5 coverage and bounded semantic reranking.
- [x] Non-destructive SQLite index maintenance and operational diagnostics.
- [x] LoCoMo and LongMemEval schema adapters with dataset hashes and retrieval metrics.
- [x] Reproducible 100,000-memory scale profile in CI or a recorded release artifact.
- [ ] Optional 1,000,000-memory soak profile outside normal CI.
- [x] ANN decision record based on measured latency, recall, disk and semantic coverage.
- [ ] Full official-dataset evaluation reports (datasets are evaluator-supplied).

## 1.7 — Ecosystem and installation

- [x] Optional preview-first Graphify `graph.json` import with provenance and no network dependency.
- [x] Silent installation, owned-component uninstallation and preservation of Hermes configuration.
- [x] Upgrade discovery and failed-new-install cleanup.
- [x] Pre-migration backup manifest and integrity check.
- [x] Transactional upgrade rollback that restores the previous active integration.
- [x] Explicit, previewable choice to preserve or physically erase historical memory.
- [x] Recorded upgrade/uninstall acceptance run against an installed prior release.

## Mandatory release acceptance

- [x] Hermes works with knowledge projection disabled.
- [x] Desktop and TUI capture automatically.
- [x] Deleting a Hermes session does not delete historical memory.
- [x] Derived knowledge can be removed and rebuilt without losing memory atoms.
- [x] Requested work is not represented as verified execution.
- [x] Retrieval regression prevents unrelated-topic dominance.
- [x] Upgrade preserves all installed historical memory.
- [x] Uninstallation targets only installation-owned Hermes components.
- [x] No data leaves the computer by default.

The final signed installer is released only after every mandatory acceptance item is checked with reproducible evidence.

# HyperMemory 2.0.0 release-candidate acceptance

Date: 2026-08-13

## Proven compatibility

- Pre-change source baseline: `c220e70e2b77f9e895e01070482953eeb1fcd83a`.
- Online SQLite/development backup: `artifacts/development-backups/baseline-1.7.0-20260813T122007958Z`.
- Backup restore rehearsal: 90 atoms, 90 vectors, 90 audits, schema 4, integrity valid.
- The installed 1.7.0 service remained untouched and healthy during development.
- With every operational feature disabled, the legacy schema remains version 4 and no operational table or endpoint is exposed.
- Additive upgrade, feature-flag rollback and re-enable preserve both legacy and operational data.

## Verification results

- Release build: succeeded with 0 warnings and 0 errors.
- .NET regression and operational suite: 69/69 passed.
- Hermes provider suite: 20/20 passed.
- Deterministic retrieval evaluation: passed.
- Recall@5: 1.0.
- Grounded top-1 accuracy: 1.0.
- Mean reciprocal rank: 1.0.
- Topical precision@5: 0.9333.
- Superseded leak rate: 0.
- Retrieval latency p95: 20.23 ms.
- 10,000-event scale rehearsal: passed previously in this development cycle with complete projection and integrity.

Covered scenarios include restart/rebuild, multi-session and multi-agent history, model/agent switch, concurrent revisions, rollback, bounded working memory, typed project statements, secrets (including nested JSON), evidence-only PASS, validation lifecycle, artifact modification/deletion, transitive dependency invalidation, repeated resolved errors, repair limits, decision supersession, context budget, capability degradation and false-completion prevention.

## Installer candidate

- Version: 2.0.0.0.
- Path: `Instalador/HyperMemorySetup.exe`.
- SHA-256: `CF64B54EE1AA05E2CEC001AC5B643FA0CC8527CFF2AA19B3D90CBEAE9949759B`.
- Local signature status: `NotSigned`.

The local candidate is intentionally not described as an official signed release. The official installer must be rebuilt and signed from reviewed public source by `.github/workflows/signpath-release.yml` after the 2.0.0 commit is published and the six SignPath repository settings are available. The workflow independently tests, signs the API/Bridge, rebuilds the installer from those signed binaries, signs the installer and verifies Authenticode before publishing its artifact.

# HyperMemory

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).

## Code signing policy

Official Windows releases are built from the public HyperMemory repository by the reviewed GitHub Actions workflow and submitted to SignPath.io. The certificate is provided by SignPath Foundation; private signing keys are never held in this repository or by its maintainers.

- Committer, reviewer and release approver: [Claudio Lozano](https://github.com/claudiolozano).
- Privacy policy: [Privacy and retention](docs/PRIVACY-AND-RETENTION.md).
- Network disclosure: HyperMemory stores memories locally and does not transfer information to other networked systems unless the user or the person operating it explicitly configures or requests such a connection. Its API is bound to the local loopback interface.
- Release provenance: only artifacts produced from this repository by `.github/workflows/signpath-release.yml` are eligible for official signing.

HyperMemory is a local, model-agnostic, append-only automatic memory service for Hermes Agent. Before every user turn, Hermes retrieves relevant history through the provider; after every completed turn, the conversation is stored without requiring the user to invoke a skill. It targets .NET 10 and opens directly in Visual Studio 2026 through `HyperMemory.sln`.

“Infinite context” here means unbounded durable history plus bounded retrieval into the model context window. It does not claim that an LLM has an infinite token window.

## Safety invariants

- The user chooses a base path or an existing `Hyper_Memory` path. Runtime persistence is always resolved to a directory whose final name is exactly `Hyper_Memory`.
- A link or junction cannot be used as the `Hyper_Memory` directory.
- Memories are immutable versions. Reusing a logical ID appends a version; it never updates the previous row.
- Every accepted event also gets a SHA-256-addressed JSON envelope under `Hyper_Memory\events`. It is opened with `CreateNew`, flushed to disk, and never reopened for writing by the subsystem.
- Event IDs are idempotency keys. Reusing one with different content is rejected.
- Provenance, validity ranges, claim keys and supersession links are append-only evidence attached to each version.
- Contradictions preserve both records. They never trigger automatic rewriting or deletion.
- There is no delete, purge, retention, vacuum, or destructive repair API.
- Full input is committed before optional background summarization. A summary is another append.

SQLite necessarily updates its own database pages and WAL as part of ACID operation; the invariant is that historical memory records are never updated or deleted. Immutable event envelopes provide an additional raw recovery layer. For physical disaster recovery, back up the complete `Hyper_Memory` directory at the filesystem level.

## Projects

- `HyperMemory.Core`: immutable domain records and storage/model abstractions.
- `HyperMemory.Infrastructure`: SQLite FTS5, vector storage, deterministic knowledge projection, Ollama adaptation, path policy, integrity checks.
- `HyperMemory.Api`: loopback-only Minimal API and cumulative background summaries.
- `HyperMemory.Bridge`: stdin/stdout CLI, a Hermes-compatible skill and the automatic memory-provider plugin.
- `HyperMemory.Tests`: persistence, restart, search, idempotency and integrity tests.

## Build and test

```powershell
dotnet restore HyperMemory.sln
dotnet build HyperMemory.sln -c Release
dotnet test HyperMemory.sln -c Release --no-build
```

## Retrieval evaluation

The deterministic evaluation corpus exercises exact artifact constraints, topic isolation, current-vs-superseded decisions, graph-expanded corrections, and full-text recall beyond the bounded semantic window. It reports recall@5, grounded top-1 accuracy, mean reciprocal rank, topical precision, topic drift, p50/p95 latency, storage growth, superseded leakage, and integrity.

```powershell
.\scripts\Run-Evaluation.ps1
.\scripts\Run-Evaluation.ps1 -Output artifacts\evaluation\latest.json
```

The command exits unsuccessfully when a quality threshold regresses, and the signed-release workflow runs it before producing binaries.

The same runner accepts the official JSON schemas for [LoCoMo](https://github.com/snap-research/locomo) and [LongMemEval](https://github.com/xiaowu0162/LongMemEval). Datasets are supplied by the evaluator and are not redistributed by HyperMemory. The adapter indexes each benchmark history in an isolated temporary store, compares retrieved session IDs with the official evidence IDs, records the dataset SHA-256, and reports evidence recall@K, hit rate, MRR, category breakdown, and latency. It deliberately does not label retrieval success as answer-generation correctness.

```powershell
.\scripts\Run-Evaluation.ps1 -Dataset C:\benchmarks\locomo10.json -Format locomo -Limit 100 -TopK 5 -Output artifacts\evaluation\locomo.json
.\scripts\Run-Evaluation.ps1 -Dataset C:\benchmarks\longmemeval_s_cleaned.json -Format longmemeval -Limit 100 -TopK 5 -Output artifacts\evaluation\longmemeval.json
```

## Controlled graph import

`POST /memory/import/graph` accepts a preview-first, provenance-preserving import of Graphify NetworkX `graph.json` data. The import is canonical-hashed, size-limited, idempotent, confidence-aware, and rejected before writing when identifiers, paths, edges, or evidence are invalid. Imported data is stored as immutable source atoms and projected by HyperMemory's own rebuildable knowledge layer; no external tool writes directly to internal graph tables. See [the external graph import contract](docs/EXTERNAL-GRAPH-IMPORT.md).

## Privacy and retention

Automatic memory remains indefinite by default, but users can mark a turn as off-record with ordinary language such as `no guardes esto en la memoria`. Capture can also be disabled locally. Secrets are sanitized both in the Hermes provider and again at the API boundary, and redaction/classification information is retained for audit without retaining the detected value. HyperMemory performs no silent age-based deletion. See [privacy and retention](docs/PRIVACY-AND-RETENTION.md).

## Choose storage and run

### Instalación para usuarios finales

El entregable recomendado es `HyperMemorySetup.exe`. No requiere .NET, Visual Studio, terminal ni permisos de administrador:

1. Hacer doble clic en el instalador.
2. Elegir la unidad o carpeta donde se creará `Hyper_Memory`.
3. Pulsar **Instalar HyperMemory** y reiniciar Hermes.

El instalador registra el inicio automático, activa `hypermemory` como proveedor de memoria de Hermes y aparece como **HyperMemory para Hermes** en Configuración de Windows > Aplicaciones instaladas. Desde ese momento, Hermes consulta recuerdos antes de contestar y guarda automáticamente cada intercambio terminado. La desinstalación detiene HyperMemory, restaura la configuración anterior de memoria y elimina exclusivamente el Skill y el plugin marcados como propiedad de esa instalación; no modifica otros Skills, sesiones ni archivos de Hermes. La memoria histórica permanece en `Hyper_Memory` por la política de cero borrado.

Instalación desatendida para administración:

```powershell
HyperMemorySetup.exe --silent --storage-root "D:\"
```

La instalación silenciosa no deja procesos hijos bloqueando la herramienta de despliegue; HyperMemory arranca automáticamente en el siguiente inicio de sesión de Windows. La instalación gráfica lo inicia inmediatamente.

Windows registra también `QuietUninstallString`, por lo que las herramientas corporativas pueden ejecutar la desinstalación silenciosa estándar.

Para generar un instalador nuevo:

```powershell
.\scripts\Build-Installer.ps1
```

Direct development run:

```powershell
dotnet run --project src/HyperMemory.Api -- --storage-root "D:\"
```

This creates or reuses `D:\Hyper_Memory`. Alternatively set `HYPERMEMORY_STORAGE` or `HyperMemory:StorageBasePath`. If no path is supplied, startup fails safely instead of choosing a drive implicitly.

For an immutable, timestamped deployment entirely below the selected folder:

```powershell
.\scripts\Install-HyperMemory.ps1 -StorageBasePath "D:\"
```

Each deployment gets a new `Hyper_Memory\app\releases\<timestamp>` directory. The installer refuses to overwrite an existing release.

## API

The default listener is `http://127.0.0.1:5077` and is intentionally not exposed to the LAN.

- `POST /memory/upsert`: append a memory version.
- `POST /memory/query`: hybrid FTS5 and cosine retrieval with optional occurrence/validity filters.
- `POST /memory/summarize`: summarize through the active/configured Ollama model and optionally append it.
- `GET /memory/integrity`: SQLite, row-count and SHA-256 verification.
- `GET /live`: lightweight process liveness.
- `GET /health`: fast readiness and row-count consistency.
- `GET /memory/integrity`: full on-demand SQLite and immutable-envelope verification.
- `GET /memory/knowledge/status`: projection progress, pending memories, entity count and relation count.
- `GET /memory/knowledge/{versionId}`: entities and evidence-labelled relations derived from one immutable memory.
- `POST /memory/knowledge/rebuild`: discard only derived knowledge and queue a complete rebuild from `memory_atoms`.
- `GET /memory/scale`: database/WAL size, index coverage, projection backlog, semantic-window coverage and ANN evaluation signal.
- `POST /memory/maintenance`: non-destructive SQLite statistics optimization and passive WAL checkpoint.
- `GET /memory/diagnostics`: one operational view of capture counts, turn-index backlog, graph backlog/failures, last stored memory and database/WAL growth.

The installed Hermes provider authenticates all `/memory/*` requests with a random per-installation token kept in the user's protected application directory. The service stays bound to loopback and the supervisor restarts it automatically after an unexpected exit.

If the active Ollama model supports `/api/embed`, its exact model name and vector dimension label every vector. If it does not, a deterministic local token-hash embedding keeps search available; disable this fallback with `AllowDeterministicEmbeddingFallback=false`. FTS retrieval remains available across model changes, while semantic comparison occurs only inside an identical provider/model/dimension space.

### Historical evidence

`/memory/upsert` retains the original request contract and accepts additional optional fields: `sourceUri`, `sourceTitle`, `author`, `validFrom`, `validTo`, `supersedesVersionId`, `claimKey`, and `statedConfidence`. Existing clients require no changes.

`/memory/query` additionally accepts `occurredFrom`, `occurredTo`, `validAt`, and `includeSuperseded`. Each result preserves the original `atom`, `score`, `textScore`, and `semanticScore` fields and adds:

- `citation`: stable version ID, label, source URI, occurrence date and content hash.
- `evidence`: status, calculated confidence, primary-source flag, supersession flag and contradicting version IDs.

Contradiction detection is conservative: two different contents are linked only when the caller assigns the same explicit `claimKey`. Confidence describes evidence quality and retrieval relevance; it is not a guarantee that a stored statement is true.

### Rebuildable knowledge projection

HyperMemory incrementally projects immutable turns into typed entities and relations without rewriting the original memories. Requests, responses, projects, sessions, sources, authors, decisions and conservatively detected artifacts are represented separately. Relations carry `EXTRACTED`, `INFERRED`, `AMBIGUOUS`, or `VERIFIED` evidence classes; generated assistant output is never marked verified merely because it was stored.

Successful Hermes file-tool operations are independently constrained to the active workspace and hashed. Completed foreground terminal calls retain their exit status, while Hermes verification results preserve check kind and targeted/full scope. Background process startup is never misreported as completed execution, and targeted tests never imply that the whole project passed.

The projection tables are disposable indexes, not a second source of truth. They can be deleted and rebuilt deterministically from `memory_atoms`; the background worker processes old and new memories in bounded batches. Hybrid queries expand strong text/semantic seeds through verified files, artifacts, decisions and sources, and return `knowledge` reasons alongside the existing retrieval scores. See [Knowledge projection](docs/KNOWLEDGE-PROJECTION.md).

## Hermes integration

Publish the Bridge, then install the included skill into a new Hermes skill directory:

```powershell
dotnet publish src/HyperMemory.Bridge -c Release -o .\artifacts\bridge
.\src\HyperMemory.Bridge\hermes-skill\install.ps1 -PublishedBridgeDirectory .\artifacts\bridge
```

The end-user installer deploys both the optional skill and the `hypermemory` external memory provider through Hermes' official plugin interface. It refuses to overwrite an existing skill or plugin and never modifies Hermes source code. If another external memory provider is active, installation stops instead of replacing it silently.

## Operational limits

FTS5 indexes the complete history and keeps exact textual recall available as the archive grows. Semantic reranking is deliberately bounded to the most recent compatible vector window, so query cost does not grow without limit; older records remain reachable through the full-text index. The turn index is migrated incrementally in small background batches. A future ANN-backed `IMemoryStore` can expand old semantic-only recall without changing Core, API, or Bridge.

Knowledge projection is also bounded and incremental. SQLite runs periodic non-destructive `optimize` and passive WAL checkpoint maintenance. `/memory/scale` recommends evaluating an ANN implementation only after the archive exceeds both 100,000 memories and twenty times the configured recent semantic window; this is a measurement trigger, not an automatic storage migration.

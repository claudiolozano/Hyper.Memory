# HyperMemory

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).

HyperMemory is a local, model-agnostic, append-only memory service for Hermes Agent. It targets .NET 10 and opens directly in Visual Studio 2026 through `HyperMemory.sln`.

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
- `HyperMemory.Infrastructure`: SQLite FTS5, vector storage, Ollama adaptation, path policy, integrity checks.
- `HyperMemory.Api`: loopback-only Minimal API and cumulative background summaries.
- `HyperMemory.Bridge`: stdin/stdout CLI and a Hermes-compatible skill package.
- `HyperMemory.Tests`: persistence, restart, search, idempotency and integrity tests.

## Build and test

```powershell
dotnet restore HyperMemory.sln
dotnet build HyperMemory.sln -c Release
dotnet test HyperMemory.sln -c Release --no-build
```

## Choose storage and run

### Instalación para usuarios finales

El entregable recomendado es `HyperMemorySetup.exe`. No requiere .NET, Visual Studio, terminal ni permisos de administrador:

1. Hacer doble clic en el instalador.
2. Elegir la unidad o carpeta donde se creará `Hyper_Memory`.
3. Pulsar **Instalar HyperMemory** y reiniciar Hermes.

El instalador registra el inicio automático y aparece como **HyperMemory para Hermes** en Configuración de Windows > Aplicaciones instaladas. La desinstalación detiene HyperMemory y elimina exclusivamente el Skill marcado como propiedad de esa instalación; no modifica otros Skills, configuración, sesiones ni archivos de Hermes. La memoria histórica permanece en `Hyper_Memory` por la política de cero borrado.

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
- `GET /health`: readiness plus integrity.

If the active Ollama model supports `/api/embed`, its exact model name and vector dimension label every vector. If it does not, a deterministic local token-hash embedding keeps search available; disable this fallback with `AllowDeterministicEmbeddingFallback=false`. FTS retrieval remains available across model changes, while semantic comparison occurs only inside an identical provider/model/dimension space.

### Historical evidence

`/memory/upsert` retains the original request contract and accepts additional optional fields: `sourceUri`, `sourceTitle`, `author`, `validFrom`, `validTo`, `supersedesVersionId`, `claimKey`, and `statedConfidence`. Existing clients require no changes.

`/memory/query` additionally accepts `occurredFrom`, `occurredTo`, `validAt`, and `includeSuperseded`. Each result preserves the original `atom`, `score`, `textScore`, and `semanticScore` fields and adds:

- `citation`: stable version ID, label, source URI, occurrence date and content hash.
- `evidence`: status, calculated confidence, primary-source flag, supersession flag and contradicting version IDs.

Contradiction detection is conservative: two different contents are linked only when the caller assigns the same explicit `claimKey`. Confidence describes evidence quality and retrieval relevance; it is not a guarantee that a stored statement is true.

## Hermes installation

Publish the Bridge, then install the included skill into a new Hermes skill directory:

```powershell
dotnet publish src/HyperMemory.Bridge -c Release -o .\artifacts\bridge
.\src\HyperMemory.Bridge\hermes-skill\install.ps1 -PublishedBridgeDirectory .\artifacts\bridge
```

The installer refuses to overwrite an existing Hermes skill. The skill format follows Hermes Agent's official `SKILL.md` conventions and calls the API through the Bridge, so Hermes source code is unchanged.

## Operational limits

Vector search is exact and streams every compatible historical vector while retaining a bounded top-candidate heap. This favors complete recall and embedded reliability over approximate-index speed. For very large datasets requiring lower latency, implement an ANN-backed `IMemoryStore` adapter without changing Core, API, or Bridge.

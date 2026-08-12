# Controlled external graph import

HyperMemory can import a Graphify `graph.json` without making Graphify a runtime dependency and without allowing an external graph to modify derived knowledge tables directly. The immutable `memory_atoms` table remains the only source of truth; imported nodes and edges become append-only source records and the native knowledge projector derives the searchable graph from them.

## Safety contract

- Import is preview-only unless `commit` is explicitly `true`.
- `sourceName` and an absolute `file`, `http`, or `https` `sourceUri` are mandatory. Embedded URI credentials are rejected.
- A canonical SHA-256 is calculated from validated, sorted nodes and edges. Passing it back as `expectedSha256` prevents committing a graph different from the preview.
- Event identifiers derive from the source hash and graph identifiers. Repeating the same import is idempotent and reports existing records rather than duplicating them.
- Duplicate node IDs, dangling edges, invalid confidence classes, rooted/traversing source paths, excessive graph sizes, and unsupported hyperedges reject the complete import before any write.
- Duplicate binary edges are ignored with an explicit warning.
- Imported evidence retains Graphify's `EXTRACTED`, `INFERRED`, or `AMBIGUOUS` class. It is never upgraded to `VERIFIED` merely because it was imported.
- Raw graph data never writes directly to `knowledge_entities` or `knowledge_edges`. Those tables remain disposable and rebuildable.

## Supported schema

The format name is `graphify-networkx-v1`. It accepts NetworkX node-link JSON with `nodes` and `links` (`edges` is accepted for newer NetworkX exports). Nodes require string `id` and `label`; edges require string `source`, `target`, `relation`, and `confidence`.

Default limits are 100,000 nodes and 250,000 binary edges. They can be changed through `HyperMemory:ExternalGraphImportMaxNodes` and `HyperMemory:ExternalGraphImportMaxEdges`. The local HTTP server's request-size limit is an additional protective boundary.

## Two-step API use

First send the graph with `commit: false` to `POST /memory/import/graph`. Inspect `valid`, `problems`, `warnings`, counts, and `sourceSha256`. Then send the same request with `commit: true` and `expectedSha256` equal to the preview hash.

Each committed source record contains the import policy, source URI, source name, format, and canonical hash in its immutable metadata. Normal integrity, diagnostics, and knowledge-projection endpoints cover the imported records.

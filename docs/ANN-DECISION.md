# ANN decision record

## Decision

HyperMemory keeps FTS5 as the complete historical index and bounded semantic reranking as the default offline retrieval path. At 100,000 memories it must report `ann_evaluation_recommended`; adding an approximate-nearest-neighbour (ANN) engine is deferred until a candidate demonstrates a measurable recall/latency benefit without weakening offline operation, deterministic installation or uninstall safety.

## Recorded evidence

The reproducible `synthetic-multi-year` profile in `artifacts/evaluation/scale-100k.json` recorded on 2026-08-12:

- 100,000 immutable memory events and 100,000 completed projections;
- Recall@5 of 1.0, with all five chronological anchors ranked first;
- retrieval latency p50 51.70 ms and p95 71.34 ms;
- 656,465,920-byte SQLite database and 181,012,232-byte WAL before checkpoint;
- complete full-text coverage and valid event-chain integrity;
- bounded semantic coverage of 5% with the current 5,000-item window.

This proves that exact historical retrieval remains healthy at 100,000 memories, while the 5% semantic coverage is the measured reason to evaluate ANN for fuzzy, paraphrased recall at the next scale stage.

## Acceptance gate for an ANN backend

An ANN backend may become the default only after the same frozen corpus and query set shows no regression in exact Recall@5, improved paraphrase recall, bounded p95 latency, acceptable disk growth, deterministic rebuild from memory atoms, no network dependency, and complete removal without affecting Hermes or the primary historical store.

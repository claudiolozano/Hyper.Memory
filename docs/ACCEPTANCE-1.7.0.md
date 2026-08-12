# HyperMemory 1.7.0 acceptance record

Recorded locally on 2026-08-12 against the installed Hermes Desktop/TUI environment and the `E:\Hyper_Memory` store.

## Upgrade and integrity

- Upgraded an active HyperMemory 1.3.6 installation to 1.7.0.
- Counts before upgrade: 57 atoms, 57 vectors and 57 audit entries.
- Counts after upgrade: 57 atoms, 57 vectors and 57 audit entries.
- The installed 1.7.0 health endpoint reported `healthy` and full integrity.
- The migration backup contained 14 SHA-256-verified files and a complete SQLite/WAL snapshot.

## TUI and Desktop recall/capture

- Stored the unique marker `acceptance-1-7-69b0ce30d5bf` as an automatic Hermes turn.
- A new Hermes TUI one-shot session returned that exact marker; the store then grew from 58 to 59 atoms and the durable outbox was empty.
- A new Hermes Desktop session independently returned the exact same marker; the store then grew from 59 to 60 atoms.
- The installed plugin identified itself as 1.7.0 and Hermes configuration reported `memory.provider=hypermemory`.

## Session deletion

- Deleted only the test Desktop session `20260812_163232_b8d92f` through the official Hermes session command.
- HyperMemory still contained 60 atoms, integrity remained valid, and the marker was still returned as the top result.

## Uninstall and reinstall

- Silent uninstall with the default preservation policy stopped the owned API, removed only the owned Hermes Skill and plugin, and unset the owned memory provider.
- The historical SQLite database and immutable event directory remained present.
- Reinstalling 1.7.0 reattached the Skill/plugin and provider and returned to `healthy` with all 60 atoms and valid integrity.

## Automated gates

- 26 .NET tests passed, including operation with knowledge projection disabled.
- 17 Python provider tests passed.
- The 100,000-memory profile achieved Recall@5 1.0, p95 71.34 ms, complete FTS coverage and valid integrity. See `artifacts/evaluation/scale-100k.json` when generated locally and [the ANN decision record](ANN-DECISION.md).

The optional one-million-memory soak and full evaluator-supplied LoCoMo/LongMemEval datasets remain separate scale/research runs; they are not required by the mandatory installer release gate.

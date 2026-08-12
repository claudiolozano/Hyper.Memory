# Privacy and retention

HyperMemory is designed for durable historical recall, so its default retention is indefinite. It does not silently expire, summarize away, or delete older memories. Privacy controls operate before immutable storage whenever possible.

## Default protections

- Hermes automatically captures completed primary-agent turns, but recognizes direct phrases such as `no guardes esto en la memoria` or `do not save this` and does not persist that turn.
- The local connection policy can set `CaptureEnabled` to `false`. The installer enables capture and user opt-out by default.
- Secret redaction runs in both the Hermes provider and the central API. This defense in depth covers direct API clients as well as normal Hermes use.
- Detectors cover labelled passwords/tokens/secrets, bearer authorization values, common provider token prefixes, JWTs, private-key blocks, credentials embedded in HTTP(S) URLs, and payment-card candidates that pass the Luhn checksum.
- Stored metadata records redaction count, classification, capture policy, retention policy, and central enforcement version. The secret value itself is never written intentionally.

## Retention policy

The current policy is `indefinite-until-explicit-user-action`. Automated age-based deletion is intentionally absent because it would undermine historical memory and could destroy evidence without informed consent. The current safe uninstaller removes HyperMemory from Hermes but preserves the historical `Hyper_Memory` folder as a backup; it does not yet offer physical erasure.

HyperMemory does not claim that pattern detection can recognize every possible secret. Users should still avoid placing raw credentials in conversations. Future physical erasure controls must use an authenticated, previewable operation with an export/backup option and exact scope reporting; they must not be implemented as a broad or silent cleanup.

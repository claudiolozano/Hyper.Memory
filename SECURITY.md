# Security policy

## Supported version

Only the latest published release receives security fixes.

## Reporting a vulnerability

Do not disclose exploitable vulnerabilities in a public issue. Use GitHub's **Report a vulnerability** private reporting feature in the repository Security tab. Include affected version, reproduction steps, impact, and any suggested mitigation.

Maintainers will acknowledge a complete report within seven days, investigate it, and coordinate disclosure after a fix is available. Never include real credentials, personal memories, or a user's `Hyper_Memory` data in a report.

## Security model

HyperMemory listens only on loopback by default, stores state under the user-selected `Hyper_Memory` directory, and installs a separately owned Hermes Skill. Historical memory is append-only. Uninstallation removes only verified integration files and preserves historical data.

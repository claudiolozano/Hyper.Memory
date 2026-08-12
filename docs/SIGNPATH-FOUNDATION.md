# SignPath Foundation onboarding

HyperMemory uses SignPath Foundation's free code-signing program for eligible open-source projects.

Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).

## Eligibility mapping

- License: MIT, an OSI-approved license.
- Source: all project-owned code is public; no proprietary components are included.
- Build: official binaries are produced only by GitHub-hosted Actions runners.
- Security: `SECURITY.md` provides private vulnerability reporting instructions.
- Maintenance: issues and pull requests are accepted publicly.
- Release: the project publishes its documented Windows installer from the signed workflow artifact.

## Application

After the public repository and an initial release exist, submit the application at [signpath.org](https://signpath.org/). Provide:

- public repository URL;
- public download/release URL;
- project description from `README.md`;
- MIT license URL;
- security policy URL;
- workflow path `.github/workflows/signpath-release.yml`.

Install the SignPath GitHub App when requested and grant access only to this repository.

## Values supplied after approval

Configure these GitHub repository variables exactly as issued by SignPath:

- `SIGNPATH_ORGANIZATION_ID`
- `SIGNPATH_PROJECT_SLUG`
- `SIGNPATH_SIGNING_POLICY_SLUG`
- `SIGNPATH_RUNTIME_CONFIGURATION_SLUG`
- `SIGNPATH_INSTALLER_CONFIGURATION_SLUG`

Configure `SIGNPATH_API_TOKEN` as an Actions secret. Never place it in source, workflow variables, build logs, issues, or releases.

Create two artifact configurations in SignPath using `.signpath/runtime-artifacts.xml` and `.signpath/installer.xml`. Then run the **SignPath release** workflow manually with the release version.

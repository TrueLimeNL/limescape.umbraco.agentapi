# Contributing

Use .NET 10 and Umbraco 17 LTS. Keep dependency versions in Directory.Packages.props.
Build with warnings as errors, run the AgentApi test project, and pack before a PR.
See README.md for the commands and repository extraction prerequisites.

Keep all code, defaults and labels generic. Tenant-specific values belong in
configuration. Add direct API tests for protective rules. Content tools must not
be added until delegated authentication, role checks and recorded human approval
are implemented. User-facing labels must support Dutch and English.

Submit focused pull requests with behavior, tests and limitations. Contributors
license their contributions to TrueLime B.V. as set out in article 9 of the LOPL (see LICENSE).

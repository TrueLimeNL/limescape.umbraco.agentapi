# Security policy

## Supported versions

The latest minor version of the current major version receives security fixes.

## Reporting a vulnerability

Do not open a public issue for security problems. Use GitHub's private vulnerability reporting ("Report a vulnerability" on the Security tab of the repository) and include:

- the affected version and Umbraco version,
- the configuration involved and a minimal reproduction,
- the impact you expect (for example: a call accepted without a valid person token, a material visible to someone without rights, a write without an audit entry).

Do not include tokens, tenant documents or personal data in a report.

We acknowledge reports within five working days and aim to release a fix within 30 days for confirmed issues, coordinating disclosure with the reporter.

## Scope

In scope: accepting a forged, expired or replayed person token; a delegated token of an issuer that is not configured as trusted; reading or changing a material without the rights of the signed-in person; leaking keys, titles or files of materials the caller may not see; forged or replayed upload-session signatures and signed download links; a write that is not attributed to the acting person in the audit log.

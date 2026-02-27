# Security Policy

This repository contains the **KeelMatrix.Telemetry** .NET library and its related telemetry endpoint.

## Reporting a Vulnerability

Do **not** open a public issue for security vulnerabilities.

Report privately via:

- GitHub **Security Advisory** (preferred), or  
- Email: **keelmatrix@gmail.com**

Please include a clear description, reproduction steps, affected versions, and impact details if known.

We will review and coordinate a responsible fix and disclosure.

---

## Supported Versions

Security fixes are provided for the latest released version.  
Older versions may not receive patches.

---

## Scope

Security reports may include:

- Local filesystem handling (queue, markers, salt file)
- Project identity computation
- Serialization and payload validation
- HTTPS transmission logic
- Opt-out enforcement

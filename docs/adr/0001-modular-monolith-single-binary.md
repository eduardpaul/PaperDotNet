# ADR-0001: Modular monolith in one binary, with admin CLI

**Status:** Accepted (2026-09-24)

## Context
Self-hosting and practicality come first. Operators should run one app
container plus PostgreSQL. We still want clear module boundaries so the
product can grow (and extensions can plug in the same way).

## Decision
- One ASP.NET Core host (`paperdotnet`) composes modules that implement `IModule`.
  Each module has its own DbContext and schema, and a small `*.Contracts` project
  when other modules need it.
- The same executable runs admin commands: `paperdotnet migrate | bootstrap |
  tenant … | user … | healthcheck`. Without arguments it serves the API.
- Module boundaries are checked by architecture tests (assembly references and
  source scans).

## Consequences
- Operators get one image for API and administration; the container health
  check needs no curl.
- A separate CLI project (as sketched in the technical approach) is not needed.

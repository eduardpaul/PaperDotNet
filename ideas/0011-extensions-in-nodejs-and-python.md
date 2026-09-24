# 0011: Extensions implemented in Node.js and Python

- **Status:** new
- **Area:** Extensions
- **Date:** 2026-09-24
- **Mapped to:**

## The idea

Developers should be able to write extensions in Node.js (JavaScript/TypeScript)
and Python, not only in .NET. A Node or Python extension can use the same
extension points as a .NET one wherever possible: event handlers, field types,
file processors, automation activities, API endpoints, jobs and MCP tools.

## Why / problem it solves

- Opens the ecosystem to far more developers.
- Python has the best libraries for OCR, AI/ML, document classification and
  data processing.
- Node.js is the natural choice for web developers and integrations.

## Examples / references

- Architecture vision section 3.3:
  - tier 1: in-process .NET, decided first (decision 3)
  - tier 2: remote extensions in any language via webhooks and the REST API
    (phase 6)
- Generated SDKs for JS/TS and Python (idea 0003) can be the client side of
  the extension SDK.
- Similar models:
  - VS Code extension host (separate process, RPC)
  - MCP servers (stdio/HTTP JSON-RPC, idea 0004)
  - GitHub Apps / Slack apps (webhooks + API)
  - Dapr sidecars

## Notes

<!-- Open questions to settle during review:
     - How Node/Python code runs:
         a. Remote: the extension is a separate service; the host calls it
            over HTTP webhooks. It works with any language, but the developer
            has to host it.
         b. Managed sidecar: the host starts and supervises the Node/Python
            process per extension (or per tenant) and talks to it over
            gRPC or JSON-RPC (stdio/socket). Installs like a normal
            extension and stays close to in-process.
         c. Embedded runtimes: e.g. Jint/ClearScript for JS, pythonnet for
            Python. Fast, but fragile and hard to sandbox (not recommended
            as the main path).
     - One language-neutral extension protocol (manifest + RPC contract),
       with the .NET in-process tier as a fast path. Define it early so
       decision 3 (in-process first) doesn't lock the design to .NET.
     - Before/after handlers must work the same as for .NET extensions
       (idea 0012). Synchronous "before" handlers need low latency, so use
       sidecar RPC with short timeouts and a fail-open/fail-closed policy.
     - Packaging: the manifest declares "runtime": "dotnet" | "node" | "python",
       the entry point and dependencies (npm / pip). Should the host run
       npm/pip install, or require prebuilt container images?
     - Isolation and security:
         - a process or container per extension
         - resource limits
         - scoped tokens per tenant (idea 0005)
         - the extension only sees the permissions it declares
     - Official SDKs: `@paperdotnet/extension-sdk` (npm) and
       `paperdotnet-extension-sdk` (PyPI), with decorators and helpers for
       each extension point, and a local dev/test harness.
     - Roadmap: move part of phase 6 (remote extensions) earlier? -->

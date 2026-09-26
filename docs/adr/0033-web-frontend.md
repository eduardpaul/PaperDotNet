# ADR-0033: Web frontend on the TypeScript SDK with React, TanStack and Tailwind

- **Status:** Accepted
- **Date:** 2026-09-26

## Context

The backend covers phases P0–P7 and the TypeScript SDK was made complete
enough for a first-party client (ADR-0032). The user asked for the web UI
next, built on **the SDK + React + TanStack + Tailwind**, covering the
catalogued use cases and being modern and easy to use.

Constraints from the guiding principle:
- **Minimal install stays one container:** the UI must ship inside the
  PaperDotNet image and work without extra configuration.
- **No hand-rolled security or protocol code:** sign-in uses the SDK's
  `OAuthSession` (oauth4webapi); accessible widgets come from a library.
- **Licenses:** MIT/Apache-2.0, BSD/ISC with notice.

## Decision

**1. Stack** (all MIT unless noted):

| Concern | Choice |
|---|---|
| API access | `@paperdotnet/client` (this repo, Apache-2.0), the generated Kiota client + runtime. The UI never calls `fetch` for the API directly, except through the SDK's `client.fetch` for binaries. |
| Build | Vite, React 19, TypeScript (strict) |
| Routing | TanStack Router, file-based routes, typed path and search params (filters, sort and tabs live in the URL) |
| Server state | TanStack Query: one query-key factory, invalidation from live events (`/v1.0/me/events`) |
| Tables and long lists | TanStack Table, TanStack Virtual |
| Styling | Tailwind CSS v4 with design tokens as CSS variables (light and dark) |
| Widgets | shadcn/ui-style components kept in the repo, on Radix UI primitives (accessible focus, keyboard and ARIA handling) |
| Icons, toasts, command palette | lucide-react (ISC), sonner, cmdk |
| Dates and numbers | `Intl` with the user's preferences (PLT-17); date-fns for calendar math |
| Tests | Vitest for units; Playwright (Apache-2.0) end-to-end against the real host, started like the SDK's end-to-end tests |
| Quality gates | `tsc --noEmit`, ESLint (typescript-eslint, react-hooks), Prettier; warnings fail the build |

Later slices add, when they need them: dnd-kit (drag and drop), react-markdown +
remark-gfm (notes), TanStack Form or plain controlled forms for item editors.
Each is checked and recorded in `docs/dependency-licenses.md` when added.

**2. Layout.** The app lives in `web/`. An npm workspace at the repository root
(`sdk/typescript`, `web`) gives one lockfile and lets the UI use the SDK as a
package, exactly as an outside frontend would.

**3. Hosting.**
- **Production:** the Docker build compiles the UI and copies it into the
  host's `wwwroot`. The host serves it from the same origin as the API, with
  an `index.html` fallback for client routes, excluding `/v1.0`, `/connect`,
  `/.well-known`, `/health`, `/openapi` and `/version`. Hashed assets are
  cached for a long time; `index.html` is not cached.
- **Sign-in:** when the UI is present, `Auth:LoginUrl` defaults to `/login`, so
  `/connect/authorize` sends signed-out users to the UI's sign-in page. The
  operator lists the public callback (`https://dms.example.com/callback`) in
  `Auth:FirstPartyRedirectUris`.
- **Development:** the Vite dev server proxies `/v1.0`, `/connect` and
  `/.well-known` to a local host, so the browser sees one origin.
- **No UI:** a host without `wwwroot/index.html` (API-only builds, tests)
  behaves as before.

**4. UX principles** (details in [frontend.md](../frontend.md)):
- **Short path to everyday tasks:** Home shows today's work, and the Inbox is
  a triage queue.
- **Keyboard first:** a command palette (⌘K / Ctrl+K) for navigation, search
  and actions.
- **Live:** processing status, notifications and changes by others arrive
  through server-sent events.
- **Honest concurrency:** edits send `If-Match`. A 412 offers to reload or to
  compare, never to overwrite silently.
- **Accessible and responsive:** the app works from phone width upwards and
  meets WCAG 2.2 AA contrast.
- **Permission-aware:** the UI shows actions by scope and access level, and the
  server stays the authority.

**5. Extension UI later.** Navigation entries, item panels and field editors
come from registries in the app (`web/src/extensibility`). Built-in features
use them too, so extension bundles (architecture vision §3) can plug in later
without restructuring.

## Consequences

- The UI is another SDK consumer, so missing API pieces show up as SDK gaps.
  They are fixed in the API and regenerated, never worked around in the UI.
- The Docker build gains a Node stage; the runtime image stays .NET only.
- The .NET test suite is unaffected. UI tests need Node and run on demand and
  in CI, like the SDK's end-to-end tests.
- Owning the component code (shadcn/ui style) means more files in the repo,
  but no dependency on a component library's release cycle or styling.

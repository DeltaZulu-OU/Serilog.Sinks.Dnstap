# Development roadmap

Work proceeds on `main`, one focused commit per change. Each phase requires passing Linux, Windows, and macOS CI before it is marked complete. No release is published until the final release gates pass.

Status: **Phases 1 and 2 complete** (file safety and deferred TCP configuration, including cross-platform CI). Next: Phase 3.

| Phase | Scope | Exit criteria |
| --- | --- | --- |
| 1 | File safety and lifecycle | No implicit overwrite; explicit replacement; ownership, shutdown, and creation errors tested |
| 2 | TCP configuration API | Fluent configuration with deferred connection, bounded connect/handshake, terminal errors, no implicit reconnection |
| 3 | Configuration consistency | Shared naming and validation, native Serilog `BatchingOptions`, automatic replay prohibited |
| 4 | Declarative configuration | File and TCP support through `Serilog.Settings.Configuration` tested from `appsettings.json` |
| 5 | Diagnostics and lifecycle hardening | Clear failure reporting, record accounting, concurrent disposal and stalled-I/O tests |
| 6 | Independent interoperability and package readiness | External DNSTap receiver tests, consumer sample, package restore, symbols and source metadata |
| 7 | Performance and resilience | File and TCP throughput, allocations, backpressure, and slow-reader measurements |
| 8 | Release package workflow | Validated artifact published and independently verified, mirroring `Dnstap.NET` |

## Phase 8: release package workflow

Follow [Dnstap.NET's release workflow](https://github.com/DeltaZulu-OU/Dnstap.NET/blob/main/.github/workflows/publish.yml), adapting package ID, assembly, dependency mapping, and licensing to `Serilog.Sinks.Dnstap`.

- Release is **manually dispatched from main**, with `contents: write` and `packages: write` only in the publishing job; reject other refs. Verify SemVer strictly, compare all existing `v*` tags, and permit rerunning a release only if its tag points at the same commit.
- Build and test a release candidate on CI, including configuration-binding, independent file/TCP interoperability and a clean consumer restore. Verify the `.nupkg` ID, version, Apache-2.0 license, `net10.0` assembly, README, dependencies (including the GPL-licensed `Dnstap.NET`), and source commit metadata.
- Persist the **tested** package and a single-entry SHA-256 manifest as CI artifacts. The publish job downloads and verifies that exact artifact; it must never rebuild an untested substitute.
- Publish to `https://nuget.pkg.github.com/DeltaZulu-OU/index.json` with an authorized token; skip duplicates only when a clean registry download subsequently proves that the published package has identical bytes.
- Verify a fresh registry-backed consumer restore and execution, in an isolated NuGet cache, and compare published bytes against the tested candidate.
- Create or verify an annotated `vVERSION` tag at the tested commit. Create or reconcile the GitHub Release containing the exact `.nupkg` and `SHA256SUMS`, then download both assets and reverify digests.
- Detect and reject conflicting existing tags, packages, releases, and release assets; distinguish missing assets from failed API lookups. Include script-level tests for version comparison, package metadata, and release artifact reconciliation.

No automatic release on push or tag creation. Preserve the existing Apache-2.0 source license and document that `Dnstap.NET` remains GPL-3.0-only. Release version and package naming are managed for this repository independently of `Dnstap.NET`.

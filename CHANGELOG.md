# Changelog

All notable changes to msg2eml. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/). Each `vX.Y.Z` tag produces a
[GitHub release](https://github.com/carminezac/msg2eml/releases) with the
Docker image and self-contained binaries.

## [Unreleased]

### Planned

- v0.4: native window for the desktop binary with Photino.NET (system webview),
  browser app window kept as fallback.
- v0.5: batch upload with ZIP output in the web service.
- Manual round-trip verification in Outlook and Thunderbird (`docs/04-round-trip-checklist.md`).

## [0.3.0] — 2026-10-11

### Added

- `msg2eml-desktop`: self-contained window launcher. Runs the web app on
  `127.0.0.1` on a port chosen by the OS, opens it in a browser application
  window (Edge, Chrome, Chromium, Brave, default browser otherwise) and exits
  when the window is closed. No firewall prompt, no port clash, no console
  window on Windows.
- `msg2eml-cli`: command-line converter for files and directories
  (`--recursive`, `--force`, `--stdout`, `--max-depth`, `--max-bytes`,
  exit codes 0/1/2).
- `msg2eml-server`: the web service as a self-contained Linux binary, with a
  hardened systemd unit (`deploy/msg2eml.service`).
- Prometheus metrics on `GET /metrics`, built on `System.Diagnostics.Metrics`
  with an in-process exporter: conversions by result and error code, duration
  and size histograms, in-progress gauge, process and GC gauges.
- GitHub Actions pipeline: tests on every push, multi-arch image
  (`linux/amd64`, `linux/arm64`) on GHCR tagged by branch, SHA and semver,
  GitHub release on `v*` tags with binaries for Linux (glibc and musl),
  Windows and macOS, Portainer stack files, systemd unit and `SHA256SUMS`.
  The tag must match `<Version>` in `Directory.Build.props`.
- Portainer stack files: direct port (`deploy/portainer-stack.yml`) and behind
  Traefik with `/metrics` off the public route (`deploy/portainer-stack.traefik.yml`).
- Assembly version shown at startup and in the page footer; request scopes in
  JSON logs.
- Round-trip checklist for Outlook and Thunderbird (`docs/04-round-trip-checklist.md`).

### Changed

- The web application moved from `Program.cs` to `WebApp.Create()` in the
  `Msg2Eml.Web` class library, shared by the server entry point and the
  desktop launcher.
- Invariant globalization: binaries no longer depend on the system ICU library.
- Debug symbols embedded in the assemblies: no loose `.pdb` files, line
  numbers kept in stack traces.
- Outside a container the server listens on `http://localhost:8080` by
  default; `--urls http://0.0.0.0:8080` exposes it on the LAN.

### Fixed

- The golden test theory failed on CI, where the local corpus does not exist
  (xUnit treats a theory without data rows as a failure).

## [0.2.0] — 2026-10-11

### Added

- Thirteen synthetic `.msg` fixtures generated with MsgKit by
  `tools/gen-fixtures`, one per risk case of the plan, committed under
  `tests/Msg2Eml.Tests/Fixtures/`.
- Golden tests against the Outlook Web export of the same mail
  (`samples/X.msg` + `samples/X.ref.eml`, local corpus only) and a C# port of
  the fidelity comparison (`EmlFidelity`).
- Health check running a real micro-conversion of an embedded synthetic message.

### Fixed

- Original `Content-Type` and `Content-Language` of the transport headers were
  lost: MimeKit routes `Content-*` headers to the body entity, not to the message.
- An inline-flagged image whose `cid` is not referenced in the HTML was hidden
  inside `multipart/related`; it is now a regular attachment.
- A `.msg` attached as a plain file came out as `application/octet-stream`.
- Pure-ASCII text parts lacked an explicit `Content-Transfer-Encoding`.

## [0.1.0] — 2026-10-10

### Added

- Conversion core on MsgReader and MimeKit: original transport headers as the
  base, MAPI properties as fallback, body rebuilt with inline images,
  attachments, nested `.msg`, RTF-only and opaque S/MIME policies, nesting and
  size limits, re-parse self-check.
- Minimal API `POST /api/convert`, upload page with drag and drop, `GET /health`.
- Structured JSON logs that never carry message content.
- Multi-stage Dockerfile on .NET 10 and hardened compose stack (non-root,
  read-only filesystem, no capabilities, memory and pid limits).
- CLI probe and fidelity comparison script from the validation spike
  (`tools/oneshot-msgconv`).

[Unreleased]: https://github.com/carminezac/msg2eml/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/carminezac/msg2eml/releases/tag/v0.3.0
[0.2.0]: https://github.com/carminezac/msg2eml/commits/master
[0.1.0]: https://github.com/carminezac/msg2eml/commits/master

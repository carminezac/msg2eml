# msg2eml

Conversion of Outlook `.msg` files to `.eml`, self-hosted, .NET 10 +
MsgReader + MimeKit stack. WebUI, HTTP API, hardened Docker container.

Status: **v0.1, minimal service** (see [`action-plan.md`](action-plan.md),
section 15). The conversion logic was first validated with a spike on a
real message, comparing the output with the `.eml` export from Outlook Web:
original headers, visible text, inline images and `cid:` references identical,
valid MIME for an independent parser. Details in [`docs/03-quality-analysis.md`](docs/03-quality-analysis.md).

## Getting started

Docker only. The image is built by GitHub Actions for amd64 and arm64 and
published on GitHub Container Registry:

```bash
docker run -d --name msg2eml -p 8080:8080 --read-only --tmpfs /tmp:size=64m \
  --cap-drop ALL --security-opt no-new-privileges:true --memory 1g \
  ghcr.io/carminezac/msg2eml:latest
# WebUI:   http://localhost:8080/
# API:     POST http://localhost:8080/api/convert   (multipart, field `file`)
# Health:  GET  http://localhost:8080/health
# Metrics: GET  http://localhost:8080/metrics      (Prometheus text format)
```

To build from source instead: `docker compose up -d --build`.

```bash
curl -f -o mail.eml -F "file=@mail.msg" http://localhost:8080/api/convert
```

The response is `message/rfc822` with `Content-Disposition: attachment` and the
input file name with the `.eml` extension. Errors are JSON `{code, message}`:

| HTTP | `code` | When |
| --- | --- | --- |
| 400 | `InvalidInput` | `file` field missing or extension other than `.msg` |
| 413 | `FileTooLarge` | over `MaxUploadBytes`, or over `MaxDecodedBytes` once decoded |
| 422 | `InvalidMsgFile` | not an OLE container, or MsgReader cannot open it |
| 422 | `UnsupportedMessageType` | appointment, task, contact, note |
| 422 | `NestingTooDeep` | nested `.msg` beyond `MaxNestingDepth` |
| 500 | `MimeGenerationError`, `ConversionError`, `AttachmentReadError` | internal error |

Error messages, logs and metric labels never contain subjects, addresses,
attachment names or contents.

### Portainer

Stacks → Add stack → Web editor, paste one of:

- [`deploy/portainer-stack.yml`](deploy/portainer-stack.yml): port 8080 on the LAN, hardened, health check, log rotation;
- [`deploy/portainer-stack.traefik.yml`](deploy/portainer-stack.traefik.yml): behind Traefik with TLS, no published port, `/metrics` kept off the public route, rate limiting on.

Pin a version tag (`ghcr.io/carminezac/msg2eml:0.3.0`) instead of `latest` for
reproducible deploys. Tags are produced for every push to `master` (`latest`,
short SHA) and for every `v*` git tag (`0.3.0`, `0.3`). A `v*` tag also
creates the GitHub release with the binaries, the stack files, the systemd unit
and `SHA256SUMS`; the tag must match `<Version>` in `Directory.Build.props` or
the pipeline fails.

### Without Docker

Every release on GitHub ships self-contained binaries: no .NET runtime, no
Docker, nothing to install. On Linux they need only glibc and libstdc++
(`linux-musl` builds for Alpine).

- **msg2eml-desktop**: a window with the converter. Double-click it: the
  program starts on `127.0.0.1` on a free port, opens the page in an
  application window of the browser already on the machine (Edge, Chrome,
  Chromium, Brave; default browser otherwise) and exits when the window is
  closed. Nothing listens on the network, so Windows never asks to unblock the
  firewall, and no fixed port can clash with other software.
- **msg2eml-cli**: command line, for batches and scripts.

  ```bash
  msg2eml mail.msg                   # writes mail.eml next to it
  msg2eml inbox/ -r -o converted/    # every .msg under inbox/, recursively
  msg2eml mail.msg --stdout | less   # EML on standard output
  msg2eml --help                     # options, exit codes
  ```

- **msg2eml-server**: the web service for Linux hosts without Docker. Listens on
  `http://localhost:8080`; add `--urls http://0.0.0.0:8080` to expose it on the
  LAN. [`deploy/msg2eml.service`](deploy/msg2eml.service) is a hardened systemd
  unit that mirrors the container settings.

Configuration is the same everywhere (`Msg2Eml__*` environment variables, or
`appsettings.json` next to the executable). Verify downloads with
`sha256sum -c SHA256SUMS`.

### Observability

Structured JSON logs on stdout with the events `conversion_started`,
`conversion_completed`, `invalid_msg`, `conversion_failed` and
`recipient_unresolved`, each carrying only sizes, durations, counters and
error codes. `GET /metrics` exposes, in Prometheus format and without any
third-party package: conversions by result and error code, duration and size
histograms, in-progress gauge, resident memory and GC gauges.

## Configuration

Environment variables `Msg2Eml__<Name>` (see `docker-compose.yml`):

| Name | Default | Meaning |
| --- | --- | --- |
| `MaxUploadBytes` | 52428800 (50 MB) | maximum size of the `.msg` |
| `MaxDecodedBytes` | 268435456 (256 MB) | maximum total size of decoded bodies and attachments |
| `MaxNestingDepth` | 5 | maximum depth of nested `.msg` |
| `MaxConcurrentConversions` | 4 | simultaneous conversions; the others wait |
| `RateLimitPerMinute` | 0 (off) | requests per minute per IP |
| `JsonLogs` | true | JSON logs on stdout, otherwise text |
| `MetricsEnabled` | true | Prometheus metrics on `GET /metrics` |

The container runs non-root, with a read-only filesystem, `cap_drop: ALL`,
`no-new-privileges`, `mem_limit` and `pids_limit`. The conversion takes place
entirely in memory: no temporary files, no persistence.
Size `mem_limit` as `MaxConcurrentConversions` × ~4 × `MaxUploadBytes`.

## How it converts

1. If Outlook preserved the original transport headers, the EML starts from
   those (Received, DKIM, ARC, Message-ID, Thread-Index, X-*), removing only
   the MIME framing of the old body. MAPI properties fill in only what is
   missing. Without transport headers (drafts, Sent Items) the MAPI fallback is used.
2. Body rebuilt with MimeKit: `text/plain`, `text/html`, `cid:` images
   as `LinkedResources` only if referenced in the HTML, attachments, nested `.msg`
   as `message/rfc822`, RTF-only preserved as a `body.rtf` attachment,
   opaque S/MIME returned with a byte-identical `smime.p7m` as the body.
3. `Prepare(SevenBit)` for the `Content-Transfer-Encoding`, CRLF, re-reading
   of the produced EML as a self-check.

Requirements and policies: action plan, section 3.

## Development and testing

No .NET on the host: everything runs in the SDK image.

```bash
./tools/dev.sh test        # 64 tests: core, fixtures, golden, API, CLI, desktop
./tools/dev.sh build       # msg2eml:local image
./tools/dev.sh up | down   # docker compose
```

**Synthetic fixtures.** `tests/Msg2Eml.Tests/Fixtures/*.msg` are generated by
`tools/gen-fixtures` with [MsgKit](https://github.com/Sicos1977/MsgKit) from
invented data, one per risk case: plain text without transport headers, native
HTML with transport headers, inline images (referenced and not), attachments
(PDF, Unicode name, no extension, empty), Unicode subject and addresses,
Exchange recipient without SMTP, HTML only inside RTF, RTF-only, a `.msg`
attached as a file, appointment, task, contact, and the message used by the
health check. Regenerate after changing a case:

```bash
./tools/dev.sh sdk dotnet run --project tools/gen-fixtures -- tests/Msg2Eml.Tests/Fixtures
```

**Golden tests.** For every `samples/X.msg` with a `samples/X.ref.eml` next to
it (the Outlook Web export of the same mail), the produced EML must match the
reference on main headers, visible text, attachment hashes and CID resolution.
`samples/` is not committed: it contains third-party correspondence. Without it
the golden tests pass vacuously.

The same comparison is available as a standalone script:

```bash
./tools/oneshot-msgconv/compare-eml.py outlook-web-reference.eml produced.eml
```

Full procedure in [`docs/02-playbook.md`](docs/02-playbook.md).

## Layout

```text
msg2eml/
├── src/
│   ├── Msg2Eml.Core/             MsgConverter, errors, limits
│   ├── Msg2Eml.Web/              Razor class library: WebApp, API, page, health check, metrics
│   ├── Msg2Eml.Server/           server entry point (Docker, systemd)
│   ├── Msg2Eml.Desktop/          window launcher: loopback port + browser app window
│   └── Msg2Eml.Cli/              command-line converter
├── tests/Msg2Eml.Tests/          xunit: core, fixtures, golden, API, CLI, desktop
│   └── Fixtures/                 synthetic .msg files (generated, committed)
├── tools/
│   ├── dev.sh                    test/build/up without .NET on the host
│   ├── gen-fixtures/             MsgKit generator of the synthetic fixtures
│   └── oneshot-msgconv/          spike CLI probe + compare-eml.py
├── deploy/                       Portainer stack files (direct port, Traefik), systemd unit
├── docs/                         journal, playbook, quality analysis, round-trip checklist
├── .github/workflows/ci.yml      tests, multi-arch image on GHCR, release with binaries
├── samples/  out/                real corpus, not committed
├── Dockerfile  docker-compose.yml  Msg2Eml.slnx  Directory.Build.props
└── action-plan.md
```

## Next steps

Manual round-trip in Outlook and Thunderbird on a real corpus, following
[`docs/04-round-trip-checklist.md`](docs/04-round-trip-checklist.md). Then v0.4
of the plan: batch upload with ZIP output. Still not coverable with synthetic
fixtures: embedded `.msg` and S/MIME, exercised only when a real sample is
available.

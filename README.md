# msg2eml

Conversion of Outlook `.msg` files to `.eml`, self-hosted, .NET 10 +
MsgReader + MimeKit stack. WebUI, HTTP API, hardened Docker container.

Status: **v0.1, minimal service** (see [`action-plan.md`](action-plan.md),
section 15). The conversion logic was first validated with a spike on a
real message, comparing the output with the `.eml` export from Outlook Web:
original headers, visible text, inline images and `cid:` references identical,
valid MIME for an independent parser. Details in [`docs/03-quality-analysis.md`](docs/03-quality-analysis.md).

## Getting started

Only Docker is required.

```bash
docker compose up -d --build
# WebUI:  http://localhost:8080/
# API:    POST http://localhost:8080/api/convert   (multipart, field `file`)
# Health: GET  http://localhost:8080/health
```

```bash
curl -f -o mail.eml -F "file=@mail.msg" http://localhost:8080/api/convert
```

The response is `message/rfc822` with `Content-Disposition: attachment` and the
input file name with the `.eml` extension. Errors are JSON `{code, message}`:

| HTTP | `code` | When |
| --- | --- | --- |
| 400 | `InvalidInput` | `file` field missing or extension other than `.msg` |
| 413 | `FileTooLarge` | over `MaxUploadBytes` or over `MaxDecodedBytes` decoded |
| 422 | `InvalidMsgFile` | not an OLE container or MsgReader cannot open it |
| 422 | `UnsupportedMessageType` | appointment, task, contact, note |
| 422 | `NestingTooDeep` | nested `.msg` beyond `MaxNestingDepth` |
| 500 | `MimeGenerationError`, `ConversionError`, `AttachmentReadError` | internal error |

Error messages and logs never contain subjects, addresses, names or
attachment contents.

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
./tools/dev.sh test        # 18 tests: core, limits, API, page
./tools/dev.sh build       # msg2eml:local image
./tools/dev.sh up | down   # docker compose
```

Tests that use real messages look for `.msg` files in `samples/` (or in
`MSG2EML_SAMPLES`) and skip themselves if the directory is empty. `samples/` and
`out/` contain third-party correspondence and must not be committed (`.gitignore`).

To measure the fidelity of a conversion against the Outlook Web export:

```bash
./tools/oneshot-msgconv/compare-eml.py outlook-web-reference.eml produced.eml
```

Full procedure in [`docs/02-playbook.md`](docs/02-playbook.md).

## Layout

```text
msg2eml/
├── src/
│   ├── Msg2Eml.Core/             MsgConverter, errors, limits
│   └── Msg2Eml.Web/              Minimal API, Razor Page, healthcheck
├── tests/Msg2Eml.Tests/          xunit: core, limits, API
├── tools/
│   ├── dev.sh                    test/build/up without .NET on the host
│   └── oneshot-msgconv/          spike CLI probe + compare-eml.py
├── docs/                         journal, playbook, quality analysis
├── samples/  out/                real corpus, not committed
├── Dockerfile  docker-compose.yml  Msg2Eml.slnx
└── action-plan.md
```

## Next steps

v0.2 of the plan: corpus of synthetic fixtures with golden tests against Outlook Web,
edge cases (no transport headers, `EX` recipients, RTF-only, S/MIME,
nested, corrupted), manual round-trip in Outlook and Thunderbird.

# MSG to EML Converter — Action Plan

## Goal

Build a self-hosted tool to convert Outlook `.msg` files to `.eml`, with a WebUI, HTTP API, Docker container and deployment via Portainer.

Stack:

- .NET 10 LTS
- ASP.NET Core
- MsgReader 6.1.1+
- MimeKit 4.17.0+
- Razor Pages + HTMX
- Docker
- Portainer

## Status

- 2026-09-07: plan revised after a **validation spike** of the stack on a real message (`docs/01-spike-log.md`, `docs/03-quality-analysis.md`). Stack confirmed; `tools/oneshot-msgconv/Program.cs` contains the probe's conversion logic.
- 2026-10-12: **v0.1 completed**. Service in `src/` (Core + Web), 18 tests in `tests/`, Dockerfile and hardened compose, verified in the container: health OK, conversion via API identical to the probe's, comparison with the Outlook Web export unchanged.
- 2026-10-12: **v0.2 completed**. 13 synthetic fixtures generated with MsgKit (`tools/gen-fixtures`), 45 tests including golden tests against the Outlook Web reference and the health check running a real micro-conversion. The fixtures exposed four defects in the v0.1 converter, all fixed (see below). Next step: v0.3.

Defects found by the synthetic fixtures, all in code that the single real sample could not reach:

- MimeKit routes the `Content-*` headers of the loaded transport block to the body entity, not to the message: the removal of the old MIME framing was a no-op (harmless, the body is replaced anyway) but the original `Content-Type`, needed for S/MIME detection, was always read as null and `Content-Language` was silently lost;
- an inline-flagged image whose `cid` is not referenced in the HTML ended up inside `multipart/related`, invisible in most clients; it is now a regular attachment;
- a `.msg` attached as a plain file came out as `application/octet-stream` because MsgReader guesses that type for unknown extensions and the extension table was never consulted;
- pure-ASCII text parts had no explicit `Content-Transfer-Encoding` after `Prepare` (implicit 7bit, valid but inconsistent); it is now explicit.

Limits of the synthetic corpus: MsgKit cannot write embedded `.msg` attachments nor set the S/MIME message class, so nested messages and S/MIME remain covered only by real samples.

Notes that emerged during v0.1, to keep in mind for future porting:

- the code page provider must be registered **at process startup**, not in a static constructor: the healthcheck caught a container that answered `Unhealthy` until a conversion had touched the converter;
- `.dockerignore` must exclude `bin/` and `obj/`, otherwise the local restore (done in the SDK container with the NuGet cache mounted) pollutes the image build;
- in .NET 10 `System.Text.Encoding.CodePages` is part of the framework: no package to reference;
- the UI uses a few lines of inline JavaScript (XHR for progress and blob download) instead of HTMX: downloading a file from an HTTP response is not a case HTMX handles well, and the container has no egress to load libraries from a CDN.

---

## 1. Guiding principle

> Preserve the original content as much as possible, avoiding unnecessary transformations.

Corollary that emerged from the spike: when Outlook has preserved the original transport headers (`TransportMessageHeaders`), maximum fidelity is obtained by **starting from those** and rebuilding only the body from the MAPI properties. The MAPI properties are the fallback for messages that have no transport headers (drafts, Sent Items, locally created items).

This preserves Received, DKIM, ARC, Message-ID, Thread-Index, In-Reply-To, References and all `X-*` headers: essential information for threading and for the archival/evidentiary use of the produced EMLs.

---

## 2. Application architecture

```text
.msg
 ↓
MsgReader (Storage.Message)
 ↓
ConversionService
 ├── headers:     TransportMessageHeaders → MimeMessage.Load, removal of body headers only
 │                (fallback: From/To/Cc/Bcc/Subject/Date/Message-ID from MAPI)
 ├── body:        BodyBuilder (text/plain, text/html, LinkedResources, Attachments)
 └── attachments: inline if the cid is referenced in the HTML, otherwise attachment; nested .msg → message/rfc822
 ↓
Prepare(SevenBit) + WriteTo (CRLF)
 ↓
self-check: re-read of the produced EML
 ↓
.eml
```

### Internal model

**Not** a model with enumerated fields (Subject, Sender, To, …): it would be lossy and would contradict the guiding principle. The internal model is:

- `RawTransportHeaders` (string, may be absent)
- `TextBody`, `HtmlBody`, `RtfBody`
- `Attachments`: for each one `FileName`, `Data`, `MimeType`, `ContentId`, `IsInline`, `Hidden`, or a nested message
- MAPI fallback fields: `Subject`, `Sender`, `SenderRepresenting`, `Recipients` (with `AddressType` SMTP/EX), `SentOn`, `ReceivedOn`, `Type`

The dependency on MsgReader is not abstracted behind a replaceable interface: the conversion strategy depends on MsgReader-specific APIs (`TransportMessageHeaders`, RTF de-encapsulation) and there is no realistic alternative to swap in. Instead, the **conversion logic** is isolated in a testable class that receives a `Stream` and returns a `MimeMessage`.

---

## 3. Non-negotiable technical requirements

These emerged from the spike; each one corresponds to an error that would recur.

1. **`Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` at startup**, with the `System.Text.Encoding.CodePages` package. Without it, in a Linux container, every message with an RTF-encapsulated body fails with `'Windows-1252' is not a supported encoding name`.
2. **Headers loaded as a message**, not copied into a `new MimeMessage()`: MimeKit pre-creates an empty `From` and clients show a missing sender. In the fallback without transport headers, explicitly remove the empty `From`.
3. **`mime.Prepare(EncodingConstraint.SevenBit)` before serialization**: MimeKit does not assign a `Content-Transfer-Encoding` on its own, and 8-bit text without a CTE is non-compliant and can be corrupted in transit.
4. **`NewLineFormat.Dos`** for CRLF and `International = false`.
5. **Re-read of the produced EML** with `MimeMessage.Load` as a post-conversion self-check: if it cannot be reopened, the conversion has failed.
6. **Depth limit for nested `.msg`** (for example 5 levels) and a limit on the total decoded size: unbounded recursion is a stack overflow vector on malicious input.
7. **Minimum versions**: MsgReader 6.1.1, MimeKit 4.17.0 (4.9.0 is flagged by `NU1902` / `GHSA-g7hc-96xr-gvvx`). Enable `NuGetAudit` in the build.
8. **Date**: in the fallback use `SentOn`, then `ReceivedOn`, then `CreationTime`. Never `DateTimeOffset.Now`: for an archive it is false data. If none is available, omit the header.
9. **`Content-Language`** is a message header, not a body header: it must be preserved. Remove only `Content-Type`, `Content-Transfer-Encoding`, `MIME-Version`, `Content-Disposition`, `Content-ID`, `Content-Description`, `Content-Location`, `Content-MD5`, `Content-Length`.

### Policies to make explicit

- **X500/`EX`-only recipients without SMTP**: they only surface in the MAPI fallback. Policy: preserve the display name with a documented placeholder address (`unknown@invalid`), and report it in the structured log as `recipient_unresolved` (without the name). To be revisited if frequent cases emerge.
- **RTF-only**: if neither text nor HTML exists, preserve the RTF as an `application/rtf` attachment with a courtesy `text/plain`. In a later phase, evaluate RTF → text conversion.
- **Non-mail types** (`IPM.Appointment`, `IPM.Task`, `IPM.Contact`, `IPM.StickyNote`, `IPM.Schedule.*`): response `422 UnsupportedMessageType`. Do not attempt best-effort conversions in the MVP.
- **S/MIME** (`IPM.Note.SMIME`, `smime.p7m` attachment): preserve the `p7m` as an `application/pkcs7-mime` part with the original headers. Encrypted content is not convertible; signed content remains verifiable only if the `p7m` is byte-identical.

---

## 4. Conversion fidelity

Cases to cover in the test corpus, in order of risk:

- **HTML only inside RTF** (de-encapsulation): the most common case in internal Outlook mail and the one at highest risk of loss. It is the case of the spike sample.
- messages **without transport headers** (drafts, Sent Items): the MAPI fallback becomes the main path
- `EX`-only recipients without SMTP
- native HTML (`PR_HTML`)
- pure plain text
- inline images via CID, including those marked `Hidden`
- multiple non-inline attachments
- Unicode file names
- dates and time zones (in the passthrough the original time zone is in the `Date` header; in the fallback `SentOn` is UTC)
- embedded `.msg`, including multiple levels
- RTF-only messages
- attachments without a MIME type
- signed and encrypted S/MIME
- non-mail types
- very large messages (> 25 MB)
- corrupted or truncated files
- `.eml` file renamed `.msg` and vice versa

---

## 5. Error handling

Explicit application errors:

```text
InvalidMsgFile            not a valid OLE/CFBF container
FileTooLarge
UnsupportedMessageType    appointment, task, contact, …
NestingTooDeep            nested .msg beyond the limit
AttachmentReadError
MimeGenerationError       Prepare/WriteTo or self-check failed
ConversionError           everything else
```

HTTP mapping:

```text
400  invalid input (extension, malformed multipart)
413  payload too large
422  InvalidMsgFile / UnsupportedMessageType / NestingTooDeep
500  MimeGenerationError / ConversionError
```

In the error response body: application code and a generic message. Never the subject or other message data.

---

## 6. Security

The conversion takes place **entirely in memory**: no temporary files, hence no path sanitization, random directories or cleanup. Still required:

- upload limit of 25-50 MB (Kestrel's `MaxRequestBodySize` and `MultipartBodyLengthLimit`)
- streaming of the multipart directly into memory, without buffering to disk
- sanitization of the **output file name** for the `Content-Disposition`
- depth and decoded-size limits for nested `.msg`
- concurrency limit tied to the container's memory limit (see section 8)
- **no network egress**: the container has no reason to go out. In compose, `internal: true` network or no network other than the one towards the reverse proxy. The probe already uses `--network none`.
- non-root container
- read-only filesystem
- Docker capabilities dropped
- `no-new-privileges`
- no persistent storage
- rate limiting if the service is reachable from outside the LAN

Indicative Docker configuration:

```yaml
user: "10001:10001"
read_only: true
security_opt:
  - no-new-privileges:true
cap_drop:
  - ALL
tmpfs:
  - /tmp:size=64m
mem_limit: 1g
environment:
  - DOTNET_EnableDiagnostics=0
  - ASPNETCORE_HTTP_PORTS=8080
networks:
  - proxy          # only towards Traefik; no network with egress
```

With a read-only filesystem, ASP.NET Core Data Protection warns that it cannot persist keys: this service does not need them, disable it or point it to `/tmp`.

---

## 7. WebUI

The initial UI stays very simple.

```text
MSG → EML Converter

[ Drop .msg here ]

or

[ Select file ]

[ Convert ]
```

Features: drag & drop, upload progress, readable errors, automatic download. No SPA. Razor Pages + HTMX.

---

## 8. Batch conversion

Second phase: multiple upload, parallel conversion, ZIP output.

```text
file1.msg
file2.msg
file3.msg
   ↓
conversion
   ↓
converted.zip
```

Concurrency limit sized on memory: MsgReader loads the file into RAM and every attachment becomes a `byte[]`, so one conversion can take up three or four times the size of the `.msg`. With 50 MB uploads, four simultaneous conversions require about 1 GB: set `mem_limit` accordingly or reduce concurrency. In the ZIP, every failed file is replaced by a `name.error.txt` with the error code, so the batch does not fail because of a single file.

---

## 9. Testing

### Fixtures

The fixtures in the repository are **synthetic or anonymized**: generated from a test mailbox or scrubbed of names, addresses and contents. The real corpus used for validation (`samples/`, `out/`) contains third-party correspondence and **stays out of version control** (`.gitignore` at the root).

```text
tests/
└── Fixtures/
    ├── rtf-encapsulated-html.msg   + .ref.eml
    ├── native-html.msg             + .ref.eml
    ├── plain-text.msg              + .ref.eml
    ├── no-transport-headers.msg
    ├── ex-recipients.msg
    ├── inline-images.msg           + .ref.eml
    ├── attachments.msg             + .ref.eml
    ├── unicode-filenames.msg       + .ref.eml
    ├── nested.msg                  + .ref.eml
    ├── rtf-only.msg
    ├── smime-signed.msg
    ├── appointment.msg
    ├── truncated.msg
    └── not-a-msg.msg
```

### Golden tests

For every fixture with a `.ref.eml`, the reference is the export of the same mail from Outlook Web. The test compares product and reference the way `tools/oneshot-msgconv/compare-eml.py` does, which is to be ported into the test project as the base:

| Check | Expected |
| --- | --- |
| `From` / `To` / `Cc` / `Subject` / `Date` / `Message-ID` | identical |
| visible text of `text/plain` and `text/html` | identical |
| attachments and images | identical `sha256` |
| `cid:` referenced in the HTML | all resolved |
| independent MIME parser (Python's `email` or MimeKit in strict mode) | no defects |
| 8-bit text parts | `Content-Transfer-Encoding` present |

Acceptable: different but equivalent `alternative`/`related` structure, whitespace, extra `Return-Path`, `<meta http-equiv>` injected by the reference client.

### Other tests

- unit tests of the conversion logic on in-memory `Stream`
- limit tests: size, nesting depth, unsupported type, corrupted file
- HTTP tests: code mapping, `Content-Disposition`, no sensitive data in the error body
- manual round-trip with Outlook and Thunderbird on the real corpus: no script replaces it

---

## 10. Docker

Multi-stage build, as `tools/oneshot-msgconv/Dockerfile` already does:

```text
mcr.microsoft.com/dotnet/sdk:10.0
        ↓
      build
        ↓
mcr.microsoft.com/dotnet/aspnet:10.0
        ↓
runtime container, non-root user
```

Healthcheck on `GET /health`, invoked by the Docker `HEALTHCHECK` via `dotnet Msg2Eml.Web.dll --healthcheck` (the runtime image has no curl). It runs a real micro-conversion of a synthetic `.msg` embedded in the assembly (`src/Msg2Eml.Web/Resources/health.msg`, generated by `tools/gen-fixtures`), which exercises MsgReader, MimeKit and the encoding provider exactly as a request would.

---

## 11. Portainer

Single-container stack.

```text
Portainer
   │
   ▼
msg2eml
ASP.NET Core :8080
   │
   ├── WebUI
   └── API
```

With HTTPS:

```text
LAN
 ↓
Traefik
 ↓
msg2eml   (internal network, no egress)
```

---

## 12. HTTP API

```http
POST /api/convert
Content-Type: multipart/form-data
```

Response:

```http
200 OK
Content-Type: message/rfc822
Content-Disposition: attachment; filename="input-name.eml"; filename*=UTF-8''input-name.eml
```

The name derives from the uploaded file name, sanitized, with the extension replaced. `filename*` for non-ASCII names.

Batch phase:

```http
POST /api/convert/batch   → 200 application/zip
```

---

## 13. Observability

Structured logs. Events:

```text
conversion_started      { input_size, has_transport_headers }
conversion_completed    { duration_ms, output_size, attachments, inline, nested_depth }
recipient_unresolved    { address_type }
invalid_msg             { error_code }
conversion_failed       { error_code, exception_type }
```

Never log: subject, sender, recipients, body, attachment names, attachment contents. The detailed report printed by the probe does **not** belong in the service.

Metrics: `conversion_duration`, `input_size`, `conversion_success`, `conversion_failure` per error code.

---

## 14. Implementation sequence

The correctness of the conversion was verified before the MVP, through the spike. Order from here:

```text
spike (done)
 ↓
minimal service around the probe's logic
 ↓
corpus + golden tests
 ↓
Docker hardening + Portainer
 ↓
batch
 ↓
monitoring
```

---

## 15. Milestones

### v0.1 — minimal service (completed 2026-10-12)

- [x] porting of `Program.cs` into `Msg2Eml.Core` with the requirements of section 3
- [x] `POST /api/convert`, `GET /health`
- [x] Razor Page with single upload and download
- [x] multi-stage Dockerfile, hardened compose
- [x] errors mapped as in section 5
- [x] basic tests (core, limits, API) and `tools/dev.sh` to work without .NET on the host

### v0.2 — corpus and tests (completed 2026-10-12)

- [x] synthetic fixtures from section 9, generated with MsgKit by `tools/gen-fixtures`
- [x] golden tests with Outlook Web reference (`samples/X.msg` + `samples/X.ref.eml`, local only)
- [x] edge cases: no transport headers, `EX`, RTF-encapsulated HTML, RTF-only, non-mail types, truncated
- [x] healthcheck with micro-conversion (moved up from v0.3)
- [ ] S/MIME and embedded `.msg`: not producible with MsgKit, covered only by real samples
- [ ] manual Outlook and Thunderbird round-trip on a real corpus of 10-20 messages (moved to v0.3)

### v0.3 — deployment

- Portainer stack, optional Traefik
- structured logs and metrics
- manual Outlook and Thunderbird round-trip on a real corpus of 10-20 messages

### v0.4 — batch

- multiple upload, ZIP
- limited concurrency and consistent `mem_limit`

### v1.0

- deployment and API documentation
- Outlook/Thunderbird compatibility verified on the full corpus

---

## 16. Repository layout

```text
msg2eml/
├── src/
│   ├── Msg2Eml.Core/        conversion: MsgReader + MimeKit, errors, limits
│   └── Msg2Eml.Web/         Minimal API, Razor Pages, HTMX, healthcheck
├── tests/
│   └── Msg2Eml.Tests/       unit + golden + HTTP
├── tools/oneshot-msgconv/   probe (kept as a manual verification tool)
├── docs/
├── samples/                 real corpus, NOT committed
├── out/                     probe output, NOT committed
├── .gitignore
├── Dockerfile
├── docker-compose.yml
├── Msg2Eml.sln
└── README.md
```

Two projects instead of three: the conversion logic is a few hundred lines and a separate `Infrastructure` layer adds nothing (see section 2).

---

## 17. Final stack

```text
.NET 10 LTS
ASP.NET Core
MsgReader 6.1.1+
MimeKit 4.17.0+
System.Text.Encoding.CodePages
Razor Pages + HTMX
Docker
Portainer
Traefik optional
```

```text
┌───────────────────────────────────┐
│ ASP.NET Core                      │
│                                   │
│  Razor / HTMX WebUI               │
│           │                       │
│           ▼                       │
│      Minimal API                  │
│           │                       │
│           ▼                       │
│      ConversionService            │
│      MsgReader → MimeKit          │
│      header passthrough +         │
│      rebuilt body                 │
│           │                       │
│           ▼                       │
│   Prepare + self-check → .eml     │
└───────────────────────────────────┘
```

---

## Conclusion

The .NET + ASP.NET Core + MsgReader + MimeKit stack has been **verified on a real, non-trivial case** (HTML only inside RTF, Exchange sender, signature with inline images): original headers, visible text, attachments and inline references preserved, valid MIME for an independent parser. The validity limit is a single message: the v0.2 corpus is the step that makes the converter reliable.

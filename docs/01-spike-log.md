# Action log — MSG → EML conversion spike

Date: 2026-09-07
Goal: understand the real quality of the `.msg` → `.eml` conversion with the
stack proposed in the plan (.NET + MsgReader + MimeKit) **before** writing the
service. Constraint: everything runs via throwaway Docker, no .NET toolchain
installed on the host.

Test file: `samples/<supplier-message>.msg` (157 KB, `CDFV2 Microsoft Outlook Message`).

---

## 1. Checking the prerequisites

- `docker --version` → 29.3.0, server running.
- `file` on the `.msg` → OLE/CFBF container, so a real `.msg` and not a renamed
  `.eml`.

## 2. Discovering the MsgReader API instead of guessing it

Reasoning: writing the converter from memory means iterating on compilation
errors, and every iteration inside Docker costs a NuGet restore. Cheaper to
start with a first program that does **reflection** on the assembly and prints
the types, properties and methods of `MsgReader.Outlook`.

Useful result:

- `Storage.Message`: `Subject`, `Sender`, `SenderRepresenting`, `Recipients`,
  `BodyText`, `BodyHtml`, `BodyRtf`, `Attachments`, `SentOn`, `ReceivedOn`,
  `Type` and — above all — **`TransportMessageHeaders`**, i.e. the original
  SMTP headers of the message.
- `Storage.Attachment`: `FileName`, `Data`, `MimeType`, `ContentId`, `IsInline`,
  `Hidden`, `RenderingPosition`.
- `Storage.Recipient`: `Email`, `DisplayName`, `Type`, `AddressType` (`SMTP` /
  `EX`).

`TransportMessageHeaders` changed the design of the converter: when Outlook
has kept the original headers, maximum fidelity is obtained by **starting from
those** and rebuilding only the body from the MAPI properties. The MAPI
properties become the fallback for messages without transport headers
(drafts, items created locally).

A mounted directory was used for the package cache, so subsequent restores
are immediate.

## 3. First converter

Choices:

- headers: full copy of the transport headers, removing only the MIME framing
  of the old body (`Content-Type`, `Content-Transfer-Encoding`,
  `MIME-Version`, `Content-Disposition`, …), which will be regenerated;
- body: MimeKit's `BodyBuilder`, which builds the correct
  `alternative` / `related` / `mixed` hierarchy by itself;
- inline: an attachment becomes a `LinkedResource` if it has a `ContentId`
  actually referenced as `cid:` in the HTML, otherwise a regular attachment;
- nested `.msg`: recursive conversion into `message/rfc822`;
- RTF-only: if neither text nor HTML exists, the RTF is kept as an
  `application/rtf` attachment instead of being thrown away;
- missing MIME type: inferred from the extension, with
  `application/octet-stream` as fallback.

The program also prints a report (MAPI source, MIME structure produced,
headers, `cid` check) and **re-reads the EML just written**: if MimeKit can
reopen it, the file is at least well formed.

## 4. Error: Windows-1252 encoding missing

```text
System.ArgumentException: 'Windows-1252' is not a supported encoding name
   at MsgReader.Rtf.Font..cctor()
   at MsgReader.Rtf.Document.DeEncapsulateHtmlFromRtf(String rtf)
```

Cause: .NET Core on Linux includes only the base encodings; the Windows code
pages require the dedicated provider. Fix:

```csharp
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
```

with `System.Text.Encoding.CodePages` as a dependency. **This must go into the
final service**: without the provider, in a Linux container, every message with
an RTF-encapsulated body fails.

The stack trace also revealed an important fact: in this message `BodyHtml`
does **not** come from a native HTML property, but from the de-encapsulation
of the RTF. This is the hardest case for fidelity.

## 5. Error: empty `From:` at the top of the EML

First version: `new MimeMessage()` and then copy of the headers. Result:
`MimeMessage` pre-creates an empty `From` header, and clients read the
**first** `From` — hence an empty sender. Duplicated `Date`, `Subject` and
`Message-Id` also showed up (the generated ones and the originals).

Structural fix: do not copy the headers into a new message, but
**load the original headers as a message** (`MimeMessage.Load` on the header
block terminated by an empty line) and remove only the body headers from it.
Order, values and uniqueness of the headers remain the original ones; the MAPI
properties fill in only what is missing.

## 6. Comparison with Outlook Web

The directory contained the `.eml` export of the same mail made by Outlook
Web: an independent reference, far more conclusive than eyeballing.
`compare-eml.py` was written (stdlib only: the `email` module also acts as a
**MIME validator independent** of MimeKit).

The comparison immediately showed a real defect in our output:

```text
text/plain  CTE=None   non-ascii-bytes=29
text/html   CTE=None   non-ascii-bytes=29
```

No `Content-Transfer-Encoding` on parts that contain 8-bit bytes (Italian
accented characters, `€`). Without the header, `7bit` applies: non-compliant
message, and accented characters get corrupted when passing through a
non-`8BITMIME` MTA. Outlook Web uses `base64` on both parts.

Cause: MimeKit does not choose the encoding by itself at write time. Fix:

```csharp
mime.Prepare(EncodingConstraint.SevenBit);
```

which assigns each part a valid encoding (`quoted-printable` for text,
`base64` for binaries). `NewLineFormat.Dos` was also added for CRLF.

## 7. Trying MsgReader 6.1.1

5.6.0 was the version the API had been mapped with; 6.1.1 is the latest. The
signatures used are identical and the conversion produces the same HTML, so
**we start from 6.1.1**. MimeKit bumped to 4.17.0 because 4.9.0 is flagged
with a moderate-severity vulnerability (`NU1902` / `GHSA-g7hc-96xr-gvvx`).

## 8. Outcome of the final comparison

- main headers: identical to the reference;
- visible text of `text/plain` and `text/html`: identical;
- 3 inline images: identical `sha256`;
- 3 `cid:` references: all resolved;
- no MIME defects for either file;
- only item flagged: `Return-Path`, present in ours and removed by Outlook Web
  (higher fidelity, not a defect).

Non-substantial differences are documented in `03-quality-analysis.md`.

## 9. Reorganisation

The probe was moved from a temporary script to `tools/oneshot-msgconv/` with a
multi-stage build and a **hardened** container (`--read-only`, `--network none`,
`--cap-drop ALL`, `no-new-privileges`, `--tmpfs /tmp`, non-root user,
input mounted read-only). The same measures planned in point 5 of the plan, so
they are verified from the start.

`--network none` is also a substantial guarantee: the conversion cannot
exfiltrate anything, useful when handling company correspondence.

## 10. What to carry into the service

1. `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` at startup.
2. Headers rebuilt from `TransportMessageHeaders`, MAPI only as fallback.
3. `Prepare(EncodingConstraint.SevenBit)` before serialising.
4. `LinkedResources` only for `cid:` actually referenced in the HTML.
5. RTF-only fallback and MIME type inferred from the extension.
6. Recursion on nested `.msg`.
7. Re-reading the produced EML as a post-conversion self-check.
8. `compare-eml.py` as the basis for the integration tests (point 8 of the plan).

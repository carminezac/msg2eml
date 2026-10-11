# Round-trip checklist — Outlook and Thunderbird

Manual verification of produced EML files in real mail clients (plan, section 9
and milestone v0.3). No script replaces this step: it is the only check that
the files *render* correctly, not just that they are valid MIME.

## Corpus

Use 10-20 real messages that together cover: HTML with inline images, plain
text, multiple attachments, Unicode subject and file names, a forwarded message
as an Outlook item (embedded `.msg`), an S/MIME signed message, one from Sent
Items or Drafts (no transport headers), one very large (> 25 MB).

Convert each one with the service (`POST /api/convert`) or the probe
(`tools/oneshot-msgconv/run.sh`). Keep the files out of the repository.

## Per message

Open the `.eml` in **Thunderbird** (File → Open → Saved Message, or
`thunderbird file.eml`) and in **Outlook** (double-click the `.eml`, or drag it
into a folder). For each client, tick:

| # | Check | Outlook | Thunderbird |
| --- | --- | --- | --- |
| 1 | Sender name and address as in the original | | |
| 2 | All To / Cc recipients, no `unknown@invalid` unless expected | | |
| 3 | Subject identical, accents and symbols intact | | |
| 4 | Date and time identical (watch the time zone) | | |
| 5 | HTML body renders like the original, no missing formatting | | |
| 6 | Inline images display in place, no broken image icons | | |
| 7 | Attachments listed with the right names and sizes | | |
| 8 | Attachments open correctly (PDF, Office, images) | | |
| 9 | Unicode file names not mangled | | |
| 10 | Embedded `.msg` appears as a nested message and opens | | |
| 11 | Reply / forward works and quotes the body correctly | | |
| 12 | Message threads with the original (same Message-ID) | | |
| 13 | S/MIME: signature shown as valid, encrypted body decrypts | | |
| 14 | No security warning about malformed content | | |

## Known, accepted differences

- MIME structure differs from the Outlook Web export (`alternative` >
  `related` instead of `related` > `alternative`): both render identically.
- Plain text alternative may differ by whitespace from the Outlook Web export
  (continuation-line spaces normalized by MsgReader).
- `Return-Path` is kept; Outlook Web drops it.
- Sent Items and Drafts have no transport headers: `Message-ID` is generated
  (`@msg2eml.generated`) and threading with the original is not expected.

## Recording the outcome

Note the client versions and the date, and record any failed check as an
issue with the fixture category (not the message content). If a failure can be
reproduced with a synthetic fixture, add the case to `tools/gen-fixtures` and a
test to `SyntheticFixtureTests`.

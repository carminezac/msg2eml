#!/usr/bin/env python3
"""Compares two .eml files (expected vs produced) and reports fidelity differences.

    ./tools/oneshot-msgconv/compare-eml.py <reference.eml> <produced.eml>

Uses only the stdlib: the `email` parser acts as an independent validator with respect
to MimeKit, so an EML that opens here is well-formed MIME.
"""
import email
import email.policy
import hashlib
import html as html_mod
import re
import sys
import difflib


def load(path):
    with open(path, "rb") as fh:
        return email.message_from_binary_file(fh, policy=email.policy.default)


def tree(part, lvl=0):
    bits = []
    if part.get_filename():
        bits.append("name=" + part.get_filename())
    if part.get("content-id"):
        bits.append("cid=" + part["content-id"])
    if part.get("content-transfer-encoding"):
        bits.append("cte=" + part["content-transfer-encoding"])
    if part.get_content_charset():
        bits.append("charset=" + part.get_content_charset())
    if not part.is_multipart():
        bits.append("size=%d" % len(part.get_payload(decode=True) or b""))
    out = ["  " * lvl + part.get_content_type() + "  " + " ".join(bits)]
    if part.is_multipart():
        for sub in part.get_payload():
            out += tree(sub, lvl + 1)
    return out


def body(msg, ctype):
    for part in msg.walk():
        if part.get_content_type() == ctype:
            raw = part.get_payload(decode=True)
            cs = part.get_content_charset() or "utf-8"
            return raw.decode(cs, "replace").replace("\r\n", "\n").replace("\r", "\n")
    return None


def binaries(msg):
    out = {}
    for part in msg.walk():
        if part.get_content_maintype() in ("image", "application") or part.get_filename():
            payload = part.get_payload(decode=True)
            if payload is None:
                continue
            key = part.get_filename() or (part.get("content-id") or "?")
            out[key] = (len(payload), hashlib.sha256(payload).hexdigest()[:16])
    return out


squash = lambda s: re.sub(r"\s+", " ", s or "").strip()


def visible(s):
    s = re.sub(r"(?is)<(script|style)\b.*?</\1>", " ", s or "")
    s = re.sub(r"(?s)<!--.*?-->", " ", s)
    return squash(html_mod.unescape(re.sub(r"<[^>]+>", " ", s)))


def main(ref_path, new_path):
    ref, new = load(ref_path), load(new_path)
    fail = 0

    print("=" * 72)
    print("A (reference):", ref_path)
    print("B (produced)   :", new_path)

    print("\n-- main headers " + "-" * 56)
    for key in ("From", "To", "Cc", "Bcc", "Subject", "Date", "Message-ID",
                "Reply-To", "In-Reply-To", "References", "Return-Path", "Thread-Index"):
        a, b = squash(str(ref.get(key) or "")), squash(str(new.get(key) or ""))
        if not a and not b:
            continue
        mark = "OK " if a == b else "DIFF"
        if a != b:
            fail += 1
        print(f"  [{mark}] {key}")
        if a != b:
            print(f"         A: {a[:150] or '<missing>'}")
            print(f"         B: {b[:150] or '<missing>'}")
    print(f"  total headers: A={len(ref.items())}  B={len(new.items())}")

    print("\n-- MIME structure " + "-" * 54)
    print("  A:"); print("\n".join("    " + l for l in tree(ref)))
    print("  B:"); print("\n".join("    " + l for l in tree(new)))

    print("\n-- body " + "-" * 64)
    for ctype in ("text/plain", "text/html"):
        a, b = body(ref, ctype), body(new, ctype)
        if a is None and b is None:
            print(f"  [--  ] {ctype}: missing in both")
            continue
        if a is None or b is None:
            fail += 1
            print(f"  [DIFF] {ctype}: present only in {'A' if b is None else 'B'}")
            continue
        same_bytes = a == b
        same_squash = squash(a) == squash(b)
        same_vis = visible(a) == visible(b)
        print(f"  {ctype}: len A={len(a)} B={len(b)}  "
              f"identical={same_bytes}  ignoring-whitespace={same_squash}  visible-text={same_vis}")
        if not same_vis:
            fail += 1
            diff = [l for l in difflib.unified_diff(
                visible(a).split(". "), visible(b).split(". "), "A", "B", lineterm="")][2:]
            for line in diff[:20]:
                print("      " + line[:160])
        elif not same_bytes:
            diff = [l for l in difflib.unified_diff(a.splitlines(), b.splitlines(), "A", "B", lineterm="")
                    if l[:1] in "+-"][2:]
            print(f"      formatting/whitespace differences only: {len(diff)} lines")
            for line in diff[:6]:
                print("      " + line[:160])

    print("\n-- attachments and inline images " + "-" * 39)
    ba, bb = binaries(ref), binaries(new)
    for name in sorted(set(ba) | set(bb)):
        x, y = ba.get(name), bb.get(name)
        if x == y:
            print(f"  [OK  ] {name}  {x[0]} B  sha256:{x[1]}")
        else:
            fail += 1
            print(f"  [DIFF] {name}  A={x}  B={y}")

    print("\n-- MIME validation (stdlib parser) " + "-" * 37)
    for label, msg in (("A", ref), ("B", new)):
        defects = [d for p in msg.walk() for d in p.defects]
        print(f"  {label}: defects={defects or 'none'}")
        if defects:
            fail += 1
        for part in msg.walk():
            if part.get_content_maintype() == "text" and not part.get("content-transfer-encoding"):
                raw = part.get_payload(decode=True) or b""
                if any(byte > 127 for byte in raw):
                    fail += 1
                    print(f"     !! {label}: {part.get_content_type()} 8-bit without Content-Transfer-Encoding")

    # cids referenced in the HTML must exist as a part
    print("\n-- CID resolution " + "-" * 54)
    for label, msg in (("A", ref), ("B", new)):
        html = body(msg, "text/html") or ""
        cids = sorted(set(re.findall(r"cid:([^\"'\s>)]+)", html)))
        present = {(p.get("content-id") or "").strip("<>") for p in msg.walk()}
        for cid in cids:
            ok = cid in present
            if not ok:
                fail += 1
            print(f"  {label}: cid:{cid} -> {'resolved' if ok else 'MISSING'}")
        if not cids:
            print(f"  {label}: no cid in HTML")

    print("\n" + "=" * 72)
    print("RESULT:", "no substantial differences" if fail == 0 else f"{fail} differences to review")
    return 0 if fail == 0 else 1


if __name__ == "__main__":
    if len(sys.argv) != 3:
        print(__doc__)
        sys.exit(2)
    sys.exit(main(sys.argv[1], sys.argv[2]))

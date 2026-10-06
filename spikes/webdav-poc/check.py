#!/usr/bin/env python3
"""Drives the WebDAV PoC with Explorer/Office-like request sequences and prints PASS/FAIL per capability.

Usage: check.py <base-url> <ro|rw>     e.g. check.py http://127.0.0.1:5099 ro
Standard library only.
"""
import base64
import http.client
import json
import re
import sys
import time
import urllib.parse
import xml.etree.ElementTree as ET

BASE = sys.argv[1].rstrip("/")
MODE = sys.argv[2]
TOKEN = "pdn_poc_0123456789abcdef"
AUTH = "Basic " + base64.b64encode(f"anyone:{TOKEN}".encode()).decode()
D = "{DAV:}"
MS = "{urn:schemas-microsoft-com:}"
results = []


def request(method, path, body=None, headers=None, auth=True):
    url = urllib.parse.urlsplit(BASE)
    conn = http.client.HTTPConnection(url.hostname, url.port, timeout=60)
    h = {"User-Agent": "Microsoft-WebDAV-MiniRedir/10.0.22631"}
    if auth:
        h["Authorization"] = AUTH
    if body is not None and isinstance(body, str):
        body = body.encode()
        h.setdefault("Content-Type", "application/xml; charset=utf-8")
    h.update(headers or {})
    started = time.perf_counter()
    conn.request(method, path, body=body, headers=h)
    r = conn.getresponse()
    data = r.read()
    r.elapsed = time.perf_counter() - started
    r.data = data
    conn.close()
    return r


def check(name, ok, detail=""):
    results.append((name, bool(ok), detail))


def q(path):
    return urllib.parse.quote(path, safe="/")


def propfind(path, depth="1", props=None):
    body = None
    if props is not None:
        inner = "".join(props)
        body = f'<?xml version="1.0" encoding="utf-8"?><D:propfind xmlns:D="DAV:" xmlns:Z="urn:schemas-microsoft-com:"><D:prop>{inner}</D:prop></D:propfind>'
    r = request("PROPFIND", q(path), body, {"Depth": depth})
    entries = {}
    if r.status == 207:
        root = ET.fromstring(r.data)
        for resp in root.findall(f"{D}response"):
            href = resp.find(f"{D}href").text
            values = {}
            for ps in resp.findall(f"{D}propstat"):
                status = ps.find(f"{D}status").text
                for p in ps.find(f"{D}prop"):
                    values[p.tag] = (status, p)
            entries[href] = values
    return r, entries


def names(entries, base):
    out = []
    for href in entries:
        rel = urllib.parse.unquote(href)
        if rel.rstrip("/") == base.rstrip("/"):
            continue
        out.append(rel.rstrip("/").rsplit("/", 1)[-1])
    return out


def node(path):
    r = request("GET", "/poc/node?path=" + urllib.parse.quote(path), auth=False)
    return json.loads(r.data) if r.status == 200 else None


EXPLORER_PROPS = ["<D:creationdate/>", "<D:displayname/>", "<D:getcontentlength/>", "<D:getcontenttype/>", "<D:getetag/>",
                  "<D:getlastmodified/>", "<D:resourcetype/>", "<D:quota-available-bytes/>", "<D:quota-used-bytes/>",
                  "<Z:Win32FileAttributes/>", "<Z:Win32CreationTime/>", "<Z:Win32LastModifiedTime/>", "<Z:Win32LastAccessTime/>",
                  "<D:isreadonly/>", "<D:ishidden/>"]


def read_checks():
    r = request("OPTIONS", "/dav/", auth=False)
    check("OPTIONS /dav/ without credentials → 200", r.status == 200, f"{r.status}")
    dav = r.getheader("DAV")
    check(f"OPTIONS advertises DAV class ({'1' if MODE == 'ro' else '1, 2'}) via our gate", dav == ("1" if MODE == "ro" else "1, 2"), f"DAV: {dav}")
    check("OPTIONS sends MS-Author-Via: DAV via our gate", r.getheader("MS-Author-Via") == "DAV")
    r = request("OPTIONS", "/", auth=False)
    check("OPTIONS / (root probe) answered with DAV headers", r.status == 200 and r.getheader("DAV") is not None, f"{r.status} DAV={r.getheader('DAV')}")

    r = request("PROPFIND", "/dav/", None, {"Depth": "0"}, auth=False)
    check("PROPFIND without credentials → 401 + Basic challenge", r.status == 401 and "Basic" in (r.getheader("WWW-Authenticate") or ""),
          f"{r.status} {r.getheader('WWW-Authenticate')}")
    r = request("PROPFIND", "/dav/", None, {"Depth": "0", "Authorization": "Basic " + base64.b64encode(b"x:pdn_wrong").decode()}, auth=False)
    check("PROPFIND with a wrong token → 401", r.status == 401, f"{r.status}")
    r = request("GET", "/v1.0/me")
    check("Basic is ignored outside /dav (/v1.0/me → 401)", r.status == 401, f"{r.status}")

    r, e = propfind("/dav/", "1")
    check("PROPFIND /dav/ depth 1 → workspaces", r.status == 207 and sorted(names(e, "/dav/")) == ["Home", "Projects"], f"{r.status} {names(e, '/dav/')}")
    hrefs = list(e)
    check("hrefs include the /dav path base", all(h.startswith("/dav/") for h in hrefs), str(hrefs[:3]))
    check("collection hrefs end with '/'", all(h.endswith("/") for h in hrefs), str(hrefs[:3]))

    r, e = propfind("/dav/Projects/", "1")
    check("workspace lists only document libraries (no 'Tasks')", sorted(names(e, "/dav/Projects/")) == ["Archive", "Contracts"], str(names(e, "/dav/Projects/")))

    r, e = propfind("/dav/Projects/Contracts/", "1")
    listed = names(e, "/dav/Projects/Contracts/")
    expected = {"Invoice.pdf", "Invoice (2).pdf", "invoice (3).pdf", "Report_ Q1_2025_.pdf", "CON_.pdf", "Scan without extension.png",
                "Trailing dots.pdf", "Read only.pdf", "Large.bin", "Ä Umlaut & #hash 100% [x]", "2025", "Big"}
    check("library listing: names, duplicates, sanitizing, reserved names, hidden itemless files", set(listed) == expected,
          f"missing={sorted(expected - set(listed))} extra={sorted(set(listed) - expected)}")
    folder_href = next((h for h in e if "Umlaut" in urllib.parse.unquote(h)), None)
    check("special characters are percent-encoded in hrefs", folder_href is not None and "%23" in folder_href and "%25" in folder_href,
          str(folder_href))

    if folder_href:
        r2 = request("PROPFIND", folder_href, None, {"Depth": "1"})
        inner = r2.status == 207 and b"Inner.pdf" in r2.data
        check("PROPFIND on the href of a folder with Ä # % [ ] (round trip)", inner, f"{r2.status}")
        r3 = request("GET", folder_href.rstrip("/") + "/Inner.pdf")
        check("GET a file inside that folder", r3.status == 200 and r3.data.startswith(b"%PDF"), f"{r3.status}")

    r = request("PROPFIND", "/dav/Projects/Contracts/", None, {"Depth": "infinity"})
    check("Depth: infinity refused (403)", r.status == 403, f"{r.status}")

    r = request("PROPFIND", "/dav/Projects/Contracts/", None, {"Depth": "1"})
    check("allprop (empty body) works", r.status == 207, f"{r.status}")

    r, e = propfind("/dav/Projects/Contracts/", "1", EXPLORER_PROPS)
    check("Explorer-style PROPFIND (DAV: + Win32 props) → 207", r.status == 207, f"{r.status}")
    file_props = next((v for h, v in e.items() if h.endswith("/Invoice.pdf")), {})
    folder_props = next((v for h, v in e.items() if h.rstrip("/").endswith("/Contracts")), {})
    for prop in ["getcontentlength", "getcontenttype", "getetag", "getlastmodified", "creationdate", "displayname"]:
        status, el = file_props.get(D + prop, ("missing", None))
        check(f"file property {prop}", "200" in status and (el.text or ""), f"{status} {el.text if el is not None else ''}")
    status, el = file_props.get(MS + "Win32FileAttributes", ("missing", None))
    check("Win32FileAttributes reported (read-only 00000001 in ro mode)", "200" in status and el.text == ("00000001" if MODE == "ro" else "00000020"),
          f"{status} {el.text if el is not None else ''}")
    status, el = folder_props.get(D + "resourcetype", ("missing", None))
    check("resourcetype is <collection/> on folders", el is not None and el.find(f"{D}collection") is not None, status)
    status, el = folder_props.get(D + "quota-available-bytes", ("missing", None))
    check("quota-available-bytes on collections (drive size in Explorer)", "200" in status, status)
    status, _ = file_props.get(D + "isreadonly", ("missing", None))
    check("unknown requested props → 404 propstat, not an error", "404" in status, status)
    lm = file_props.get(D + "getlastmodified", ("", None))[1]
    check("getlastmodified is RFC 1123", lm is not None and re.match(r"^\w{3}, \d{2} \w{3} \d{4} \d{2}:\d{2}:\d{2} GMT$", lm.text or ""),
          lm.text if lm is not None else "")
    etag_prop = (file_props.get(D + "getetag", ("", None))[1].text or "") if file_props.get(D + "getetag") else ""

    r = request("GET", q("/dav/Projects/Contracts/Invoice.pdf"))
    check("GET file content", r.status == 200 and b"Invoice A" in r.data, f"{r.status}")
    check("GET Content-Type", r.getheader("Content-Type") == "application/pdf", r.getheader("Content-Type"))
    etag = r.getheader("ETag")
    check("GET ETag header is a quoted entity tag (RFC 9110)", etag is not None and etag.startswith('"') and etag.endswith('"'), str(etag))
    check("GET Last-Modified header", r.getheader("Last-Modified") is not None, str(r.getheader("Last-Modified")))
    r = request("GET", q("/dav/Projects/Contracts/Invoice (2).pdf"))
    check("GET a suffixed duplicate returns the second item", r.status == 200 and b"Invoice B" in r.data, f"{r.status}")
    r = request("GET", q("/dav/projects/contracts/invoice.pdf"))
    check("case-insensitive path resolution (Windows)", r.status == 200 and b"Invoice A" in r.data, f"{r.status}")

    r = request("HEAD", q("/dav/Projects/Contracts/Large.bin"))
    check("HEAD: Content-Length, no body", r.status == 200 and r.getheader("Content-Length") == str(3 * 1024 * 1024) and r.data == b"",
          f"{r.status} {r.getheader('Content-Length')}")
    full = request("GET", q("/dav/Projects/Contracts/Large.bin")).data
    r = request("GET", q("/dav/Projects/Contracts/Large.bin"), None, {"Range": "bytes=0-9"})
    check("Range bytes=0-9 → 206 with 10 bytes", r.status == 206 and r.data == full[0:10], f"{r.status} len={len(r.data)} Content-Range={r.getheader('Content-Range')}")
    r = request("GET", q("/dav/Projects/Contracts/Large.bin"), None, {"Range": "bytes=100-"})
    check("Range bytes=100- → rest of file", r.status == 206 and r.data == full[100:], f"{r.status} len={len(r.data)}")
    r = request("GET", q("/dav/Projects/Contracts/Large.bin"), None, {"Range": "bytes=-5"})
    check("Range bytes=-5 → last 5 bytes", r.status == 206 and r.data == full[-5:], f"{r.status} len={len(r.data)}")

    if etag_prop:
        quoted = etag_prop if etag_prop.startswith('"') else f'"{etag_prop}"'  # RFC 4918: getetag is a quoted entity tag
        r = request("GET", q("/dav/Projects/Contracts/Invoice.pdf"), None, {"If-None-Match": quoted})
        check("If-None-Match: current etag → 304", r.status == 304, f"{r.status}")
    r = request("GET", q("/dav/Projects/Contracts/Invoice.pdf"), None, {"If-Modified-Since": "Fri, 01 Jan 2100 00:00:00 GMT"})
    check("If-Modified-Since in the future → 304", r.status == 304, f"{r.status}")

    r, e = propfind("/dav/Projects/Contracts/Big/", "1", EXPLORER_PROPS)
    count = len(e) - 1
    check("5,000-entry folder, Explorer PROPFIND depth 1", r.status == 207 and count == 5000, f"{count} entries, {r.elapsed * 1000:.0f} ms, {len(r.data) / 1e6:.1f} MB")


def readonly_checks():
    for method, path, headers, body in [
        ("PUT", "/dav/Projects/Contracts/new.txt", {}, b"x"),
        ("PUT", "/dav/Projects/Contracts/Invoice.pdf", {}, b"x"),
        ("DELETE", "/dav/Projects/Contracts/Invoice.pdf", {}, None),
        ("MKCOL", "/dav/Projects/Contracts/NewFolder", {}, None),
        ("MOVE", "/dav/Projects/Contracts/Invoice.pdf", {"Destination": BASE + "/dav/Projects/Contracts/x.pdf"}, None),
        ("COPY", "/dav/Projects/Contracts/Invoice.pdf", {"Destination": BASE + "/dav/Projects/Contracts/x.pdf"}, None),
        ("LOCK", "/dav/Projects/Contracts/Invoice.pdf", {"Timeout": "Second-60"}, None),
        ("PROPPATCH", "/dav/Projects/Contracts/Invoice.pdf", {}, None),
    ]:
        r = request(method, q(path), body, headers)
        check(f"read-only: {method} → 403", r.status == 403, f"{r.status}")


LOCKINFO = '<?xml version="1.0" encoding="utf-8"?><D:lockinfo xmlns:D="DAV:"><D:lockscope><D:exclusive/></D:lockscope><D:locktype><D:write/></D:locktype><D:owner><D:href>user</D:href></D:owner></D:lockinfo>'


def write_checks():
    lib = "/dav/Projects/Contracts/"
    r = request("PUT", q(lib + "Notes.docx"), b"PK\x03\x04docx-content", {"Content-Type": "application/octet-stream"})
    check("PUT new file → 201 Created", r.status == 201, f"{r.status}")
    n = node("/Projects/Contracts/Notes.docx")
    check("PUT new file → item with title 'Notes', any file type", n is not None and n["title"] == "Notes", str(n))

    r1 = request("PUT", q(lib + "Explorer.pdf"), b"")
    r2 = request("PUT", q(lib + "Explorer.pdf"), b"%PDF-1.4 explorer")
    n = node("/Projects/Contracts/Explorer.pdf")
    check("Explorer pattern: PUT 0 bytes, then PUT content → one file version", n is not None and n["fileVersions"] == 1 and n["size"] == 17,
          f"{r1.status}/{r2.status} {n}")

    before = node("/Projects/Contracts/Invoice.pdf")
    r = request("PUT", q(lib + "Invoice.pdf"), b"%PDF-1.4 new version")
    after = node("/Projects/Contracts/Invoice.pdf")
    check("PUT existing file → 204/200 and a new version of the same item", r.status in (200, 204) and after["id"] == before["id"]
          and after["fileVersions"] == before["fileVersions"] + 1, f"{r.status} {before['fileVersions']}→{after['fileVersions']}")
    check("PUT existing answers 204 No Content (RFC 4918)", r.status == 204, f"{r.status}")

    big = b"x" * (6 * 1024 * 1024)
    r = request("PUT", q(lib + "TooBig.bin"), big)
    check("PUT over the size limit → 413 reaches the client", r.status == 413, f"{r.status} (store returned 413)")
    r = request("PUT", q(lib + "TooBig2.bin"), big, {"X-Poc-Status-Workaround": "1"})
    check("…workaround: store sets HttpContext status itself", r.status == 413, f"{r.status}")

    r = request("PUT", q(lib + "Read only.pdf"), b"%PDF-1.4 change")
    check("PUT to a file without Contribute → 403 reaches the client", r.status == 403, f"{r.status}")

    r = request("MKCOL", q(lib + "New Folder"))
    check("MKCOL → 201", r.status == 201, f"{r.status}")
    r = request("MKCOL", q(lib + "New Folder"))
    check("MKCOL existing → 405", r.status == 405, f"{r.status}")
    r = request("MKCOL", q("/dav/Projects/NotALibrary"))
    check("MKCOL at workspace level → 403", r.status == 403, f"{r.status}")
    r = request("MKCOL", q(lib + "Missing/Child"))
    check("MKCOL with a missing parent → 409", r.status == 409, f"{r.status}")

    orig = node("/Projects/Contracts/2025/Contract A.pdf")
    r = request("MOVE", q(lib + "2025/Contract A.pdf"), None, {"Destination": BASE + q(lib + "2025/Contract Renamed.pdf")})
    moved = node("/Projects/Contracts/2025/Contract Renamed.pdf")
    check("MOVE (rename) → 201", r.status == 201, f"{r.status}")
    check("MOVE keeps the item identity (id, versions)", moved is not None and moved["id"] == orig["id"],
          f"before={orig['id']} after={moved['id'] if moved else None}; MoveItemAsync called={r.getheader('X-Poc-MoveItemAsync')}, "
          f"CopyAsync={r.getheader('X-Poc-CopyAsync') is not None}")
    r = request("MOVE", q(lib + "2025/Contract Renamed.pdf"), None, {"Destination": BASE + q("/dav/Projects/Archive/Contract Renamed.pdf")})
    check("MOVE to another library → 201", r.status == 201 and node("/Projects/Archive/Contract Renamed.pdf") is not None, f"{r.status}")
    r = request("MOVE", q(lib + "Invoice (2).pdf"), None, {"Destination": BASE + q(lib + "Notes.docx"), "Overwrite": "F"})
    check("MOVE onto existing with Overwrite: F → 412", r.status == 412, f"{r.status}")
    folder_before = node("/Projects/Contracts/New Folder")
    request("PUT", q(lib + "New Folder/child.pdf"), b"%PDF child")
    child_before = node("/Projects/Contracts/New Folder/child.pdf")
    r = request("MOVE", q(lib + "New Folder"), None, {"Destination": BASE + q(lib + "Renamed Folder")})
    folder_after = node("/Projects/Contracts/Renamed Folder")
    child_after = node("/Projects/Contracts/Renamed Folder/child.pdf")
    check("MOVE folder (rename) → 201", r.status == 201 and folder_after is not None, f"{r.status}")
    check("MOVE folder keeps the folder's identity (permissions, values) and its children's",
          None not in (folder_before, folder_after, child_before, child_after)
          and folder_after["id"] == folder_before["id"] and child_after["id"] == child_before["id"],
          f"folder {(folder_before or {}).get('id')}→{(folder_after or {}).get('id')}, "
          f"child same={None not in (child_before, child_after) and child_after['id'] == child_before['id']}")

    r = request("COPY", q(lib + "Notes.docx"), None, {"Destination": BASE + q(lib + "Notes copy.docx")})
    check("COPY file → 201, new item", r.status == 201 and node("/Projects/Contracts/Notes copy.docx") is not None, f"{r.status}")

    request("MKCOL", q(lib + "ToDelete"))
    request("PUT", q(lib + "ToDelete/a.pdf"), b"%PDF a")
    bin_before = node("/Projects/Contracts")["recycleBin"]
    r = request("DELETE", q(lib + "ToDelete"))
    check("DELETE non-empty folder → 204, children first (store rejects non-empty folders)", r.status == 204 and node("/Projects/Contracts/ToDelete") is None,
          f"{r.status}")
    check("DELETE goes to the recycle bin (folder + child)", node("/Projects/Contracts")["recycleBin"] == bin_before + 2,
          f"{bin_before}→{node('/Projects/Contracts')['recycleBin']}")

    body = ('<?xml version="1.0" encoding="utf-8"?><D:propertyupdate xmlns:D="DAV:" xmlns:Z="urn:schemas-microsoft-com:"><D:set><D:prop>'
            '<Z:Win32CreationTime>Mon, 06 Oct 2026 10:00:00 GMT</Z:Win32CreationTime><Z:Win32LastModifiedTime>Mon, 06 Oct 2026 10:00:00 GMT</Z:Win32LastModifiedTime>'
            '<Z:Win32FileAttributes>00000020</Z:Win32FileAttributes></D:prop></D:set></D:propertyupdate>')
    r = request("PROPPATCH", q(lib + "Notes.docx"), body)
    check("PROPPATCH Win32 props (Explorer after every PUT) → 207 with 200s", r.status == 207 and b"404" not in r.data and b"403" not in r.data,
          f"{r.status} {r.data[-200:]!r}")
    body2 = '<?xml version="1.0"?><D:propertyupdate xmlns:D="DAV:"><D:set><D:prop><x:color xmlns:x="urn:x">red</x:color></D:prop></D:set></D:propertyupdate>'
    r = request("PROPPATCH", q(lib + "Notes.docx"), body2)
    check("PROPPATCH unknown dead property → 207 with a per-property failure", r.status == 207, f"{r.status}")

    # Locks (Office opens read-write only after a successful LOCK)
    r = request("LOCK", q(lib + "Notes.docx"), LOCKINFO, {"Timeout": "Second-3600", "Depth": "0"})
    token_header = r.getheader("Lock-Token")
    body_token = re.search(rb"<(?:\w+:)?locktoken><(?:\w+:)?href>([^<]+)<", r.data)
    token = token_header.strip("<>") if token_header else (body_token.group(1).decode() if body_token else None)
    check("LOCK exclusive → 200 with lockdiscovery body", r.status == 200 and body_token is not None, f"{r.status}")
    check("LOCK response has a Lock-Token header (RFC 4918 §10.5, required by clients)", token_header is not None, str(token_header))
    r = request("PUT", q(lib + "Notes.docx"), b"PK changed")
    check("PUT on a locked file without the token → 423", r.status == 423, f"{r.status}")
    if token:
        r = request("PUT", q(lib + "Notes.docx"), b"PK changed", {"If": f"(<{token}>)"})
        check("PUT with If: (<token>) → succeeds", r.status in (200, 204), f"{r.status}")
        r = request("LOCK", q(lib + "Notes.docx"), LOCKINFO, {"Timeout": "Second-60"})
        check("second exclusive LOCK → 423", r.status == 423, f"{r.status}")
        r = request("LOCK", q(lib + "Notes.docx"), None, {"Timeout": "Second-600", "If": f"(<{token}>)"})
        check("LOCK refresh with If → 200", r.status == 200, f"{r.status}")
        r = request("LOCK", q(lib + "Notes.docx"), LOCKINFO, {"Timeout": "Infinite, Second-4100000000"})
        r = request("UNLOCK", q(lib + "Notes.docx"), None, {"Lock-Token": f"<{token}>"})
        check("UNLOCK → 204", r.status == 204, f"{r.status}")
    r = request("LOCK", q(lib + "Notes.docx"), LOCKINFO, {"Timeout": "Infinite, Second-4100000000"})
    m = re.search(rb"<(?:\w+:)?timeout>([^<]+)<", r.data)
    check("LOCK timeout is capped by MaxLockTimeout (1 h)", r.status == 200 and m and m.group(1) == b"Second-3600", f"{r.status} {m.group(1) if m else None}")
    tok = re.search(rb"<(?:\w+:)?locktoken><(?:\w+:)?href>([^<]+)<", r.data)
    if tok:
        request("UNLOCK", q(lib + "Notes.docx"), None, {"Lock-Token": f"<{tok.group(1).decode()}>"})

    r = request("LOCK", q(lib + "Brand new.docx"), LOCKINFO, {"Timeout": "Second-60"})
    check("LOCK on an unmapped URL creates an empty resource (201, RFC 4918 §9.10.4)", r.status == 201 and node("/Projects/Contracts/Brand new.docx") is not None,
          f"{r.status} exists={node('/Projects/Contracts/Brand new.docx') is not None}")

    # Office/LibreOffice "safe save" without in-place PUT: temp file, rename original, rename temp, delete backup.
    request("PUT", q(lib + "Budget.xlsx"), b"PK v1")
    orig = node("/Projects/Contracts/Budget.xlsx")
    r1 = request("PUT", q(lib + "~$Budget.xlsx"), b"owner")
    check("Office owner file ~$… recognized as transient", r1.getheader("X-Poc-Transient") == "true", str(r1.getheader("X-Poc-Transient")))
    steps = [request("PUT", q(lib + "Budget.tmp"), b"PK v2").status,
             request("MOVE", q(lib + "Budget.xlsx"), None, {"Destination": BASE + q(lib + "Budget~RF1.TMP")}).status,
             request("MOVE", q(lib + "Budget.tmp"), None, {"Destination": BASE + q(lib + "Budget.xlsx")}).status,
             request("DELETE", q(lib + "Budget~RF1.TMP")).status,
             request("DELETE", q(lib + "~$Budget.xlsx")).status]
    final = node("/Projects/Contracts/Budget.xlsx")
    check("safe-save (temp file, rename original, rename temp, delete backup) keeps the original item",
          final is not None and final["id"] == orig["id"] and final["fileVersions"] == orig["fileVersions"] + 1,
          f"statuses={steps} before={orig['id']} after={final['id'] if final else None} versions={orig['fileVersions']}→{final['fileVersions'] if final else None}")


read_checks()
if MODE == "ro":
    readonly_checks()
else:
    write_checks()

width = max(len(n) for n, _, _ in results)
for name, ok, detail in results:
    print(f"{'PASS' if ok else 'FAIL'}  {name.ljust(width)}  {detail}")
print(f"\n{sum(ok for _, ok, _ in results)}/{len(results)} passed ({MODE})")

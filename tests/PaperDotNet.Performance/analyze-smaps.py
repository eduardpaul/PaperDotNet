#!/usr/bin/env python3
"""Inventory Linux smaps or smaps.gz; classify mappings, not allocation ownership."""
import argparse
import collections
import csv
import gzip
import hashlib
import json
import pathlib
import re


def category(name):
    if "doublemapper" in name:
        return "runtime allocator"
    if ".dll" in name:
        return "managed assemblies"
    if ".so" in name:
        return "native libraries"
    if name == "[anonymous]":
        return "anonymous"
    return "other"


def assembly_group(name):
    if "/shared/Microsoft.NETCore.App/" in name:
        return ".NET runtime"
    if "/shared/Microsoft.AspNetCore.App/" in name:
        return "ASP.NET runtime"
    if pathlib.Path(name).name.startswith("PaperDotNet.") or name.endswith("/paperdotnet.dll"):
        return "PaperDotNet"
    return "third-party packages"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=pathlib.Path)
    parser.add_argument("output", type=pathlib.Path, help="Output prefix for JSON and CSV")
    args = parser.parse_args()
    raw = args.input.read_bytes()
    data = gzip.decompress(raw) if args.input.suffix == ".gz" else raw
    fields = ("Size", "Rss", "Pss", "Shared_Clean", "Shared_Dirty", "Private_Clean", "Private_Dirty")
    files = collections.defaultdict(collections.Counter)
    totals = collections.defaultdict(collections.Counter)
    groups = collections.defaultdict(collections.Counter)
    permissions = collections.defaultdict(collections.Counter)
    blocks = re.split(r"(?=^[0-9a-f]+-[0-9a-f]+ )", data.decode(), flags=re.M)
    for block in blocks:
        if not block.strip():
            continue
        header = block.splitlines()[0].split()
        name = " ".join(header[5:]) or "[anonymous]"
        values = {key: int(value) for key, value in re.findall(r"^(\w+):\s+(\d+) kB", block, re.M)}
        metrics = {field: values.get(field, 0) for field in fields}
        files[name].update(metrics)
        totals[category(name)].update(metrics)
        if category(name) == "managed assemblies":
            groups[assembly_group(name)].update(metrics)
        if category(name) == "runtime allocator":
            permissions[header[1]].update(metrics)
    report = {
        "source": str(args.input), "decompressedSha256": hashlib.sha256(data).hexdigest(),
        "units": "KiB", "categories": totals, "assemblyGroups": groups,
        "runtimeAllocatorPermissions": permissions,
        "files": [{"path": name, "category": category(name), **metrics}
                  for name, metrics in sorted(files.items(), key=lambda item: -item[1]["Rss"])],
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.with_suffix(".json").write_text(json.dumps(report, indent=2) + "\n")
    with args.output.with_suffix(".csv").open("w", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=["path", "category", *fields])
        writer.writeheader()
        writer.writerows(report["files"])
    print(json.dumps({"categories": totals, "assemblyGroups": groups,
                      "runtimeAllocatorPermissions": permissions}, indent=2))


if __name__ == "__main__":
    main()

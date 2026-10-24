#!/usr/bin/env python3
"""Round trip an isolated EF content database through the actual pinned PaperGo DAL.

Build interop.go inside a disposable PaperGo checkout first. PostgreSQL connection
strings are read from environment variables to keep them out of command output.
"""

import argparse
import json
import os
import subprocess
import tempfile
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument(
    "--host",
    type=Path,
    default=ROOT / "src/PaperDotNet.Host/bin/Debug/net10.0/paperdotnet.dll",
)
parser.add_argument("--go-probe", type=Path, required=True)
parser.add_argument("--provider", choices=["sqlite", "postgresql"], default="sqlite")
args = parser.parse_args()


def run(argv, env):
    result = subprocess.run(argv, env=env, capture_output=True, text=True, check=False)
    if result.returncode:
        raise SystemExit(
            f"Interop command failed ({result.returncode}): {result.stderr or result.stdout}"
        )
    return json.loads(result.stdout)


with tempfile.TemporaryDirectory(prefix="paperdotnet-interop-") as temporary:
    directory = Path(temporary)
    env = os.environ.copy()
    if args.provider == "sqlite":
        env["PAPERDOTNET_CONTENT_CONNECTION_STRING"] = (
            f"Data Source={directory / 'content.db'}"
        )
        env["PAPERGO_INTEROP_CONNECTION"] = str(directory / "content.db")
    else:
        for name in [
            "PAPERDOTNET_CONTENT_CONNECTION_STRING",
            "PAPERGO_INTEROP_CONNECTION",
        ]:
            if not env.get(name):
                raise SystemExit(
                    f"Set {name} to an isolated, empty PostgreSQL test database."
                )

    def cli(command, *options):
        return run(
            [
                "dotnet",
                str(args.host.resolve()),
                "content-db",
                "--provider",
                args.provider,
                "--actor",
                "dotnet-interop",
                command,
                *map(str, options),
            ],
            env,
        )

    def input_file(name, value):
        path = directory / name
        path.write_text(json.dumps(value), encoding="utf-8")
        return path

    assert cli("init")["status"] == "compatible"
    workspace = cli("workspace", "--name", "Interop workspace")
    collection = cli(
        "collection",
        "--workspace",
        workspace["id"],
        "--name",
        "Interop list",
        "--publishing",
    )
    type_id = str(uuid.uuid4())
    schema = cli(
        "schema",
        "--collection",
        collection["id"],
        "--version",
        collection["version"],
        "--input",
        input_file(
            "schema.json",
            {
                "fields": [
                    {
                        "id": "serial",
                        "label": "Serial",
                        "type": "integer",
                        "indexed": True,
                        "options": {"unique": True},
                    },
                    {
                        "id": "status",
                        "label": "Status",
                        "type": "choice",
                        "choices": ["Open", "Closed"],
                        "indexed": True,
                    },
                ],
                "content_types": [
                    {
                        "id": type_id,
                        "key": "record",
                        "name": "Record",
                        "field_keys": ["serial", "status"],
                        "is_default": True,
                    }
                ],
            },
        ),
    )
    values = {"serial": 9223372036854775807, "status": "Open"}
    content = {"name": "Published first", "contentTypeId": type_id, "values": values}
    item = cli(
        "create",
        "--parent",
        collection["id"],
        "--input",
        input_file("item.json", content),
    )
    cli("publish", "--id", item["id"], "--version", item["version"])
    item = cli("read", "--id", item["id"], "--surface", "head")["resource"]
    content["name"] = "Separate draft"
    item = cli(
        "edit",
        "--id",
        item["id"],
        "--version",
        item["version"],
        "--input",
        input_file("edit.json", content),
    )
    go = run(
        [
            str(args.go_probe.resolve()),
            "--provider",
            args.provider,
            "--item",
            item["id"],
        ],
        env,
    )
    assert (
        go["schema_compatible"]
        and go["indexed_fields"] == 4
        and len(go["surfaces"]) == 2
    )
    assert go["revision"]["payload"]["serial"] == 9223372036854775807
    assert go["schema_revisions"] == 2
    archived = cli("read", "--id", item["id"], "--surface", "head")
    assert archived["resource"]["archivedBy"] == "papergo-interop"
    assert archived["schema"]["id"] == schema["id"]
    content["name"] = "EF after Go"
    item = cli(
        "edit",
        "--id",
        item["id"],
        "--version",
        archived["resource"]["version"],
        "--input",
        input_file("after-go.json", content),
    )
    head = cli("read", "--id", item["id"], "--surface", "head")
    assert head["schema"]["id"] == go["new_schema_id"]
    assert head["resource"]["archivedBy"] == "papergo-interop"
    published = cli("read", "--id", item["id"])
    assert published["revision"]["name"] == "Published first"
    cli("unarchive", "--id", item["id"], "--version", item["version"])
    assert len(cli("browse", "--collection", collection["id"])) == 1
    print(
        f"{args.provider}: EF -> PaperGo schema/read/archive/schema revision -> EF edit/unarchive passed"
    )

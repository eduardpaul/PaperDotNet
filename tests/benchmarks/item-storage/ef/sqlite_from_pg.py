"""Builds the SQLite copy with EF's own DDL (tables lists_items, lists_permission_grants and their indexes)
from CSV exports of the PostgreSQL data. Usage: sqlite_from_pg.py <ddl.sql> <items.csv> <grants.csv> <out.db>"""
import csv, os, re, sqlite3, sys

ddl_path, items_csv, grants_csv, path = sys.argv[1:5]
csv.field_size_limit(10**9)
if os.path.exists(path):
    os.remove(path)
ddl = open(ddl_path).read()
tables = re.findall(r'CREATE TABLE "lists_(?:items|permission_grants)" \(.*?\);', ddl, re.S)
indexes = re.findall(r'CREATE (?:UNIQUE )?INDEX "[^"]+" ON "lists_(?:items|permission_grants)" \([^)]*\);', ddl)
db = sqlite3.connect(path)
db.executescript('PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;' + '\n'.join(tables))
for table, file, n in [('lists_items', items_csv, 17), ('lists_permission_grants', grants_csv, 7)]:
    with open(file, newline='') as f:
        db.executemany(f'INSERT INTO "{table}" VALUES ({",".join("?" * n)})', ([None if v == '' else v for v in r] for r in csv.reader(f)))
    db.commit()
db.executescript('\n'.join(indexes) + '\nANALYZE;')
print(f'{len(tables)} tables, {len(indexes)} indexes, {db.execute("SELECT count(*) FROM lists_items").fetchone()[0]} items')

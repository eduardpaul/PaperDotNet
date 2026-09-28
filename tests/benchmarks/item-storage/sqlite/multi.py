"""Multi-select and multi-lookup fields on SQLite: JSON (json_each, a lower bound for the C# pdn_json_contains function)
vs. one value table. Same data as pg/10_multi_values.sql. Usage: multi.py <data-dir>"""
import hashlib, json, os, sqlite3, statistics, sys, time

path = os.path.join(sys.argv[1], 'multi.db')
if os.path.exists(path):
    os.remove(path)


def g(s):
    h = hashlib.md5(s.encode()).hexdigest()
    return f'{h[:8]}-{h[8:12]}-{h[12:16]}-{h[16:20]}-{h[20:]}'.upper()


db = sqlite3.connect(path)
db.executescript('''PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;
CREATE TABLE lists_items(id TEXT NOT NULL PRIMARY KEY, tenant_id TEXT NOT NULL, list_id TEXT NOT NULL, deleted_at INTEGER, title TEXT NOT NULL, fields TEXT NOT NULL);
CREATE TABLE lists_item_values(tenant_id TEXT NOT NULL, list_id TEXT NOT NULL, item_id TEXT NOT NULL, field TEXT NOT NULL, value TEXT NOT NULL,
  PRIMARY KEY (item_id, field, value)) WITHOUT ROWID;''')
T, L = g('tenant'), g('list-multi')
items, values = [], []
for n in range(1, 500001):
    labels = sorted({f'Label{(n * k * 7 + k) % 11}' for k in range(1, 2 + n % 4)} | ({'Rare'} if n % 200 == 0 else set()))
    contacts = sorted({g(f'c{(n * 31 + k * 9973) % 50000}') for k in range(1, 2 + n % 5)})
    i = g(f'm{n}')
    items.append((i, T, L, None, f'Item {n}', json.dumps({'labels': labels, 'contacts': contacts})))
    values += [(T, L, i, 'labels', v) for v in labels] + [(T, L, i, 'contacts', v) for v in contacts]
db.executemany('INSERT INTO lists_items VALUES (?,?,?,?,?,?)', items)
db.executemany('INSERT INTO lists_item_values VALUES (?,?,?,?,?)', values)
db.executescript('CREATE INDEX ix_items_list ON lists_items(list_id); CREATE INDEX ix_values_lookup ON lists_item_values(list_id, field, value, item_id); ANALYZE;')
db.commit()
print(f'@@ sqlite multi sizes: {len(values)} values, database {os.path.getsize(path) // 2**20} MB')


def run(label, sql, args=(), n=5):
    times = []
    for _ in range(n + 1):
        t = time.perf_counter()
        rows = db.execute(sql, args).fetchall()
        times.append((time.perf_counter() - t) * 1000)
    print(f'@@ sqlite {label}: {statistics.median(times[1:]):.2f} ms (rows={len(rows)})', flush=True)


item = 'SELECT * FROM lists_items i WHERE list_id = ? AND deleted_at IS NULL'
js = "EXISTS (SELECT 1 FROM json_each(i.fields, '$.{f}') WHERE value {op})"
val = 'EXISTS (SELECT 1 FROM lists_item_values v WHERE v.item_id = i.id AND v.list_id = ? AND v.field = ? AND v.value {op})'
vin = 'i.id IN (SELECT v.item_id FROM lists_item_values v WHERE v.list_id = ? AND v.field = ? AND v.value {op})'
C = g('c123')
cases = [
    ('M1 any of two common labels', [('labels', "IN ('Label3','Label5')", ())]),
    ('M2 rare label', [('labels', "= 'Rare'", ())]),
    ('M3 all of two labels', [('labels', "= 'Label3'", ()), ('labels', "= 'Label5'", ())]),
    ('M6 items referencing one contact', [('contacts', '= ?', (C,))]),
    ('M7 label and contact', [('labels', "= 'Label3'", ()), ('contacts', '= ?', (C,))]),
]
for label, parts in cases:
    jsql = item + ''.join(' AND ' + js.format(f=f, op=op) for f, op, _ in parts) + ' ORDER BY id LIMIT 101'
    vsql = item + ''.join(' AND ' + val.format(op=op) for _, op, _ in parts) + ' ORDER BY id LIMIT 101'
    run(f'{label} JSON', jsql, (L, *[a for _, _, args in parts for a in args]), n=3)
    run(f'{label} values', vsql, (L, *[a for f, _, args in parts for a in (L, f, *args)]))
    isql = item + ''.join(' AND ' + vin.format(op=op) for _, op, _ in parts) + ' ORDER BY id LIMIT 101'
    run(f'{label} values IN', isql, (L, *[a for f, _, args in parts for a in (L, f, *args)]))
run('M4 none of a label JSON', item + ' AND NOT ' + js.format(f='labels', op="= 'Label3'") + ' ORDER BY id LIMIT 101', (L,))
run('M4 none of a label values', item + ' AND NOT ' + val.format(op="= 'Label3'") + ' ORDER BY id LIMIT 101', (L, L, 'labels'))
run('M4 none of a label values IN', item + ' AND NOT ' + vin.format(op="= 'Label3'") + ' ORDER BY id LIMIT 101', (L, L, 'labels'))
run('M5 count per label JSON', "SELECT l.value, count(*) FROM lists_items i, json_each(i.fields, '$.labels') l WHERE i.list_id = ? AND i.deleted_at IS NULL GROUP BY 1", (L,), n=3)
run('M5 count per label values', 'SELECT value, count(*) FROM lists_item_values WHERE list_id = ? AND field = ? GROUP BY value', (L, 'labels'))
run('M8 count with a label JSON', "SELECT count(*) FROM lists_items i WHERE list_id = ? AND deleted_at IS NULL AND " + js.format(f='labels', op="= 'Label3'"), (L,), n=3)
run('M8 count with a label values', 'SELECT count(*) FROM lists_item_values WHERE list_id = ? AND field = ? AND value = ?', (L, 'labels', 'Label3'))
run('M7 label EXISTS + contact IN (each in its best form)', item + ' AND ' + val.format(op="= 'Label3'") + ' AND ' + vin.format(op='= ?') + ' ORDER BY id LIMIT 101', (L, L, 'labels', L, 'contacts', C))
run('selectivity probe: count of one contact', 'SELECT count(*) FROM lists_item_values WHERE list_id = ? AND field = ? AND value = ?', (L, 'contacts', C))
run('selectivity probe: count of a common label, capped at 2,000', 'SELECT count(*) FROM (SELECT 1 FROM lists_item_values WHERE list_id = ? AND field = ? AND value = ? LIMIT 2000)', (L, 'labels', 'Label3'))

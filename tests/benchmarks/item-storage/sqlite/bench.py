"""SQLite side of the item-storage benchmark (same data as PostgreSQL). Usage: bench.py <data-dir>"""
import hashlib, json, os, sqlite3, statistics, sys, threading, time

path = os.path.join(sys.argv[1], 'bench.db')
db = sqlite3.connect(path)
db.execute('PRAGMA busy_timeout = 5000'); db.execute('PRAGMA cache_size = -1000000')


def g(s):  # md5(text)::uuid as EF stores it on SQLite (upper-case TEXT)
    h = hashlib.md5(s.encode()).hexdigest()
    return f'{h[:8]}-{h[8:12]}-{h[12:16]}-{h[16:20]}-{h[20:]}'.upper()


def contains(doc, frag):  # stand-in for the C# pdn_json_contains function (parses every row's JSON)
    d, f = json.loads(doc), json.loads(frag)
    for k, v in f.items():
        x = d.get(k)
        if isinstance(v, list):
            if not isinstance(x, list) or not all(e in x for e in v):
                return False
        elif x != v:
            return False
    return True


db.create_function('pdn_json_contains', 2, contains, deterministic=True)
T, DMS, CRM = g('tenant'), g('list-dms'), g('list-crm')


def run(label, sql, args=(), n=5):
    times = []
    for _ in range(n + 1):
        t = time.perf_counter()
        rows = db.execute(sql, args).fetchall()
        times.append((time.perf_counter() - t) * 1000)
    print(f'@@ sqlite {label}: {statistics.median(times[1:]):.2f} ms (rows={len(rows)})', flush=True)
    return rows


def marks(values):
    return ','.join('?' * len(values))


for user in ['u1', 'u5']:
    U, n = g(user), user[1:]
    groups = [r[0] for r in run(f'{user} today preload 2: groups', 'SELECT GroupId FROM group_members WHERE UserId=?', (U,))]
    run(f'{user} today preload 1: any unique item scope in the list', 'SELECT EXISTS (SELECT 1 FROM items WHERE TenantId=? AND ListId=? AND HasUniquePermissions)', (T, DMS))
    grants = run(f'{user} today preload 3: grants of the user in the list',
                 f"SELECT ObjectId FROM permission_grants WHERE TenantId=? AND ListId=? AND ((PrincipalType='User' AND PrincipalId=?) OR (PrincipalType='Group' AND PrincipalId IN ({marks(groups)})))",
                 (T, DMS, U, *groups))
    run(f'{user} today preload 4: every unique scope id of the list', 'SELECT Id FROM items WHERE TenantId=? AND ListId=? AND HasUniquePermissions', (T, DMS), n=3)
    allowed = [r[0] for r in grants]
    P = [U, g('ws-members'), *groups]
    allowed2 = [r[0] for r in run(f'{user} A allowed set from the ACL by principal', f'SELECT ScopeId FROM acl WHERE PrincipalId IN ({marks(P)}) AND ListId=? AND Level>=1', (*P, DMS))]
    arr = json.dumps(allowed2)
    semi = f'(SELECT ScopeId FROM acl WHERE PrincipalId IN ({marks(P)}) AND ListId=? AND Level>=1)'
    base = 'SELECT * FROM items WHERE TenantId=? AND ListId=? AND DeletedAt IS NULL'
    today = f'(ScopeId IS NULL OR ScopeId IN ({marks(allowed)}))'
    for q, extra, order, qargs in [
        ('Q1 own sub-folder page', 'AND ParentId=?', 'ORDER BY IsFolder DESC, Title LIMIT 101', (g(f'sf{n}-1'),)),
        ('Q2 root page', 'AND ParentId IS NULL', 'ORDER BY IsFolder DESC, Title LIMIT 101', ()),
        ('Q3 all readable documents by id', 'AND NOT IsFolder', 'ORDER BY Id LIMIT 101', ()),
    ]:
        run(f'{user} today {q}', f'{base} {extra} AND {today} {order}', (T, DMS, *qargs, *allowed))
        run(f'{user} A in-list {q}', f'{base} {extra} AND Scope2 IN ({marks(allowed2)}) {order}', (T, DMS, *qargs, *allowed2))
        run(f'{user} A json_each {q}', f'{base} {extra} AND Scope2 IN (SELECT value FROM json_each(?)) {order}', (T, DMS, *qargs, arr))
        run(f'{user} A semi-join {q}', f'{base} {extra} AND Scope2 IN {semi} {order}', (T, DMS, *qargs, *P, DMS))
    count = 'SELECT count(*) FROM items WHERE TenantId=? AND ListId=? AND DeletedAt IS NULL AND NOT IsFolder'
    run(f'{user} today Q4 count readable documents', f'{count} AND {today}', (T, DMS, *allowed), n=3)
    run(f'{user} A json_each Q4 count readable documents', f'{count} AND Scope2 IN (SELECT value FROM json_each(?))', (T, DMS, arr), n=3)

# Fields (CRM list, 500k deals; 50 task lists)
run('C1 JSON json_extract', "SELECT * FROM items WHERE TenantId=? AND ListId=? AND DeletedAt IS NULL AND json_extract(Fields,'$.stage')='Negotiation' ORDER BY json_extract(Fields,'$.closeDate'), Id LIMIT 101", (T, CRM), n=3)
run('C1 JSON containment function (like pdn_json_contains)', "SELECT * FROM items WHERE TenantId=? AND ListId=? AND DeletedAt IS NULL AND pdn_json_contains(Fields,'{\"stage\":\"Negotiation\"}') ORDER BY json_extract(Fields,'$.closeDate'), Id LIMIT 101", (T, CRM), n=1)
run('C1 slots', "SELECT * FROM items WHERE TenantId=? AND ListId=? AND DeletedAt IS NULL AND SText1='Negotiation' AND SDate1 IS NOT NULL ORDER BY SDate1, Id LIMIT 101", (T, CRM))
run('C3 JSON board counts', "SELECT json_extract(Fields,'$.stage'), count(*) FROM items WHERE TenantId=? AND ListId=? AND DeletedAt IS NULL GROUP BY 1", (T, CRM), n=3)
run('C3 slots board counts', 'SELECT SText1, count(*) FROM items WHERE ListId=? AND SText1 IS NOT NULL GROUP BY 1', (CRM,))
run('C4 JSON deals of one account', "SELECT * FROM items WHERE TenantId=? AND ListId=? AND DeletedAt IS NULL AND json_extract(Fields,'$.account')=? ORDER BY Id LIMIT 101", (T, CRM, g('acc42').lower()), n=3)
run('C4 junction deals of one account', "SELECT i.* FROM item_values v JOIN items i ON i.Id=v.ItemId WHERE v.Field='account' AND v.VGuid=? AND v.ListId=? ORDER BY i.Id LIMIT 101", (g('acc42'), CRM))
me, tasklists = g('u17'), [g(f'list-tasks{l}') for l in range(1, 51)]
times = []
for _ in range(3):
    t = time.perf_counter()
    for L in tasklists:
        db.execute('SELECT EXISTS (SELECT 1 FROM items WHERE TenantId=? AND ListId=? AND HasUniquePermissions)', (T, L)).fetchall()
        db.execute("SELECT * FROM items WHERE TenantId=? AND ListId=? AND DeletedAt IS NULL AND EXISTS (SELECT 1 FROM json_each(Fields,'$.assignedTo') WHERE value=?) AND json_extract(Fields,'$.status')<>'Completed' ORDER BY json_extract(Fields,'$.dueDate'), Id LIMIT 1000", (T, L, me.lower())).fetchall()
    times.append((time.perf_counter() - t) * 1000)
print(f'@@ sqlite T1 today loop over 50 task lists (preload 1 + JSON query each): {statistics.median(times):.2f} ms', flush=True)
run('T1 junction + slots, one query over 50 lists', f"SELECT i.* FROM item_values v JOIN items i ON i.Id=v.ItemId WHERE v.Field='assignedTo' AND v.VGuid=? AND v.ListId IN ({marks(tasklists)}) AND i.DeletedAt IS NULL AND i.SText1<>'Completed' ORDER BY i.SDate1, i.Id LIMIT 100", (me, *tasklists))

# Fan-out: one statement rewriting a subtree's scope holds SQLite's database-wide write lock.
def conn():
    c = sqlite3.connect(path, timeout=5.0, isolation_level=None)
    c.execute('PRAGMA busy_timeout = 5000')
    return c

homes = [g(f'hf{i}') for i in range(1, 501)]
for label, where, args in [
        ('10,000 rows (Public folder)', 'ParentId = ?', (g('pub'),)),
        ('100,000 rows (500 home folders)', f'ParentId IN (SELECT Id FROM items WHERE ParentId IN ({marks(homes)}))', tuple(homes))]:
    big, results = conn(), []

    def writer():  # an unrelated edit (a CRM deal) arriving 100 ms after the rewrite started
        time.sleep(0.1)
        c, t = conn(), time.perf_counter()
        try:
            c.execute('BEGIN IMMEDIATE')
            c.execute("UPDATE items SET Title = 'x', Version = Version + 1 WHERE Id = ?", (g('deal77'),))
            c.execute('ROLLBACK')
            results.append(f'unrelated writer waited {(time.perf_counter() - t) * 1000:.0f} ms')
        except sqlite3.OperationalError as e:
            results.append(f'unrelated writer failed after {(time.perf_counter() - t) * 1000:.0f} ms: {e}')

    th = threading.Thread(target=writer)
    th.start()
    t = time.perf_counter()
    big.execute('BEGIN IMMEDIATE')
    count = big.execute(f'UPDATE items SET ScopeId = ?, Scope2 = ? WHERE {where}', (g('x'), g('x'), *args)).rowcount
    held = (time.perf_counter() - t) * 1000
    big.execute('ROLLBACK')
    th.join()
    print(f'@@ sqlite scope rewrite {label}: {count} rows, write lock held {held:.0f} ms; {results[0]}', flush=True)

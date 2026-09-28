"""Loads the CSV export of the PostgreSQL benchmark data into SQLite with EF-style columns (GUIDs as TEXT).
Usage: load.py <data-dir>"""
import csv, sqlite3, sys, time, os
csv.field_size_limit(10**9)
data = sys.argv[1]
path = os.path.join(data, 'bench.db')
if os.path.exists(path): os.remove(path)
db = sqlite3.connect(path)
db.executescript("""
PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;
CREATE TABLE items(
  Id TEXT NOT NULL PRIMARY KEY, TenantId TEXT NOT NULL, ListId TEXT NOT NULL, ContentTypeId TEXT NOT NULL, ParentId TEXT,
  IsFolder INTEGER NOT NULL, HasUniquePermissions INTEGER NOT NULL, ScopeId TEXT, Title TEXT NOT NULL, Fields TEXT NOT NULL,
  CreatedAt TEXT NOT NULL, CreatedBy TEXT, UpdatedAt TEXT NOT NULL, UpdatedBy TEXT, DeletedAt TEXT, DeletedBy TEXT, Version INTEGER NOT NULL,
  Scope2 TEXT NOT NULL, SText1 TEXT, SNum1 REAL, SDate1 TEXT);
CREATE TABLE permission_grants(Id TEXT NOT NULL PRIMARY KEY, TenantId TEXT NOT NULL, ListId TEXT NOT NULL, ObjectId TEXT NOT NULL,
  PrincipalType TEXT NOT NULL, PrincipalId TEXT NOT NULL, Level TEXT NOT NULL);
CREATE TABLE acl(TenantId TEXT NOT NULL, ListId TEXT NOT NULL, ScopeId TEXT NOT NULL, PrincipalId TEXT NOT NULL, Level INTEGER NOT NULL,
  PRIMARY KEY (ScopeId, PrincipalId)) WITHOUT ROWID;
CREATE TABLE group_members(GroupId TEXT NOT NULL, UserId TEXT NOT NULL, PRIMARY KEY (GroupId, UserId));
CREATE TABLE item_values(TenantId TEXT NOT NULL, ListId TEXT NOT NULL, ItemId TEXT NOT NULL, Field TEXT NOT NULL, Ordinal INTEGER NOT NULL,
  VText TEXT, VNum REAL, VDate TEXT, VGuid TEXT, PRIMARY KEY (ItemId, Field, Ordinal));
""")
def load(table, n):
    t = time.time()
    with open(os.path.join(data, f'{table}.csv'), newline='') as f:
        rows = ([None if v == '' else v for v in r] for r in csv.reader(f))
        db.executemany(f"INSERT INTO {table} VALUES ({','.join('?'*n)})", rows)
    db.commit()
    print(table, round(time.time() - t, 1), 's', flush=True)
load('items', 21); load('permission_grants', 7); load('acl', 5); load('group_members', 2); load('item_values', 9)
t = time.time()
db.executescript("""
-- Today's indexes (ListsDbContext + conventions; the JSON containment index is dropped on SQLite).
CREATE INDEX IX_items_TenantId ON items(TenantId);
CREATE INDEX IX_items_ListId_ParentId ON items(ListId, ParentId);
CREATE INDEX IX_items_ListId_ScopeId ON items(ListId, ScopeId);
CREATE UNIQUE INDEX IX_grants_Object ON permission_grants(ObjectId, PrincipalType, PrincipalId);
CREATE INDEX IX_grants_ListId ON permission_grants(ListId);
CREATE INDEX IX_group_members_UserId ON group_members(UserId);
-- Candidate indexes.
CREATE INDEX IX_items_Scope2_Id ON items(Scope2, Id);
CREATE INDEX IX_items_Browse ON items(ListId, ParentId, IsFolder DESC, Title, Id);
CREATE INDEX IX_acl_Principal ON acl(PrincipalId, ListId, ScopeId, Level);
CREATE INDEX IX_items_SText1 ON items(ListId, SText1, Id) WHERE SText1 IS NOT NULL;
CREATE INDEX IX_items_SNum1 ON items(ListId, SNum1, Id) WHERE SNum1 IS NOT NULL;
CREATE INDEX IX_items_SDate1 ON items(ListId, SDate1, Id) WHERE SDate1 IS NOT NULL;
CREATE INDEX IX_values_Text ON item_values(ListId, Field, VText, ItemId) WHERE VText IS NOT NULL;
CREATE INDEX IX_values_Date ON item_values(ListId, Field, VDate, ItemId) WHERE VDate IS NOT NULL;
CREATE INDEX IX_values_Guid ON item_values(Field, VGuid, ListId, ItemId) WHERE VGuid IS NOT NULL;
ANALYZE;
""")
print('indexes', round(time.time() - t, 1), 's')
print('size MB', os.path.getsize(path) // 2**20)

DROP SCHEMA IF EXISTS bench CASCADE;
CREATE SCHEMA bench;
SET search_path = bench;

-- Directory: 5,000 users, 50 groups, each user in two groups.
CREATE TABLE users(id uuid PRIMARY KEY, n int NOT NULL);
INSERT INTO users SELECT md5('u'||i)::uuid, i FROM generate_series(1,5000) i;
CREATE TABLE group_members(group_id uuid, user_id uuid, PRIMARY KEY(group_id, user_id));
CREATE INDEX ON group_members(user_id);
INSERT INTO group_members
SELECT DISTINCT g, u FROM (
  SELECT md5('g'||(i%50))::uuid g, md5('u'||i)::uuid u FROM generate_series(1,5000) i
  UNION ALL
  SELECT md5('g'||((i*7)%50))::uuid, md5('u'||i)::uuid FROM generate_series(1,5000) i) x;

-- Mirror of lists.items as EF creates it today (+ columns used by the candidate designs).
CREATE TABLE items(
  id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL,
  list_id uuid NOT NULL,
  content_type_id uuid NOT NULL,
  parent_id uuid,
  is_folder boolean NOT NULL,
  has_unique_permissions boolean NOT NULL,
  scope_id uuid,
  title varchar(1024) NOT NULL,
  fields jsonb NOT NULL,
  created_at timestamptz NOT NULL,
  created_by uuid,
  updated_at timestamptz NOT NULL,
  updated_by uuid,
  deleted_at timestamptz,
  deleted_by uuid,
  version bigint NOT NULL DEFAULT 1);

-- DMS library (Papermerge-import layout): one home folder per user with unique permissions,
-- 4 sub-folders each, 50 documents per sub-folder => 1,000,000 documents.
INSERT INTO items
SELECT md5('hf'||i)::uuid, md5('tenant')::uuid, md5('list-dms')::uuid, md5('ct-folder')::uuid, NULL, true, true, md5('hf'||i)::uuid,
       'Home '||lpad(i::text,5,'0'), '{}'::jsonb, timestamptz '2024-01-01' + i * interval '1 minute', md5('u'||i)::uuid,
       timestamptz '2024-01-01' + i * interval '1 minute', md5('u'||i)::uuid, NULL, NULL, 1
FROM generate_series(1,5000) i;

INSERT INTO items
SELECT md5('sf'||i||'-'||k)::uuid, md5('tenant')::uuid, md5('list-dms')::uuid, md5('ct-folder')::uuid, md5('hf'||i)::uuid, true, false, md5('hf'||i)::uuid,
       'Folder '||k, '{}'::jsonb, timestamptz '2024-01-02' + i * interval '1 minute', md5('u'||i)::uuid,
       timestamptz '2024-01-02' + i * interval '1 minute', md5('u'||i)::uuid, NULL, NULL, 1
FROM generate_series(1,5000) i, generate_series(1,4) k;

INSERT INTO items
SELECT md5('d'||i||'-'||k||'-'||n)::uuid, md5('tenant')::uuid, md5('list-dms')::uuid, md5('ct-doc')::uuid, md5('sf'||i||'-'||k)::uuid, false,
       (n = 1 AND k = 1 AND i % 2 = 0),
       CASE WHEN (n = 1 AND k = 1 AND i % 2 = 0) THEN md5('d'||i||'-'||k||'-'||n)::uuid ELSE md5('hf'||i)::uuid END,
       'Document '||i||'-'||k||'-'||n,
       jsonb_build_object('documentDate', (date '2020-01-01' + ((i*31+k*7+n) % 1500))::text,
                          'tags', jsonb_build_array(md5('term'||((i+n)%300))::uuid::text),
                          'correspondent', md5('corr'||((i*n)%2000))::uuid::text),
       timestamptz '2024-02-01' + (i*200+k*50+n) * interval '1 second', md5('u'||i)::uuid,
       timestamptz '2024-02-01' + (i*200+k*50+n) * interval '1 second', md5('u'||i)::uuid, NULL, NULL, 1
FROM generate_series(1,5000) i, generate_series(1,4) k, generate_series(1,50) n;

-- A shared root folder that inherits from the list (visible to every workspace member): 10,000 documents.
INSERT INTO items VALUES (md5('pub')::uuid, md5('tenant')::uuid, md5('list-dms')::uuid, md5('ct-folder')::uuid, NULL, true, false, NULL,
  'Public', '{}'::jsonb, timestamptz '2024-01-01', NULL, timestamptz '2024-01-01', NULL, NULL, NULL, 1);
INSERT INTO items
SELECT md5('pd'||n)::uuid, md5('tenant')::uuid, md5('list-dms')::uuid, md5('ct-doc')::uuid, md5('pub')::uuid, false, false, NULL,
       'Public document '||n, jsonb_build_object('documentDate', (date '2020-01-01' + (n % 1500))::text),
       timestamptz '2024-03-01' + n * interval '1 second', NULL, timestamptz '2024-03-01' + n * interval '1 second', NULL, NULL, NULL, 1
FROM generate_series(1,10000) n;

-- CRM list: 500,000 deals, no folders, inheriting from the list. Owned by 200 sales users.
INSERT INTO items
SELECT md5('deal'||n)::uuid, md5('tenant')::uuid, md5('list-crm')::uuid, md5('ct-deal')::uuid, NULL, false, false, NULL,
       'Deal '||n,
       jsonb_build_object('stage', (ARRAY['Lead','Qualified','Proposal','Negotiation','Won','Lost'])[1 + n % 6],
                          'amount', (n * 37) % 100000,
                          'closeDate', (date '2024-01-01' + (n % 900))::text,
                          'assignedTo', jsonb_build_array(md5('u'||(1 + n % 200))::uuid::text),
                          'account', md5('acc'||(n % 20000))::uuid::text),
       timestamptz '2024-01-01' + n * interval '10 seconds', md5('u'||(1 + n % 200))::uuid,
       timestamptz '2024-01-01' + n * interval '10 seconds', md5('u'||(1 + n % 200))::uuid, NULL, NULL, 1
FROM generate_series(1,500000) n;

-- 50 task lists x 2,000 tasks, assigned among 1,000 users.
INSERT INTO items
SELECT md5('task'||l||'-'||n)::uuid, md5('tenant')::uuid, md5('list-tasks'||l)::uuid, md5('ct-task')::uuid, NULL, false, false, NULL,
       'Task '||l||'-'||n,
       jsonb_build_object('status', (ARRAY['NotStarted','InProgress','Completed','Completed'])[1 + n % 4],
                          'dueDate', (date '2024-06-01' + ((l*13+n) % 400))::text,
                          'assignedTo', jsonb_build_array(md5('u'||(1 + (l*7+n) % 1000))::uuid::text)),
       timestamptz '2024-01-01' + n * interval '1 minute', NULL, timestamptz '2024-01-01' + n * interval '1 minute', NULL, NULL, NULL, 1
FROM generate_series(1,50) l, generate_series(1,2000) n;

-- Indexes exactly as ListsDbContext + conventions declare them today.
CREATE INDEX ix_items_tenant ON items(tenant_id);
CREATE INDEX ix_items_list_parent ON items(list_id, parent_id);
CREATE INDEX ix_items_list_scope ON items(list_id, scope_id);
CREATE INDEX ix_items_fields ON items USING gin (fields jsonb_path_ops);

-- permission_grants as today (levels stored as strings).
CREATE TABLE permission_grants(
  id uuid PRIMARY KEY, tenant_id uuid NOT NULL, list_id uuid NOT NULL, object_id uuid NOT NULL,
  principal_type varchar(10) NOT NULL, principal_id uuid NOT NULL, level varchar(20) NOT NULL);
INSERT INTO permission_grants
SELECT md5('gr-hf'||i)::uuid, md5('tenant')::uuid, md5('list-dms')::uuid, md5('hf'||i)::uuid, 'User', md5('u'||i)::uuid, 'Manage'
FROM generate_series(1,5000) i;
INSERT INTO permission_grants
SELECT md5('gr-hfg'||i)::uuid, md5('tenant')::uuid, md5('list-dms')::uuid, md5('hf'||i)::uuid, 'Group', md5('g'||(i%50))::uuid, 'Read'
FROM generate_series(1,5000) i WHERE i % 5 = 0;
INSERT INTO permission_grants
SELECT md5('gr-d'||i)::uuid, md5('tenant')::uuid, md5('list-dms')::uuid, md5('d'||i||'-1-1')::uuid, 'User', md5('u'||i)::uuid, 'Manage'
FROM generate_series(2,5000,2) i;
INSERT INTO permission_grants
SELECT md5('gr-dn'||i)::uuid, md5('tenant')::uuid, md5('list-dms')::uuid, md5('d'||i||'-1-1')::uuid, 'User', md5('u'||(i % 5000 + 1))::uuid, 'Read'
FROM generate_series(2,5000,2) i;
CREATE UNIQUE INDEX ON permission_grants(object_id, principal_type, principal_id);
CREATE INDEX ON permission_grants(list_id);
CREATE INDEX ON permission_grants(tenant_id);

VACUUM ANALYZE;

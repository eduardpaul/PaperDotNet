using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using PaperDotNet.Import.Papermerge;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace PaperDotNet.IntegrationTests;

/// <summary>
/// PLT-15: a Papermerge 3.6 database (the tables the converter reads, as Alembic creates them) and media folder become a
/// package that imports with folders, owners, sharing, types, fields, tags, all versions and page texts.
/// </summary>
public sealed class PapermergeImportTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Schema = """
        CREATE TYPE owner_type_enum AS ENUM ('user', 'group');
        CREATE TYPE folder_type_enum AS ENUM ('home', 'inbox');
        CREATE TABLE alembic_version (version_num varchar(32) PRIMARY KEY);
        CREATE TABLE users (id uuid PRIMARY KEY, username varchar(150) UNIQUE NOT NULL, email varchar(254), password varchar(128) NOT NULL,
            first_name varchar(150), last_name varchar(150), is_superuser boolean NOT NULL DEFAULT false, is_staff boolean NOT NULL DEFAULT false,
            is_active boolean NOT NULL DEFAULT true, created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
            deleted_at timestamptz, archived_at timestamptz, created_by uuid, updated_by uuid, deleted_by uuid, archived_by uuid, date_joined timestamptz DEFAULT now());
        CREATE TABLE groups (id uuid PRIMARY KEY, name varchar NOT NULL, delete_me boolean);
        CREATE TABLE users_groups (id uuid PRIMARY KEY, group_id uuid REFERENCES groups(id), user_id uuid REFERENCES users(id));
        CREATE TABLE nodes (id uuid PRIMARY KEY, title varchar(200) NOT NULL, ctype varchar NOT NULL, lang varchar(8), parent_id uuid REFERENCES nodes(id),
            created_at timestamptz NOT NULL, updated_at timestamptz NOT NULL, deleted_at timestamptz, archived_at timestamptz,
            created_by uuid NOT NULL, updated_by uuid NOT NULL, deleted_by uuid, archived_by uuid);
        CREATE TABLE folders (node_id uuid PRIMARY KEY REFERENCES nodes(id));
        CREATE TABLE document_types (id uuid PRIMARY KEY, name varchar NOT NULL, path_template varchar);
        CREATE TABLE documents (node_id uuid PRIMARY KEY REFERENCES nodes(id), ocr boolean DEFAULT false, ocr_status varchar DEFAULT 'unknown',
            document_type_id uuid REFERENCES document_types(id));
        CREATE TABLE document_versions (id uuid PRIMARY KEY, number int NOT NULL, file_name varchar, size int DEFAULT 0, mime_type varchar NOT NULL,
            checksum varchar, checksum_algorithm varchar, document_id uuid REFERENCES documents(node_id), lang varchar DEFAULT 'deu', text text,
            page_count int DEFAULT 0, short_description varchar, is_original boolean DEFAULT false, source_version_id uuid, creation_reason varchar NOT NULL DEFAULT 'upload',
            created_at timestamptz NOT NULL, updated_at timestamptz NOT NULL, deleted_at timestamptz, archived_at timestamptz,
            created_by uuid NOT NULL, updated_by uuid NOT NULL, deleted_by uuid, archived_by uuid);
        CREATE TABLE pages (id uuid PRIMARY KEY, number int NOT NULL, page_count int NOT NULL, lang varchar DEFAULT 'deu', text text,
            document_version_id uuid REFERENCES document_versions(id));
        CREATE TABLE tags (id uuid PRIMARY KEY, name varchar NOT NULL, fg_color varchar, bg_color varchar, pinned boolean DEFAULT false, description varchar);
        CREATE TABLE nodes_tags (id serial PRIMARY KEY, node_id uuid REFERENCES nodes(id), tag_id uuid REFERENCES tags(id));
        CREATE TABLE custom_fields (id uuid PRIMARY KEY, name varchar(255) UNIQUE NOT NULL, type_handler varchar(50) NOT NULL, config jsonb DEFAULT '{}');
        CREATE TABLE custom_field_values (id uuid PRIMARY KEY, document_id uuid REFERENCES documents(node_id), field_id uuid REFERENCES custom_fields(id),
            value jsonb NOT NULL, value_text varchar, value_numeric numeric, value_date date, value_datetime timestamptz, value_boolean boolean,
            created_at timestamptz DEFAULT now(), updated_at timestamptz);
        CREATE TABLE document_types_custom_fields (id serial PRIMARY KEY, document_type_id uuid REFERENCES document_types(id),
            custom_field_id uuid REFERENCES custom_fields(id), position int DEFAULT 0);
        CREATE TABLE ownerships (id serial PRIMARY KEY, owner_type varchar(20) NOT NULL, owner_id uuid NOT NULL, resource_type varchar(50) NOT NULL,
            resource_id uuid NOT NULL, created_at timestamptz DEFAULT now());
        CREATE TABLE special_folders (id uuid PRIMARY KEY, owner_type owner_type_enum NOT NULL, owner_id uuid NOT NULL, folder_type folder_type_enum NOT NULL,
            folder_id uuid NOT NULL REFERENCES folders(node_id), created_at timestamptz DEFAULT now(), updated_at timestamptz DEFAULT now());
        CREATE TABLE roles (id uuid PRIMARY KEY, name varchar NOT NULL);
        CREATE TABLE permissions (id uuid PRIMARY KEY, name varchar, codename varchar UNIQUE);
        CREATE TABLE roles_permissions (role_id uuid REFERENCES roles(id), permission_id uuid REFERENCES permissions(id));
        CREATE TABLE shared_nodes (id uuid PRIMARY KEY, node_id uuid REFERENCES nodes(id), user_id uuid, group_id uuid, role_id uuid REFERENCES roles(id),
            owner_id uuid, created_at timestamptz DEFAULT now(), updated_at timestamptz DEFAULT now());
        """;

    private static readonly Guid Alice = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid Accounting = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid AliceHome = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid AliceInbox = Guid.Parse("30000000-0000-0000-0000-000000000002");
    private static readonly Guid GroupHome = Guid.Parse("30000000-0000-0000-0000-000000000003");
    private static readonly Guid Invoices = Guid.Parse("30000000-0000-0000-0000-000000000004");
    private static readonly Guid Invoice = Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static readonly Guid Scan = Guid.Parse("40000000-0000-0000-0000-000000000002");
    private static readonly Guid Policy = Guid.Parse("40000000-0000-0000-0000-000000000003");
    private static readonly Guid Trashed = Guid.Parse("40000000-0000-0000-0000-000000000004");
    private static readonly Guid InvoiceV1 = Guid.Parse("50000000-0000-0000-0000-000000000001");
    private static readonly Guid InvoiceV2 = Guid.Parse("50000000-0000-0000-0000-000000000002");
    private static readonly Guid ScanV1 = Guid.Parse("50000000-0000-0000-0000-000000000003");
    private static readonly Guid PolicyV1 = Guid.Parse("50000000-0000-0000-0000-000000000004");
    private static readonly DateTimeOffset Created = new(2023, 3, 14, 9, 30, 0, TimeSpan.Zero);

    private static byte[] Pdf(params string[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages)
        {
            builder.AddPage(PageSize.A4).AddText(text, 24, new PdfPoint(40, 760), font);
        }

        return builder.Build();
    }

    private static string Data => $$$"""
        INSERT INTO alembic_version VALUES ('bb19aac50bca');
        INSERT INTO users (id, username, email, password, first_name, last_name, is_active, is_superuser) VALUES
            ('{{{Alice}}}', 'alice', 'alice@example.com', 'x', 'Alice', 'Archer', true, true),
            ('{{{Bob}}}', 'bob', 'bob@example.com', 'x', null, null, true, false),
            ('10000000-0000-0000-0000-000000000003', 'carol', 'carol@example.com', 'x', null, null, false, false);
        INSERT INTO groups VALUES ('{{{Accounting}}}', 'Accounting', false);
        INSERT INTO users_groups VALUES (gen_random_uuid(), '{{{Accounting}}}', '{{{Alice}}}'), (gen_random_uuid(), '{{{Accounting}}}', '{{{Bob}}}');
        INSERT INTO nodes (id, title, ctype, lang, parent_id, created_at, updated_at, created_by, updated_by, deleted_at) VALUES
            ('{{{AliceHome}}}', '.home', 'folder', 'deu', null, '{{{Created:O}}}', '{{{Created:O}}}', '{{{Alice}}}', '{{{Alice}}}', null),
            ('{{{AliceInbox}}}', '.inbox', 'folder', 'deu', null, '{{{Created:O}}}', '{{{Created:O}}}', '{{{Alice}}}', '{{{Alice}}}', null),
            ('{{{GroupHome}}}', '.home', 'folder', 'deu', null, '{{{Created:O}}}', '{{{Created:O}}}', '{{{Alice}}}', '{{{Alice}}}', null),
            ('{{{Invoices}}}', 'Invoices', 'folder', 'deu', '{{{AliceHome}}}', '{{{Created:O}}}', '{{{Created:O}}}', '{{{Alice}}}', '{{{Alice}}}', null),
            ('{{{Invoice}}}', 'Invoice 42', 'document', 'deu', '{{{Invoices}}}', '{{{Created:O}}}', '{{{Created.AddDays(2):O}}}', '{{{Alice}}}', '{{{Bob}}}', null),
            ('{{{Scan}}}', 'Scan', 'document', 'deu', '{{{AliceInbox}}}', '{{{Created:O}}}', '{{{Created:O}}}', '{{{Alice}}}', '{{{Alice}}}', null),
            ('{{{Policy}}}', 'Policy', 'document', 'eng', '{{{GroupHome}}}', '{{{Created:O}}}', '{{{Created:O}}}', '{{{Bob}}}', '{{{Bob}}}', null),
            ('{{{Trashed}}}', 'Old', 'document', 'deu', '{{{AliceHome}}}', '{{{Created:O}}}', '{{{Created:O}}}', '{{{Alice}}}', '{{{Alice}}}', now());
        INSERT INTO folders VALUES ('{{{AliceHome}}}'), ('{{{AliceInbox}}}'), ('{{{GroupHome}}}'), ('{{{Invoices}}}');
        INSERT INTO special_folders (id, owner_type, owner_id, folder_type, folder_id) VALUES
            (gen_random_uuid(), 'user', '{{{Alice}}}', 'home', '{{{AliceHome}}}'),
            (gen_random_uuid(), 'user', '{{{Alice}}}', 'inbox', '{{{AliceInbox}}}'),
            (gen_random_uuid(), 'group', '{{{Accounting}}}', 'home', '{{{GroupHome}}}');
        INSERT INTO ownerships (owner_type, owner_id, resource_type, resource_id) VALUES
            ('user', '{{{Alice}}}', 'node', '{{{AliceHome}}}'), ('user', '{{{Alice}}}', 'node', '{{{AliceInbox}}}'), ('group', '{{{Accounting}}}', 'node', '{{{GroupHome}}}'),
            ('user', '{{{Alice}}}', 'node', '{{{Invoices}}}'), ('user', '{{{Alice}}}', 'node', '{{{Invoice}}}'), ('user', '{{{Alice}}}', 'node', '{{{Scan}}}'),
            ('group', '{{{Accounting}}}', 'node', '{{{Policy}}}');
        INSERT INTO document_types VALUES ('60000000-0000-0000-0000-000000000001', 'Invoice', '/Invoices/{{ document.cf["Due date"] }}');
        INSERT INTO custom_fields VALUES
            ('70000000-0000-0000-0000-000000000001', 'Total amount', 'monetary', '{"currency": "CHF"}'),
            ('70000000-0000-0000-0000-000000000002', 'Paid', 'boolean', '{}'),
            ('70000000-0000-0000-0000-000000000003', 'Due date', 'date', '{}'),
            ('70000000-0000-0000-0000-000000000004', 'Category', 'select', '{"options": [{"label": "Office", "value": "Office"}, {"label": "Travel", "value": "Travel"}]}');
        INSERT INTO document_types_custom_fields (document_type_id, custom_field_id, position) VALUES
            ('60000000-0000-0000-0000-000000000001', '70000000-0000-0000-0000-000000000001', 0),
            ('60000000-0000-0000-0000-000000000001', '70000000-0000-0000-0000-000000000002', 1),
            ('60000000-0000-0000-0000-000000000001', '70000000-0000-0000-0000-000000000003', 2),
            ('60000000-0000-0000-0000-000000000001', '70000000-0000-0000-0000-000000000004', 3);
        INSERT INTO documents VALUES ('{{{Invoice}}}', true, 'success', '60000000-0000-0000-0000-000000000001'),
            ('{{{Scan}}}', false, 'unknown', null), ('{{{Policy}}}', false, 'unknown', null), ('{{{Trashed}}}', false, 'unknown', null);
        INSERT INTO custom_field_values (id, document_id, field_id, value, value_text, value_numeric, value_date, value_boolean) VALUES
            (gen_random_uuid(), '{{{Invoice}}}', '70000000-0000-0000-0000-000000000001', '{"value": 120.5}', null, 120.5, null, null),
            (gen_random_uuid(), '{{{Invoice}}}', '70000000-0000-0000-0000-000000000002', '{"value": true}', null, null, null, true),
            (gen_random_uuid(), '{{{Invoice}}}', '70000000-0000-0000-0000-000000000003', '{"value": "2024-05-31"}', null, null, '2024-05-31', null),
            (gen_random_uuid(), '{{{Invoice}}}', '70000000-0000-0000-0000-000000000004', '{"value": "Office"}', 'Office', null, null, null);
        INSERT INTO document_versions (id, number, file_name, mime_type, document_id, lang, creation_reason, is_original, created_at, updated_at, created_by, updated_by) VALUES
            ('{{{InvoiceV1}}}', 1, 'invoice.pdf', 'application/pdf', '{{{Invoice}}}', 'deu', 'upload', true, '{{{Created:O}}}', '{{{Created:O}}}', '{{{Alice}}}', '{{{Alice}}}'),
            ('{{{InvoiceV2}}}', 2, 'invoice.pdf', 'application/pdf', '{{{Invoice}}}', 'deu', 'page_edit', false, '{{{Created.AddDays(2):O}}}', '{{{Created.AddDays(2):O}}}', '{{{Bob}}}', '{{{Bob}}}'),
            ('{{{ScanV1}}}', 1, 'scan.pdf', 'application/pdf', '{{{Scan}}}', 'deu', 'upload', true, '{{{Created:O}}}', '{{{Created:O}}}', '{{{Alice}}}', '{{{Alice}}}'),
            ('{{{PolicyV1}}}', 1, 'policy.pdf', 'application/pdf', '{{{Policy}}}', 'eng', 'upload', true, '{{{Created:O}}}', '{{{Created:O}}}', '{{{Bob}}}', '{{{Bob}}}');
        INSERT INTO pages (id, number, page_count, lang, text, document_version_id) VALUES
            (gen_random_uuid(), 1, 2, 'deu', 'Rechnung Seite eins', '{{{InvoiceV1}}}'),
            (gen_random_uuid(), 2, 2, 'deu', 'Rechnung Seite zwei', '{{{InvoiceV1}}}'),
            (gen_random_uuid(), 1, 1, 'deu', 'Rechnung ocelot', '{{{InvoiceV2}}}');
        INSERT INTO tags (id, name, bg_color, description) VALUES
            ('80000000-0000-0000-0000-000000000001', 'Finance', '#c41fff', 'Money matters'),
            ('80000000-0000-0000-0000-000000000002', 'Urgent/Now', '#ff0000', null),
            ('80000000-0000-0000-0000-000000000003', 'Finance', '#00ff00', null);
        INSERT INTO nodes_tags (node_id, tag_id) VALUES ('{{{Invoice}}}', '80000000-0000-0000-0000-000000000001'),
            ('{{{Invoice}}}', '80000000-0000-0000-0000-000000000002'), ('{{{Invoices}}}', '80000000-0000-0000-0000-000000000003');
        INSERT INTO roles VALUES ('90000000-0000-0000-0000-000000000001', 'Viewer');
        INSERT INTO permissions VALUES ('91000000-0000-0000-0000-000000000001', 'View nodes', 'node.view');
        INSERT INTO roles_permissions VALUES ('90000000-0000-0000-0000-000000000001', '91000000-0000-0000-0000-000000000001');
        INSERT INTO shared_nodes (id, node_id, user_id, role_id, owner_id) VALUES
            (gen_random_uuid(), '{{{Invoices}}}', '{{{Bob}}}', '90000000-0000-0000-0000-000000000001', '{{{Alice}}}');
        """;

    private static async Task WriteVersionAsync(string media, Guid version, string name, byte[] content)
    {
        var id = version.ToString();
        var directory = Path.Combine(media, "docvers", id[..2], id[2..4], id);
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, name), content, Ct);
    }

    [Fact]
    public async Task A_papermerge_archive_imports_with_owners_sharing_types_tags_versions_and_texts()
    {
        Assert.SkipUnless(PaperDotNetApiFactory.Provider == "postgresql", "Papermerge databases are PostgreSQL.");

        // A Papermerge database next to the test database, and its media folder.
        var database = $"papermerge_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(factory.AdminConnectionString))
        {
            await admin.OpenAsync(Ct);
            await using var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin);
            await create.ExecuteNonQueryAsync(Ct);
        }

        var connectionString = new NpgsqlConnectionStringBuilder(factory.AdminConnectionString) { Database = database }.ConnectionString;
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(Ct);
            await using var schema = new NpgsqlCommand(Schema + Data, connection);
            await schema.ExecuteNonQueryAsync(Ct);
        }

        var media = Directory.CreateTempSubdirectory("pm_media_");
        try
        {
            await WriteVersionAsync(media.FullName, InvoiceV1, "invoice.pdf", Pdf("Rechnung Seite eins", "Rechnung Seite zwei"));
            await WriteVersionAsync(media.FullName, InvoiceV2, "invoice.pdf", Pdf("Rechnung ocelot"));
            await WriteVersionAsync(media.FullName, PolicyV1, "policy.pdf", Pdf("Travel policy"));
            // The scan's file is missing.

            using var package = new MemoryStream();
            var conversion = await PapermergeConverter.ConvertAsync(new PapermergeImportOptions(connectionString, media.FullName), package, Ct);
            Assert.Equal(["alice", "bob"], conversion.Users.Select(u => u.UserName));
            Assert.Equal(3, conversion.Documents); // The trashed one is left out.
            Assert.Equal(3, conversion.Versions);
            Assert.Contains(conversion.Warnings, w => w.Contains("'Scan' version 1", StringComparison.Ordinal));
            Assert.Contains(conversion.Warnings, w => w.Contains("path template", StringComparison.Ordinal));
            Assert.Contains(conversion.Warnings, w => w.Contains("Urgent-Now", StringComparison.Ordinal));

            // Unsupported schema versions are refused.
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync(Ct);
                await using var update = new NpgsqlCommand("UPDATE alembic_version SET version_num = '0000future'", connection);
                await update.ExecuteNonQueryAsync(Ct);
            }

            await Assert.ThrowsAsync<PapermergeImportException>(() =>
                PapermergeConverter.ConvertAsync(new PapermergeImportOptions(connectionString, media.FullName), Stream.Null, Ct));

            // Import: the users first (the admin command creates them without passwords), then the package.
            await factory.CreateTenantAsync("papermerge");
            var client = await ApiClient.CreateAsync(factory, "papermerge");
            foreach (var user in conversion.Users)
            {
                await client.PostAsJsonAsync("/v1.0/users", new { userName = user.UserName, password = $"{user.UserName}-password-1" }, Ct);
            }

            var content = new ByteArrayContent(package.ToArray());
            content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            var applied = await client.PostAsync("/v1.0/provisioning/apply", content, Ct);
            var body = await applied.Content.ReadAsStringAsync(Ct);
            Assert.True(applied.StatusCode == HttpStatusCode.OK, body);

            var ws = (await (await client.GetAsync("/v1.0/workspaces", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray()
                .Single(w => w.GetProperty("name").GetString() == "Papermerge").GetProperty("id").GetGuid();
            var library = (await (await client.GetAsync($"/v1.0/workspaces/{ws}/lists", Ct)).ReadJsonAsync()).EnumerateArray()
                .Single(l => l.GetProperty("name").GetString() == "Documents").GetProperty("id").GetGuid();
            var items = (await (await client.GetAsync($"/v1.0/workspaces/{ws}/lists/{library}/items?$top=100", Ct)).ReadJsonAsync())
                .GetProperty("value").EnumerateArray().ToList();
            JsonElement ByTitle(string title) => items.Single(i => i.GetProperty("fields").GetProperty("title").GetString() == title);
            Guid Id(JsonElement item) => item.GetProperty("id").GetGuid();
            Guid? Parent(JsonElement item) => item.TryGetProperty("parentId", out var p) && p.ValueKind == JsonValueKind.String ? p.GetGuid() : null;

            // Owners' folders: alice's home with her inbox and folders, the group's home.
            var aliceFolder = ByTitle("alice");
            Assert.Equal(Id(aliceFolder), Parent(ByTitle("Inbox")));
            Assert.Equal(Id(aliceFolder), Parent(ByTitle("Invoices")));
            Assert.Equal(Id(ByTitle("Accounting (group)")), Parent(ByTitle("Policy")));
            Assert.DoesNotContain(items, i => i.GetProperty("fields").GetProperty("title").GetString() == "Old");

            // The invoice: its type, field values, tags, stamps and both versions with their texts.
            var invoice = ByTitle("Invoice 42");
            var fields = invoice.GetProperty("fields");
            Assert.Equal(120.5m, fields.GetProperty("totalAmount").GetDecimal());
            Assert.True(fields.GetProperty("paid").GetBoolean());
            Assert.Equal("2024-05-31", fields.GetProperty("dueDate").GetString());
            Assert.Equal("Office", fields.GetProperty("category").GetString());
            Assert.Equal(2, fields.GetProperty("tags").GetArrayLength());
            Assert.Equal(Created, invoice.GetProperty("createdAt").GetDateTimeOffset());
            var invoiceUrl = $"/v1.0/workspaces/{ws}/lists/{library}/items/{Id(invoice)}";
            var versions = (await (await client.GetAsync($"{invoiceUrl}/file/versions", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray().ToList();
            Assert.Equal(["page_edit", "upload"], versions.Select(v => v.GetProperty("source").GetString()));
            Assert.All(versions, v => Assert.Equal("succeeded", v.GetProperty("processingStatus").GetString()));
            Assert.Equal("deu", versions[0].GetProperty("textLanguage").GetString());
            await Eventually.WaitForAsync<bool>(async () =>
                (await (await client.GetAsync("/v1.0/search?q=ocelot", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray()
                    .Any(h => h.GetProperty("id").GetGuid() == Id(invoice)) ? true : null);

            // Sharing: bob reads the shared folder, not alice's inbox; he contributes to the group's folder.
            var bob = await ApiClient.CreateAsync(factory, "papermerge", "bob", "bob-password-1");
            var bobItems = (await (await bob.GetAsync($"/v1.0/workspaces/{ws}/lists/{library}/items?$top=100", Ct)).ReadJsonAsync())
                .GetProperty("value").EnumerateArray().Select(i => i.GetProperty("fields").GetProperty("title").GetString()).ToList();
            Assert.Contains("Invoice 42", bobItems);
            Assert.Contains("Policy", bobItems);
            Assert.DoesNotContain("Inbox", bobItems);
            Assert.DoesNotContain("Scan", bobItems);

            // Tags became terms (same names merged; '/' replaced).
            var sets = (await (await client.GetAsync("/v1.0/termStore/sets", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray();
            var tagSet = sets.Single(s => s.GetProperty("name").GetString() == "Tags").GetProperty("id").GetGuid();
            var terms = (await (await client.GetAsync($"/v1.0/termStore/sets/{tagSet}/terms", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray()
                .Select(t => t.GetProperty("name").GetString()).ToList();
            Assert.Equal(["Finance", "Urgent-Now"], terms.Order(StringComparer.Ordinal));

            // Importing again changes nothing.
            var again = new ByteArrayContent(package.ToArray());
            again.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            var second = await (await client.PostAsync("/v1.0/provisioning/apply", again, Ct)).ReadJsonAsync();
            Assert.DoesNotContain(second.GetProperty("changes").EnumerateArray(), c => c.GetProperty("kind").GetString() is "items" or "files");
        }
        finally
        {
            media.Delete(recursive: true);
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(factory.AdminConnectionString);
            await admin.OpenAsync(Ct);
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(Ct);
        }
    }
}

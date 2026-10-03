using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ZvecSpike;
using static ZvecSpike.Native;

// Spike for ADR-0044 / docs/search-zvec-plan.md WP0. Usage:
//   ZvecSpike probes <dir>          functional questions (S1, S2, S3, S5, S6, filters, dynamic schema)
//   ZvecSpike scale <dir> <rows>    S4 / S7 timings
//   ZvecSpike crash <dir>           S8 (spawns a writer, kills it, reopens)
//   ZvecSpike writer <dir>          (internal) writes until killed
unsafe
{
    var mode = args[0];
    var dir = args[1];
    var config = zvec_config_data_create();
    zvec_config_data_set_memory_limit(config, 2UL << 30);
    Check(zvec_initialize(config), "initialize");

    switch (mode)
    {
        case "probes": Probes.Run(dir); break;
        case "scale": Scale.Run(dir, int.Parse(args[2])); break;
        case "crash": Crash.Run(dir); break;
        case "writer": Crash.Writer(dir); break;
        case "escape": Escape.Run(dir); break;
    }
}

internal static unsafe class Z
{
    public const int Dim = 8;

    public static nint CreateCollection(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        var schema = zvec_collection_schema_create("search");
        void Scalar(string name, uint type, bool index, bool range = false)
        {
            var field = zvec_field_schema_create(name, type, nullable: true, 0);
            if (index)
            {
                var p = zvec_index_params_create(IndexInvert);
                Check(zvec_index_params_set_invert_params(p, range, false), "invert params");
                Check(zvec_field_schema_set_index_params(field, p), "set index");
            }

            Check(zvec_collection_schema_add_field(schema, field), "add " + name);
        }

        void Text(string name, string tokenizer, string[] filters, string? extra)
        {
            var field = zvec_field_schema_create(name, TString, nullable: true, 0);
            var p = zvec_index_params_create(IndexFts);
            var list = zvec_string_array_create((nuint)filters.Length);
            for (var i = 0; i < filters.Length; i++)
            {
                zvec_string_array_add(list, (nuint)i, filters[i]);
            }

            Check(zvec_index_params_set_fts_params(p, tokenizer, list, extra), "fts params " + name);
            Check(zvec_field_schema_set_index_params(field, p), "set fts " + name);
            Check(zvec_collection_schema_add_field(schema, field), "add " + name);
        }

        Scalar("doc_id", TString, true);
        Scalar("kind", TString, true);
        Scalar("scope_id", TString, true);
        Scalar("workspace_id", TString, true);
        Scalar("term_ids", ArrayString, true);
        Scalar("updated_at", TInt64, true, range: true);
        Scalar("published", TBool, true);
        Scalar("page", TInt32, false);
        Text("title", "standard", ["lowercase", "ascii_folding"], null);
        Text("text", "standard", ["lowercase", "ascii_folding"], null);
        Text("text_english", "standard", ["lowercase", "ascii_folding", "stemmer"], """{"stemmer_lang":"english"}""");
        Text("all", "whitespace", [], null);
        Text("text_ngram", "ngram", ["lowercase"], """{"ngram_min":3,"ngram_max":3}""");

        var vec = zvec_field_schema_create("embedding", VectorFp32, nullable: true, Dim);
        var hnsw = zvec_index_params_create(IndexHnsw);
        Check(zvec_index_params_set_metric_type(hnsw, MetricCosine), "metric");
        Check(zvec_index_params_set_hnsw_params(hnsw, 16, 100), "hnsw");
        Check(zvec_field_schema_set_index_params(vec, hnsw), "vec index");
        Check(zvec_collection_schema_add_field(schema, vec), "add embedding");

        Check(zvec_collection_create_and_open(path, schema, 0, out var collection), "create_and_open");
        return collection;
    }

    public static nint Open(string path)
    {
        Check(zvec_collection_open(path, 0, out var collection), "open");
        return collection;
    }

    public sealed class Doc
    {
        public nint Handle { get; } = zvec_doc_create();

        private readonly List<nint> _strings = [];

        public Doc(string pk) => zvec_doc_set_pk(Handle, pk);

        public Doc Str(string name, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            fixed (byte* p = bytes)
            {
                Check(zvec_doc_add_field_by_value(Handle, name, TString, p, (nuint)bytes.Length), "add " + name);
            }

            return this;
        }

        public Doc Bool(string name, bool value)
        {
            var b = (byte)(value ? 1 : 0);
            Check(zvec_doc_add_field_by_value(Handle, name, TBool, &b, 1), "add " + name);
            return this;
        }

        public Doc Long(string name, long value)
        {
            Check(zvec_doc_add_field_by_value(Handle, name, TInt64, &value, 8), "add " + name);
            return this;
        }

        public Doc Int(string name, int value)
        {
            Check(zvec_doc_add_field_by_value(Handle, name, TInt32, &value, 4), "add " + name);
            return this;
        }

        public Doc Dbl(string name, double value)
        {
            Check(zvec_doc_add_field_by_value(Handle, name, TDouble, &value, 8), "add " + name);
            return this;
        }

        public Doc Vec(string name, float[] value)
        {
            fixed (float* p = value)
            {
                Check(zvec_doc_add_field_by_value(Handle, name, VectorFp32, p, (nuint)(value.Length * 4)), "add " + name);
            }

            return this;
        }

        /// <summary>String arrays always as zvec_string_t* pointers (the packed form is guessed from the byte size).</summary>
        public Doc Strs(string name, string[] values)
        {
            var pointers = new nint[values.Length];
            for (var i = 0; i < values.Length; i++)
            {
                pointers[i] = zvec_string_create(values[i]);
                _strings.Add(pointers[i]);
            }

            fixed (nint* p = pointers)
            {
                Check(zvec_doc_add_field_by_value(Handle, name, ArrayString, p, (nuint)(values.Length * sizeof(nint))), "add " + name);
            }

            return this;
        }

        public void Free()
        {
            zvec_doc_destroy(Handle);
            foreach (var s in _strings)
            {
                zvec_free_string(s);
            }
        }
    }

    public static (nuint Ok, nuint Failed) Write(nint collection, string op, IReadOnlyList<Doc> docs)
    {
        var handles = docs.Select(d => d.Handle).ToArray();
        nuint ok, failed;
        int code;
        fixed (nint* p = handles)
        {
            code = op switch
            {
                "insert" => zvec_collection_insert(collection, p, (nuint)handles.Length, out ok, out failed),
                "upsert" => zvec_collection_upsert(collection, p, (nuint)handles.Length, out ok, out failed),
                _ => zvec_collection_update(collection, p, (nuint)handles.Length, out ok, out failed),
            };
        }

        foreach (var d in docs)
        {
            d.Free();
        }

        Check(code, op);
        return (ok, failed);
    }

    public sealed record Row(string Pk, float Score, Dictionary<string, string?> Fields);

    public static List<Row> Fts(nint collection, string field, string? match, string? queryString, string? filter, int topk, params string[] outputs)
    {
        var q = zvec_vector_query_create();
        Check(zvec_vector_query_set_field_name(q, field), "field");
        Check(zvec_vector_query_set_topk(q, topk), "topk");
        if (filter is not null)
        {
            Check(zvec_vector_query_set_filter(q, filter), "filter");
        }

        var fts = zvec_fts_create();
        if (match is not null)
        {
            Check(zvec_fts_set_match_string(fts, match), "match");
        }

        if (queryString is not null)
        {
            Check(zvec_fts_set_query_string(fts, queryString), "query string");
        }

        Check(zvec_vector_query_set_fts(q, fts), "set fts");
        zvec_fts_destroy(fts);
        SetOutputs(outputs, (p, n) => zvec_vector_query_set_output_fields(q, p, n));
        var code = zvec_collection_query(collection, q, out var docs, out var count);
        zvec_vector_query_destroy(q);
        Check(code, "query");
        return Read(docs, count, outputs);
    }

    public static List<Row> Vector(nint collection, float[] vector, string? filter, int topk, params string[] outputs)
    {
        var q = zvec_vector_query_create();
        Check(zvec_vector_query_set_field_name(q, "embedding"), "field");
        fixed (float* p = vector)
        {
            Check(zvec_vector_query_set_query_vector(q, p, (nuint)(vector.Length * 4)), "vector");
        }

        Check(zvec_vector_query_set_topk(q, topk), "topk");
        if (filter is not null)
        {
            Check(zvec_vector_query_set_filter(q, filter), "filter");
        }

        SetOutputs(outputs, (p, n) => zvec_vector_query_set_output_fields(q, p, n));
        var code = zvec_collection_query(collection, q, out var docs, out var count);
        zvec_vector_query_destroy(q);
        Check(code, "vector query");
        return Read(docs, count, outputs);
    }

    /// <summary>Hybrid: one full-text route per field plus a vector route, fused with RRF.</summary>
    public static List<Row> Hybrid(nint collection, string match, string[] textFields, float[]? vector, string? filter, int topk, params string[] outputs)
    {
        var mq = zvec_multi_query_create();
        var subs = new List<nint>();
        foreach (var field in textFields)
        {
            var s = zvec_sub_query_create();
            Check(zvec_sub_query_set_field_name(s, field), "sub field");
            var fts = zvec_fts_create();
            Check(zvec_fts_set_match_string(fts, match), "sub match");
            Check(zvec_sub_query_set_fts(s, fts), "sub fts");
            zvec_fts_destroy(fts);
            Check(zvec_sub_query_set_num_candidates(s, topk), "candidates");
            Check(zvec_multi_query_add_sub_query(mq, s), "add sub");
            subs.Add(s);
        }

        if (vector is not null)
        {
            var s = zvec_sub_query_create();
            Check(zvec_sub_query_set_field_name(s, "embedding"), "sub field");
            fixed (float* p = vector)
            {
                Check(zvec_sub_query_set_query_vector(s, p, (nuint)(vector.Length * 4)), "sub vector");
            }

            Check(zvec_sub_query_set_num_candidates(s, topk), "candidates");
            Check(zvec_multi_query_add_sub_query(mq, s), "add sub");
            subs.Add(s);
        }

        Check(zvec_multi_query_set_topk(mq, topk), "mq topk");
        Check(zvec_multi_query_set_rerank_rrf(mq, 60), "rrf");
        if (filter is not null)
        {
            Check(zvec_multi_query_set_filter(mq, filter), "mq filter");
        }

        SetOutputs(outputs, (p, n) => zvec_multi_query_set_output_fields(mq, p, n));
        var code = zvec_collection_multi_query(collection, mq, out var docs, out var count);
        foreach (var s in subs)
        {
            zvec_sub_query_destroy(s);
        }

        zvec_multi_query_destroy(mq);
        Check(code, "multi query");
        return Read(docs, count, outputs);
    }

    public static List<Row> Fetch(nint collection, string[] pks, params string[] outputs)
    {
        var pkPtrs = pks.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
        var outPtrs = outputs.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
        try
        {
            fixed (nint* p = pkPtrs)
            fixed (nint* o = outPtrs)
            {
                Check(zvec_collection_fetch(collection, p, (nuint)pks.Length, outputs.Length == 0 ? null : o, (nuint)outputs.Length, false, out var docs, out var found), "fetch");
                return Read(docs, found, outputs);
            }
        }
        finally
        {
            foreach (var p in pkPtrs.Concat(outPtrs))
            {
                Marshal.FreeCoTaskMem(p);
            }
        }
    }

    private delegate int OutputSetter(nint* fields, nuint count);

    private static void SetOutputs(string[] outputs, OutputSetter set)
    {
        if (outputs.Length == 0)
        {
            return;
        }

        var ptrs = outputs.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
        try
        {
            fixed (nint* p = ptrs)
            {
                Check(set(p, (nuint)ptrs.Length), "output fields");
            }
        }
        finally
        {
            foreach (var p in ptrs)
            {
                Marshal.FreeCoTaskMem(p);
            }
        }
    }

    private static List<Row> Read(nint* docs, nuint count, string[] outputs)
    {
        var rows = new List<Row>();
        for (nuint i = 0; i < count; i++)
        {
            var d = docs[i];
            var fields = new Dictionary<string, string?>();
            foreach (var f in outputs)
            {
                fields[f] = ReadField(d, f);
            }

            rows.Add(new Row(Marshal.PtrToStringUTF8(zvec_doc_get_pk_pointer(d)) ?? "?", zvec_doc_get_score(d), fields));
        }

        if (count > 0)
        {
            zvec_docs_free(docs, count);
        }

        return rows;
    }

    private static string? ReadField(nint doc, string field)
    {
        if (!zvec_doc_has_field(doc, field))
        {
            return "<absent>";
        }

        if (zvec_doc_is_field_null(doc, field))
        {
            return "<null>";
        }

        if (zvec_doc_get_field_value_pointer(doc, field, TString, out var value, out var size) == 0)
        {
            return Marshal.PtrToStringUTF8(value, (int)size);
        }

        long l;
        if (zvec_doc_get_field_value_basic(doc, field, TInt64, &l, 8) == 0)
        {
            return l.ToString();
        }

        double dbl;
        if (zvec_doc_get_field_value_basic(doc, field, TDouble, &dbl, 8) == 0)
        {
            return dbl.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return "<unreadable>";
    }

    public static float[] Vec(int axis, float noise = 0)
    {
        var v = new float[Dim];
        v[axis % Dim] = 1;
        v[(axis + 1) % Dim] = noise;
        return v;
    }
}

internal static class Probes
{
    private static void Answer(string id, string question, bool yes, string detail) =>
        Console.WriteLine($"{id} | {question} | {(yes ? "YES" : "NO")} | {detail}");

    public static void Run(string dir)
    {
        var path = Path.Combine(dir, "probes");
        var c = Z.CreateCollection(path);
        var scopeA = Guid.NewGuid().ToString("N");
        var scopeB = Guid.NewGuid().ToString("N");
        var termA = Guid.NewGuid().ToString("N");
        var termB = Guid.NewGuid().ToString("N");

        // Head and chunk rows; the chunk row has no embedding yet (S1).
        Z.Write(c, "insert",
        [
            new Z.Doc("d1").Str("doc_id", "d1").Str("kind", "doc").Str("scope_id", scopeA).Str("workspace_id", "w1")
                .Strs("term_ids", [termA]).Long("updated_at", 1000).Bool("published", true)
                .Str("title", "Invoice ACME March").Str("text", "total 120 euro").Str("text_english", "total 120 euro").Str("all", "pdn")
                .Vec("embedding", Z.Vec(0)),
            new Z.Doc("d1.g1.0").Str("doc_id", "d1").Str("kind", "chunk").Str("scope_id", scopeA).Str("workspace_id", "w1")
                .Strs("term_ids", [termA]).Long("updated_at", 1000).Bool("published", true).Int("page", 1)
                .Str("text", "machine learning invoices are processed").Str("text_english", "machine learning invoices are processed").Str("text_ngram", "machine learning invoices are processed").Str("all", "pdn"),
            new Z.Doc("d2").Str("doc_id", "d2").Str("kind", "doc").Str("scope_id", scopeB).Str("workspace_id", "w2")
                .Strs("term_ids", [termB]).Long("updated_at", 2000).Bool("published", true)
                .Str("title", "Contract Globex").Str("text", "deep learning clause").Str("text_ngram", "deep learning clause").Str("text_english", "deep learning clause").Str("all", "pdn")
                .Vec("embedding", Z.Vec(3)),
        ]);
        Answer("S1", "Row without a vector accepted", true, "chunk row inserted with a nullable embedding column left out");

        // S5: read your writes without flush.
        var found = Z.Fts(c, "text", "learning", null, null, 10, "doc_id");
        Answer("S5", "Query sees writes before flush", found.Count == 2, $"{found.Count} hits for 'learning' right after insert: {string.Join(",", found.Select(r => r.Pk))}");

        var vec = Z.Vector(c, Z.Vec(0), null, 10, "doc_id");
        Answer("S1b", "Vector query skips rows without a vector", vec.All(r => r.Pk != "d1.g1.0"), $"vector hits: {string.Join(",", vec.Select(r => $"{r.Pk}:{r.Score:F2}"))}");

        // Embed the chunk later.
        Z.Write(c, "update", [new Z.Doc("d1.g1.0").Vec("embedding", Z.Vec(5))]);
        vec = Z.Vector(c, Z.Vec(5), null, 10, "doc_id");
        Answer("S1c", "Vector added later by update is found", vec.FirstOrDefault()?.Pk == "d1.g1.0", $"top: {vec.FirstOrDefault()?.Pk}");

        // S2: partial update keeps other columns.
        var fetched = Z.Fetch(c, ["d1.g1.0"], "text", "scope_id", "page");
        Answer("S2", "update changes only given columns", fetched.Count == 1 && fetched[0].Fields["text"]?.StartsWith("machine", StringComparison.Ordinal) == true,
            $"after vector-only update: text='{fetched.FirstOrDefault()?.Fields["text"]}', scope={fetched.FirstOrDefault()?.Fields["scope_id"]?[..6]}, page={fetched.FirstOrDefault()?.Fields["page"]}");
        Z.Write(c, "update", [new Z.Doc("d1.g1.0").Str("scope_id", scopeB)]);
        fetched = Z.Fetch(c, ["d1.g1.0"], "text", "scope_id");
        Answer("S2b", "metadata-only update keeps text and vector", fetched[0].Fields["text"]?.StartsWith("machine", StringComparison.Ordinal) == true && fetched[0].Fields["scope_id"] == scopeB
            && Z.Vector(c, Z.Vec(5), null, 1).FirstOrDefault()?.Pk == "d1.g1.0", $"scope now B, text kept, vector still found");
        Z.Write(c, "update", [new Z.Doc("d1.g1.0").Str("scope_id", scopeA)]);

        // S3: prefix queries.
        try
        {
            var prefix = Z.Fts(c, "text", null, "learn*", null, 10);
            Answer("S3", "FTS prefix (learn*)", prefix.Count == 2, $"{prefix.Count} hits");
        }
        catch (Exception ex)
        {
            Answer("S3", "FTS prefix (learn*)", false, ex.Message);
        }

        // Stemming and query syntax.
        var stem = Z.Fts(c, "text_english", "invoice", null, null, 10);
        Answer("F1", "English stemming (invoice finds invoices)", stem.Count == 1, $"{stem.Count} hits");
        var syntax = Z.Fts(c, "text", null, "+learning -deep", null, 10);
        Answer("F2", "Query syntax +required -excluded", syntax.Count == 1 && syntax[0].Pk == "d1.g1.0", string.Join(",", syntax.Select(r => r.Pk)));
        var phrase = Z.Fts(c, "text", null, "\"machine learning\"", null, 10);
        Answer("F3", "Phrase query", phrase.Count == 1, string.Join(",", phrase.Select(r => r.Pk)));

        // Filters: IN, CONTAIN_ANY, range, bool, combined with FTS.
        var trimmed = Z.Fts(c, "text", "learning", null, $"scope_id IN ('{scopeA}') AND published = true", 10);
        Answer("F4", "IN filter with FTS (scope trimming)", trimmed.Count == 1 && trimmed[0].Pk == "d1.g1.0", string.Join(",", trimmed.Select(r => r.Pk)));
        var terms = Z.Fts(c, "all", "pdn", null, $"term_ids CONTAIN_ANY ('{termB}', 'x')", 10);
        Answer("F5", "CONTAIN_ANY on string array", terms.Count == 1 && terms[0].Pk == "d2", string.Join(",", terms.Select(r => r.Pk)));
        var range = Z.Fts(c, "all", "pdn", null, "updated_at >= 1500 AND updated_at < 3000", 10);
        Answer("F6", "Range filter on INT64", range.Count == 1, string.Join(",", range.Select(r => r.Pk)));
        try
        {
            var eq = Z.Fts(c, "all", "pdn", null, "kind == 'doc'", 10);
            Answer("F7", "== operator", eq.Count == 2, string.Join(",", eq.Select(r => r.Pk)));
        }
        catch (Exception ex)
        {
            Answer("F7", "== operator", false, ex.Message);
        }

        // Quoting: a value containing a quote must be escapable (on a scalar column).
        Z.Write(c, "update", [new Z.Doc("d2").Str("workspace_id", "O'Brien \\ x")]);
        foreach (var escaped in new[] { @"workspace_id = 'O\'Brien \\ x'", "workspace_id = \"O'Brien \\\\ x\"" })
        {
            try
            {
                var n = Z.Fts(c, "all", "pdn", null, escaped, 10).Count;
                Answer("F8", "Quote and backslash escaping", n == 1, $"{escaped} -> {n}");
            }
            catch (Exception ex)
            {
                Answer("F8", "Quote escaping form accepted", false, $"{escaped}: {ex.Message}");
            }
        }

        // S7: filter-only via the constant token.
        var all = Z.Fts(c, "all", "pdn", null, "workspace_id = 'w1'", 1000, "updated_at");
        Answer("S7", "Filter-only search through constant token", all.Count == 2, $"{all.Count} rows: {string.Join(",", all.Select(r => r.Pk))}");

        // Hybrid with filter; S6: does the result say which route matched?
        var hybrid = Z.Hybrid(c, "learning", ["title", "text"], Z.Vec(3), "published = true", 10, "doc_id", "kind");
        Answer("S6", "Result says which route matched", false, $"hybrid rows (pk:score) {string.Join(",", hybrid.Select(r => $"{r.Pk}:{r.Score:F4}"))}; only one score per row");

        // Dynamic schema: add a field column to a collection with data, index it, filter on it.
        var col = zvec_field_schema_create("f_total_n", TDouble, nullable: true, 0);
        try
        {
            Check(zvec_collection_add_column(c, col, null), "add_column");
            Z.Write(c, "update", [new Z.Doc("d1").Dbl("f_total_n", 120.5)]);
            var p = zvec_index_params_create(IndexInvert);
            zvec_index_params_set_invert_params(p, true, false);
            Check(zvec_collection_create_index(c, "f_total_n", p), "create_index");
            var big = Z.Fts(c, "all", "pdn", null, "f_total_n > 100", 10);
            Answer("D1", "add_column + index on a filled collection, then filter", big.Count == 1 && big[0].Pk == "d1", string.Join(",", big.Select(r => r.Pk)));
        }
        catch (Exception ex)
        {
            Answer("D1", "add_column + index on a filled collection, then filter", false, ex.Message);
        }

        foreach (var (name, type) in new[] { ("f_vendor_s", TString), ("f_paid_b", TBool), ("f_tags_ss", ArrayString), ("f_due_l", TInt64) })
        {
            try
            {
                Check(zvec_collection_add_column(c, zvec_field_schema_create(name, type, nullable: true, 0), null), "add_column " + name);
                Answer("D3", $"add_column of type {type} later", true, name);
            }
            catch (Exception ex)
            {
                Answer("D3", $"add_column of type {type} later", false, ex.Message);
            }
        }

        var gram = Z.Fts(c, "text_ngram", null, "+lea +ear +arn", null, 10);
        Answer("S3b", "Prefix via trigram column (learn* as +lea +ear +arn)", gram.Count == 2, $"{gram.Count} hits");

        try
        {
            var vecCol = zvec_field_schema_create("embedding_v2", VectorFp32, nullable: true, 16);
            var h = zvec_index_params_create(IndexHnsw);
            zvec_index_params_set_metric_type(h, MetricCosine);
            zvec_field_schema_set_index_params(vecCol, h);
            Check(zvec_collection_add_column(c, vecCol, null), "add vector column");
            Answer("D2", "Add a vector column later (model change)", true, "embedding_v2 added");
        }
        catch (Exception ex)
        {
            Answer("D2", "Add a vector column later (model change)", false, ex.Message);
        }

        // Delete by filter (generations, containers).
        Check(zvec_collection_delete_by_filter(c, "doc_id = 'd1' AND kind = 'chunk'"), "delete_by_filter");
        var left = Z.Fts(c, "all", "pdn", null, null, 10);
        Answer("W1", "delete_by_filter", left.Count == 2, string.Join(",", left.Select(r => r.Pk)));

        // Reopen: data persisted.
        Check(zvec_collection_flush(c), "flush");
        Check(zvec_collection_close(c), "close");
        c = Z.Open(path);
        var after = Z.Fts(c, "all", "pdn", null, null, 10);
        Answer("W2", "Reopen after flush keeps rows", after.Count == 2, string.Join(",", after.Select(r => r.Pk)));
        zvec_collection_close(c);

        // Second writer on the same directory.
        try
        {
            var c1 = Z.Open(path);
            try
            {
                var c2 = Z.Open(path);
                Answer("W3", "Second open of the same collection refused", false, "opened twice in one process");
                zvec_collection_close(c2);
            }
            catch (Exception ex)
            {
                Answer("W3", "Second open of the same collection refused", true, ex.Message);
            }

            zvec_collection_close(c1);
        }
        catch (Exception ex)
        {
            Answer("W3", "Second open", false, ex.Message);
        }
    }
}

internal static class Scale
{
    public static void Run(string dir, int rows)
    {
        var path = Path.Combine(dir, $"scale_{rows}");
        var c = Z.CreateCollection(path);
        var random = new Random(42);
        var scopes = Enumerable.Range(0, 20_000).Select(_ => Guid.NewGuid().ToString("N")).ToArray();
        string[] words = ["invoice", "contract", "receipt", "letter", "report", "tax", "insurance", "bank", "salary", "travel", "hotel", "car", "repair", "medical", "school"];
        var sw = Stopwatch.StartNew();
        const int batch = 1000;
        for (var i = 0; i < rows; i += batch)
        {
            var docs = new List<Z.Doc>(batch);
            for (var j = i; j < Math.Min(rows, i + batch); j++)
            {
                var text = string.Join(' ', Enumerable.Range(0, 60).Select(_ => words[random.Next(words.Length)]));
                var v = new float[Z.Dim];
                for (var k = 0; k < v.Length; k++)
                {
                    v[k] = (float)random.NextDouble();
                }

                docs.Add(new Z.Doc($"r{j}").Str("doc_id", $"d{j / 10}").Str("kind", "chunk").Str("scope_id", scopes[random.Next(scopes.Length)])
                    .Str("workspace_id", $"w{j % 20}").Long("updated_at", j).Bool("published", true).Str("text", text).Str("all", "pdn").Vec("embedding", v));
            }

            Z.Write(c, "insert", docs);
        }

        Check(zvec_collection_flush(c), "flush");
        Console.WriteLine($"insert | {rows} rows | {sw.Elapsed.TotalSeconds:F1}s | {rows / sw.Elapsed.TotalSeconds:F0} rows/s | dir {DirSize(path) / (1 << 20)} MB");
        sw.Restart();
        Check(zvec_collection_optimize(c), "optimize");
        Console.WriteLine($"optimize | {sw.Elapsed.TotalSeconds:F1}s | dir {DirSize(path) / (1 << 20)} MB");

        void Time(string name, Func<int> run)
        {
            run();
            var times = new List<double>();
            var n = 0;
            for (var i = 0; i < 5; i++)
            {
                var t = Stopwatch.StartNew();
                n = run();
                times.Add(t.Elapsed.TotalMilliseconds);
            }

            times.Sort();
            Console.WriteLine($"{name} | median {times[2]:F1} ms | max {times[^1]:F1} ms | {n} rows");
        }

        foreach (var count in new[] { 0, 1000, 5000, 20000 })
        {
            var filter = count == 0 ? "published = true" : $"scope_id IN ({string.Join(',', scopes.Take(count).Select(s => $"'{s}'"))}) AND published = true";
            Time($"S4 keyword 'invoice tax' + IN {count} scopes, top 200", () => Z.Fts(c, "text", "invoice tax", null, filter, 200).Count);
            Time($"S7 filter-only (constant token) + IN {count} scopes, top 1000", () => Z.Fts(c, "all", "pdn", null, filter, 1000).Count);
            var v = Z.Vec(2, 0.3f);
            Time($"S4 vector + IN {count} scopes, top 200", () => Z.Vector(c, v, filter, 200).Count);
            Time($"S4 hybrid (text + vector, RRF) + IN {count} scopes, top 200", () => Z.Hybrid(c, "invoice tax", ["text"], v, filter, 200).Count);
        }

        Time("workspace filter only, keyword top 200", () => Z.Fts(c, "text", "invoice", null, "workspace_id = 'w3'", 200).Count);
        zvec_collection_close(c);
        Console.WriteLine($"process working set {Environment.WorkingSet / (1 << 20)} MB");
    }

    private static long DirSize(string path) => new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
}

internal static class Crash
{
    public static void Run(string dir)
    {
        var path = Path.Combine(dir, "crash");
        var c = Z.CreateCollection(path);
        zvec_collection_close(c);
        var self = Environment.ProcessPath!;
        var dll = typeof(Crash).Assembly.Location;
        var start = self.EndsWith("dotnet", StringComparison.Ordinal)
            ? new ProcessStartInfo(self, [dll, "writer", dir])
            : new ProcessStartInfo(self, ["writer", dir]);
        start.RedirectStandardOutput = true;
        start.Environment["LD_LIBRARY_PATH"] = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        for (var round = 1; round <= 3; round++)
        {
            using var writer = Process.Start(start)!;
            Thread.Sleep(2500 + (round * 700));
            writer.Kill();
            writer.WaitForExit();
            var last = 0;
            string? line;
            while ((line = writer.StandardOutput.ReadLine()) is not null)
            {
                last = int.Parse(line);
            }

            try
            {
                c = Z.Open(path);
                var rows = Z.Fts(c, "all", "pdn", null, null, 100_000).Count;
                Console.WriteLine($"S8 | round {round}: writer killed after acknowledging {last} rows | reopened, {rows} rows readable | {(rows >= last ? "no acknowledged write lost" : "LOST ACKNOWLEDGED WRITES")}");
                zvec_collection_close(c);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"S8 | round {round}: reopen failed after kill -9 | {ex.Message}");
                return;
            }
        }
    }

    public static void Writer(string dir)
    {
        var c = Z.Open(Path.Combine(dir, "crash"));
        var total = 0;
        var existing = Z.Fts(c, "all", "pdn", null, null, 100_000).Count;
        total = existing;
        while (true)
        {
            var docs = Enumerable.Range(total, 100).Select(i => new Z.Doc($"x{i}").Str("doc_id", $"x{i}").Str("text", "crash test row").Str("all", "pdn")).ToList();
            Z.Write(c, "insert", docs);
            total += 100;
            Console.WriteLine(total);
            Console.Out.Flush();
        }
    }
}

internal static class Escape
{
    public static void Run(string dir)
    {
        var c = Z.CreateCollection(Path.Combine(dir, "escape"));
        string[] values = ["plain", "O'Brien", "back\\slash", "quote\"double", "unié", "a b"];
        Z.Write(c, "insert", values.Select((v, i) => new Z.Doc($"e{i}").Str("workspace_id", v).Str("all", "pdn")).ToList());
        foreach (var v in values)
        {
            var single = "'" + v.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
            var dbl = "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            foreach (var literal in new[] { single, dbl })
            {
                string result;
                try
                {
                    var rows = Z.Fts(c, "all", "pdn", null, $"workspace_id = {literal}", 10);
                    result = string.Join(",", rows.Select(r => r.Pk));
                }
                catch (Exception ex)
                {
                    result = "ERROR " + ex.Message[..Math.Min(80, ex.Message.Length)];
                }

                Console.WriteLine($"ESC | value [{v}] literal {literal} -> {result}");
            }
        }

        zvec_collection_close(c);
    }
}

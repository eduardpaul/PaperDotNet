using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace PaperDotNet.Search.Zvec.Native;

/// <summary>A column of a new collection.</summary>
internal abstract record ZvecColumn(string Name);

/// <summary>A scalar column; <paramref name="Index"/> gives it an inverted index (with range support when <paramref name="Range"/>).</summary>
internal sealed record ZvecScalar(string Name, uint Type, bool Index, bool Range = false) : ZvecColumn(Name);

/// <summary>A full-text column (<c>standard</c>, <c>ngram</c> or <c>whitespace</c> tokenizer, filters, extra parameters as JSON).</summary>
internal sealed record ZvecText(string Name, string Tokenizer, string[] Filters, string? Extra = null) : ZvecColumn(Name);

/// <summary>A float vector column with an HNSW cosine index.</summary>
internal sealed record ZvecVector(string Name, int Dimensions) : ZvecColumn(Name);

/// <summary>A row returned by a query or fetch: its key, score and the output fields read with their types.</summary>
internal sealed record ZvecHit(string Pk, float Score, IReadOnlyDictionary<string, object?> Fields)
{
    public string? Text(string field) => Fields.GetValueOrDefault(field) as string;

    public double? Number(string field) => Fields.GetValueOrDefault(field) switch
    {
        double d => d,
        long l => l,
        int i => i,
        _ => null,
    };

    public IReadOnlyList<string> Texts(string field) => Fields.GetValueOrDefault(field) as IReadOnlyList<string> ?? [];
}

/// <summary>
/// An open zvec collection (one handle). Reads may run in parallel; writes are serialized by the caller. Disposing
/// closes the handle and keeps the data.
/// </summary>
internal sealed unsafe class ZvecCollection : IDisposable
{
    /// <summary>zvec's largest <c>topk</c>.</summary>
    public const int MaxTopK = 100_000;

    private nint _handle;

    private ZvecCollection(nint handle, string path)
    {
        _handle = handle;
        Path = path;
    }

    public string Path { get; }

    public static ZvecCollection Open(string path)
    {
        ZvecNative.Check(ZvecNative.zvec_collection_open(path, 0, out var handle), "open collection");
        return new ZvecCollection(handle, path);
    }

    public static ZvecCollection Create(string path, IEnumerable<ZvecColumn> columns)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var schema = ZvecNative.zvec_collection_schema_create("search");
        try
        {
            foreach (var column in columns)
            {
                var field = Field(column);
                try
                {
                    ZvecNative.Check(ZvecNative.zvec_collection_schema_add_field(schema, field), $"add field {column.Name}");
                }
                finally
                {
                    ZvecNative.zvec_field_schema_destroy(field);
                }
            }

            ZvecNative.Check(ZvecNative.zvec_collection_create_and_open(path, schema, 0, out var handle), "create collection");
            return new ZvecCollection(handle, path);
        }
        finally
        {
            ZvecNative.zvec_collection_schema_destroy(schema);
        }
    }

    private static nint Field(ZvecColumn column)
    {
        switch (column)
        {
            case ZvecScalar scalar:
                {
                    var field = ZvecNative.zvec_field_schema_create(scalar.Name, scalar.Type, nullable: true, 0);
                    if (scalar.Index)
                    {
                        var index = ZvecNative.zvec_index_params_create(ZvecNative.IndexInvert);
                        ZvecNative.Check(ZvecNative.zvec_index_params_set_invert_params(index, scalar.Range, false), "invert params");
                        ZvecNative.Check(ZvecNative.zvec_field_schema_set_index_params(field, index), "set index");
                        ZvecNative.zvec_index_params_destroy(index);
                    }

                    return field;
                }

            case ZvecText text:
                {
                    var field = ZvecNative.zvec_field_schema_create(text.Name, ZvecNative.TypeString, nullable: true, 0);
                    var index = ZvecNative.zvec_index_params_create(ZvecNative.IndexFts);
                    var filters = ZvecNative.zvec_string_array_create((nuint)text.Filters.Length);
                    for (var i = 0; i < text.Filters.Length; i++)
                    {
                        ZvecNative.zvec_string_array_add(filters, (nuint)i, text.Filters[i]);
                    }

                    ZvecNative.Check(ZvecNative.zvec_index_params_set_fts_params(index, text.Tokenizer, filters, text.Extra), $"text params {text.Name}");
                    ZvecNative.zvec_string_array_destroy(filters);
                    ZvecNative.Check(ZvecNative.zvec_field_schema_set_index_params(field, index), "set text index");
                    ZvecNative.zvec_index_params_destroy(index);
                    return field;
                }

            case ZvecVector vector:
                {
                    var field = ZvecNative.zvec_field_schema_create(vector.Name, ZvecNative.TypeVectorFp32, nullable: true, (uint)vector.Dimensions);
                    var index = ZvecNative.zvec_index_params_create(ZvecNative.IndexHnsw);
                    ZvecNative.Check(ZvecNative.zvec_index_params_set_metric_type(index, ZvecNative.MetricCosine), "metric");
                    ZvecNative.Check(ZvecNative.zvec_index_params_set_hnsw_params(index, 16, 100), "hnsw params");
                    ZvecNative.Check(ZvecNative.zvec_field_schema_set_index_params(field, index), "set vector index");
                    ZvecNative.zvec_index_params_destroy(index);
                    return field;
                }

            default:
                throw new ArgumentException($"Unknown column {column}.", nameof(column));
        }
    }

    /// <summary>Adds a nullable DOUBLE column with a range index (zvec adds only numeric columns to a collection).</summary>
    public void AddNumberColumn(string name)
    {
        var field = ZvecNative.zvec_field_schema_create(name, ZvecNative.TypeDouble, nullable: true, 0);
        try
        {
            ZvecNative.Check(ZvecNative.zvec_collection_add_column(Handle, field, null), $"add column {name}");
        }
        finally
        {
            ZvecNative.zvec_field_schema_destroy(field);
        }

        var index = ZvecNative.zvec_index_params_create(ZvecNative.IndexInvert);
        try
        {
            ZvecNative.Check(ZvecNative.zvec_index_params_set_invert_params(index, true, false), "invert params");
            ZvecNative.Check(ZvecNative.zvec_collection_create_index(Handle, name, index), $"index {name}");
        }
        finally
        {
            ZvecNative.zvec_index_params_destroy(index);
        }
    }

    private nint Handle => _handle != 0 ? _handle : throw new ObjectDisposedException(Path);

    /// <summary>Inserts or replaces whole rows.</summary>
    public void Upsert(IReadOnlyList<ZvecRow> rows) => Write(rows, "upsert");

    /// <summary>Sets the given columns of existing rows; other columns keep their values.</summary>
    public void Update(IReadOnlyList<ZvecRow> rows) => Write(rows, "update");

    private void Write(IReadOnlyList<ZvecRow> rows, string operation)
    {
        try
        {
            foreach (var chunk in rows.Chunk(1000))
            {
                var handles = chunk.Select(r => r.Handle).ToArray();
                fixed (nint* docs = handles)
                {
                    var code = operation == "upsert"
                        ? ZvecNative.zvec_collection_upsert(Handle, docs, (nuint)handles.Length, out _, out var failed)
                        : ZvecNative.zvec_collection_update(Handle, docs, (nuint)handles.Length, out _, out failed);
                    ZvecNative.Check(code, operation);
                    if (failed > 0 && operation == "upsert")
                    {
                        throw new ZvecException($"zvec {operation}: {failed} of {handles.Length} rows failed: {ZvecNative.LastError()}", 8);
                    }
                }
            }
        }
        finally
        {
            foreach (var row in rows)
            {
                row.Dispose();
            }
        }
    }

    public void Delete(IReadOnlyCollection<string> pks)
    {
        foreach (var chunk in pks.Chunk(1000))
        {
            var pointers = chunk.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
            try
            {
                fixed (nint* p = pointers)
                {
                    ZvecNative.Check(ZvecNative.zvec_collection_delete(Handle, p, (nuint)pointers.Length, out _, out _), "delete");
                }
            }
            finally
            {
                foreach (var pointer in pointers)
                {
                    Marshal.FreeCoTaskMem(pointer);
                }
            }
        }
    }

    public void DeleteWhere(string filter) => ZvecNative.Check(ZvecNative.zvec_collection_delete_by_filter(Handle, filter), "delete by filter");

    public void Flush() => ZvecNative.Check(ZvecNative.zvec_collection_flush(Handle), "flush");

    public void Optimize() => ZvecNative.Check(ZvecNative.zvec_collection_optimize(Handle), "optimize");

    /// <summary>Full-text search of one column with a zvec query string, best first.</summary>
    public List<ZvecHit> Text(string column, string query, string? filter, int topK, IReadOnlyList<(string Name, uint Type)> outputs) =>
        Run(column, filter, topK, outputs, q =>
        {
            var fts = ZvecNative.zvec_fts_create();
            try
            {
                ZvecNative.Check(ZvecNative.zvec_fts_set_query_string(fts, query), "text query");
                ZvecNative.Check(ZvecNative.zvec_vector_query_set_fts(q, fts), "set text query");
            }
            finally
            {
                ZvecNative.zvec_fts_destroy(fts);
            }
        });

    /// <summary>The rows most similar to <paramref name="vector"/> (cosine), best first.</summary>
    public List<ZvecHit> Nearest(string column, ReadOnlySpan<float> vector, string? filter, int topK, IReadOnlyList<(string Name, uint Type)> outputs)
    {
        var copy = vector.ToArray();
        return Run(column, filter, topK, outputs, q =>
        {
            fixed (float* p = copy)
            {
                ZvecNative.Check(ZvecNative.zvec_vector_query_set_query_vector(q, p, (nuint)(copy.Length * sizeof(float))), "set vector");
            }
        });
    }

    private List<ZvecHit> Run(string column, string? filter, int topK, IReadOnlyList<(string Name, uint Type)> outputs, Action<nint> configure)
    {
        var query = ZvecNative.zvec_vector_query_create();
        try
        {
            ZvecNative.Check(ZvecNative.zvec_vector_query_set_field_name(query, column), "query column");
            ZvecNative.Check(ZvecNative.zvec_vector_query_set_topk(query, Math.Clamp(topK, 1, MaxTopK)), "topk");
            if (filter is not null)
            {
                ZvecNative.Check(ZvecNative.zvec_vector_query_set_filter(query, filter), "filter");
            }

            configure(query);
            WithNames(outputs, (p, n) => ZvecNative.zvec_vector_query_set_output_fields(query, p, n), "output fields");
            ZvecNative.Check(ZvecNative.zvec_collection_query(Handle, query, out var docs, out var count), "query");
            return Read(docs, count, outputs);
        }
        finally
        {
            ZvecNative.zvec_vector_query_destroy(query);
        }
    }

    /// <summary>Rows by key (missing keys are skipped).</summary>
    public List<ZvecHit> Fetch(IReadOnlyCollection<string> pks, IReadOnlyList<(string Name, uint Type)> outputs)
    {
        var hits = new List<ZvecHit>();
        foreach (var chunk in pks.Chunk(1000))
        {
            var pointers = chunk.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
            var names = outputs.Select(o => Marshal.StringToCoTaskMemUTF8(o.Name)).ToArray();
            try
            {
                fixed (nint* p = pointers)
                fixed (nint* o = names)
                {
                    ZvecNative.Check(
                        ZvecNative.zvec_collection_fetch(Handle, p, (nuint)pointers.Length, names.Length == 0 ? null : o, (nuint)names.Length, false, out var docs, out var found),
                        "fetch");
                    hits.AddRange(Read(docs, found, outputs));
                }
            }
            finally
            {
                foreach (var pointer in pointers.Concat(names))
                {
                    Marshal.FreeCoTaskMem(pointer);
                }
            }
        }

        return hits;
    }

    private delegate int NamesSetter(nint* names, nuint count);

    private static void WithNames(IReadOnlyList<(string Name, uint Type)> outputs, NamesSetter set, string operation)
    {
        if (outputs.Count == 0)
        {
            return;
        }

        var pointers = outputs.Select(o => Marshal.StringToCoTaskMemUTF8(o.Name)).ToArray();
        try
        {
            fixed (nint* p = pointers)
            {
                ZvecNative.Check(set(p, (nuint)pointers.Length), operation);
            }
        }
        finally
        {
            foreach (var pointer in pointers)
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }
    }

    private static List<ZvecHit> Read(nint* docs, nuint count, IReadOnlyList<(string Name, uint Type)> outputs)
    {
        var hits = new List<ZvecHit>((int)count);
        try
        {
            for (nuint i = 0; i < count; i++)
            {
                var doc = docs[i];
                var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var (name, type) in outputs)
                {
                    fields[name] = ReadField(doc, name, type);
                }

                hits.Add(new ZvecHit(Marshal.PtrToStringUTF8(ZvecNative.zvec_doc_get_pk_pointer(doc)) ?? string.Empty, ZvecNative.zvec_doc_get_score(doc), fields));
            }
        }
        finally
        {
            if (count > 0)
            {
                ZvecNative.zvec_docs_free(docs, count);
            }
        }

        return hits;
    }

    private static object? ReadField(nint doc, string name, uint type)
    {
        if (!ZvecNative.zvec_doc_has_field(doc, name) || ZvecNative.zvec_doc_is_field_null(doc, name))
        {
            return null;
        }

        switch (type)
        {
            case ZvecNative.TypeString:
                return ZvecNative.zvec_doc_get_field_value_pointer(doc, name, type, out var text, out var size) == 0
                    ? Marshal.PtrToStringUTF8(text, (int)size)
                    : null;
            case ZvecNative.TypeInt64:
                long l;
                return ZvecNative.zvec_doc_get_field_value_basic(doc, name, type, &l, sizeof(long)) == 0 ? l : null;
            case ZvecNative.TypeInt32:
                int i;
                return ZvecNative.zvec_doc_get_field_value_basic(doc, name, type, &i, sizeof(int)) == 0 ? i : null;
            case ZvecNative.TypeDouble:
                double d;
                return ZvecNative.zvec_doc_get_field_value_basic(doc, name, type, &d, sizeof(double)) == 0 ? d : null;
            case ZvecNative.TypeArrayString:
                if (ZvecNative.zvec_doc_get_field_value_copy(doc, name, type, out var buffer, out var length) != 0 || buffer == 0)
                {
                    return null;
                }

                try
                {
                    var values = new List<string>();
                    var bytes = new ReadOnlySpan<byte>((void*)buffer, (int)length);
                    while (bytes.Length > 0)
                    {
                        var end = bytes.IndexOf((byte)0);
                        end = end < 0 ? bytes.Length : end;
                        values.Add(Encoding.UTF8.GetString(bytes[..end]));
                        bytes = end < bytes.Length ? bytes[(end + 1)..] : [];
                    }

                    return values;
                }
                finally
                {
                    ZvecNative.zvec_free(buffer);
                }

            default:
                throw new ArgumentException($"Reading type {type.ToString(CultureInfo.InvariantCulture)} is not supported.", nameof(type));
        }
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
        {
            _ = ZvecNative.zvec_collection_flush(handle);
            _ = ZvecNative.zvec_collection_close(handle);
        }
    }
}

/// <summary>A row to write: owns its native document and the strings of its array values until written.</summary>
internal sealed unsafe class ZvecRow : IDisposable
{
    private readonly List<nint> _strings = [];
    private nint _handle;

    public ZvecRow(string pk)
    {
        _handle = ZvecNative.zvec_doc_create();
        ZvecNative.zvec_doc_set_pk(_handle, pk);
    }

    public nint Handle => _handle != 0 ? _handle : throw new ObjectDisposedException(nameof(ZvecRow));

    public ZvecRow Text(string name, string value)
    {
        // An empty string still needs a valid pointer (fixed over an empty array gives null, which zvec rejects).
        var bytes = Encoding.UTF8.GetBytes(value.Length == 0 ? "\0" : value);
        fixed (byte* p = bytes)
        {
            ZvecNative.Check(ZvecNative.zvec_doc_add_field_by_value(Handle, name, ZvecNative.TypeString, p, (nuint)(value.Length == 0 ? 0 : bytes.Length)), $"set {name}");
        }

        return this;
    }

    public ZvecRow Long(string name, long value)
    {
        ZvecNative.Check(ZvecNative.zvec_doc_add_field_by_value(Handle, name, ZvecNative.TypeInt64, &value, sizeof(long)), $"set {name}");
        return this;
    }

    public ZvecRow Int(string name, int value)
    {
        ZvecNative.Check(ZvecNative.zvec_doc_add_field_by_value(Handle, name, ZvecNative.TypeInt32, &value, sizeof(int)), $"set {name}");
        return this;
    }

    public ZvecRow Double(string name, double value)
    {
        ZvecNative.Check(ZvecNative.zvec_doc_add_field_by_value(Handle, name, ZvecNative.TypeDouble, &value, sizeof(double)), $"set {name}");
        return this;
    }

    /// <summary>
    /// A string array, always as <c>zvec_string_t*</c> pointers: zvec guesses the packed C-string form from the byte
    /// size, which misreads packed values whose total length is a multiple of 8 (spike finding).
    /// </summary>
    public ZvecRow Texts(string name, IReadOnlyCollection<string> values)
    {
        var pointers = values.Select(v =>
        {
            var s = ZvecNative.zvec_string_create(v);
            _strings.Add(s);
            return s;
        }).ToArray();
        if (pointers.Length == 0)
        {
            ZvecNative.Check(ZvecNative.zvec_doc_set_field_null(Handle, name), $"clear {name}");
            return this;
        }

        fixed (nint* p = pointers)
        {
            ZvecNative.Check(ZvecNative.zvec_doc_add_field_by_value(Handle, name, ZvecNative.TypeArrayString, p, (nuint)(pointers.Length * sizeof(nint))), $"set {name}");
        }

        return this;
    }

    public ZvecRow Vector(string name, ReadOnlySpan<float> value)
    {
        fixed (float* p = value)
        {
            ZvecNative.Check(ZvecNative.zvec_doc_add_field_by_value(Handle, name, ZvecNative.TypeVectorFp32, p, (nuint)(value.Length * sizeof(float))), $"set {name}");
        }

        return this;
    }

    public ZvecRow Null(string name)
    {
        ZvecNative.Check(ZvecNative.zvec_doc_set_field_null(Handle, name), $"clear {name}");
        return this;
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
        {
            ZvecNative.zvec_doc_destroy(handle);
        }

        foreach (var s in _strings)
        {
            ZvecNative.zvec_free_string(s);
        }

        _strings.Clear();
    }
}

using System.Runtime.InteropServices;

namespace ZvecSpike;

/// <summary>The subset of the zvec C API (c_api.h) the spike uses. All handles are raw pointers here.</summary>
internal static unsafe partial class Native
{
    private const string Lib = "zvec_c_api";

    public const uint TString = 2, TBool = 3, TInt32 = 4, TInt64 = 5, TDouble = 9, VectorFp32 = 23, ArrayString = 41;
    public const uint IndexHnsw = 1, IndexFlat = 3, IndexInvert = 10, IndexFts = 11;
    public const uint MetricCosine = 3, MetricIp = 2;

    [LibraryImport(Lib)] public static partial int zvec_initialize(nint config);
    [LibraryImport(Lib)] public static partial nint zvec_config_data_create();
    [LibraryImport(Lib)] public static partial int zvec_config_data_set_memory_limit(nint config, ulong bytes);
    [LibraryImport(Lib)] public static partial int zvec_get_last_error(out nint message);
    [LibraryImport(Lib)] public static partial void zvec_free(nint ptr);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint zvec_collection_schema_create(string name);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint zvec_field_schema_create(string name, uint type, [MarshalAs(UnmanagedType.U1)] bool nullable, uint dimension);
    [LibraryImport(Lib)] public static partial nint zvec_index_params_create(uint type);
    [LibraryImport(Lib)] public static partial int zvec_index_params_set_invert_params(nint p, [MarshalAs(UnmanagedType.U1)] bool range, [MarshalAs(UnmanagedType.U1)] bool wildcard);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int zvec_index_params_set_fts_params(nint p, string tokenizer, nint filters, string? extra);
    [LibraryImport(Lib)] public static partial int zvec_index_params_set_metric_type(nint p, uint metric);
    [LibraryImport(Lib)] public static partial int zvec_index_params_set_hnsw_params(nint p, int m, int ef);
    [LibraryImport(Lib)] public static partial int zvec_field_schema_set_index_params(nint field, nint index);
    [LibraryImport(Lib)] public static partial int zvec_collection_schema_add_field(nint schema, nint field);
    [LibraryImport(Lib)] public static partial nint zvec_string_array_create(nuint count);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial void zvec_string_array_add(nint array, nuint index, string value);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint zvec_string_create(string value);
    [LibraryImport(Lib)] public static partial void zvec_free_string(nint value);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int zvec_collection_create_and_open(string path, nint schema, nint options, out nint collection);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int zvec_collection_open(string path, nint options, out nint collection);
    [LibraryImport(Lib)] public static partial int zvec_collection_close(nint collection);
    [LibraryImport(Lib)] public static partial int zvec_collection_destroy(nint collection);
    [LibraryImport(Lib)] public static partial int zvec_collection_flush(nint collection);
    [LibraryImport(Lib)] public static partial int zvec_collection_optimize(nint collection);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int zvec_collection_add_column(nint collection, nint field, string? expression);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int zvec_collection_create_index(nint collection, string field, nint index);

    [LibraryImport(Lib)] public static partial nint zvec_doc_create();
    [LibraryImport(Lib)] public static partial void zvec_doc_destroy(nint doc);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial void zvec_doc_set_pk(nint doc, string pk);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int zvec_doc_add_field_by_value(nint doc, string name, uint type, void* value, nuint size);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int zvec_doc_set_field_null(nint doc, string name);
    [LibraryImport(Lib)] public static partial int zvec_collection_insert(nint collection, nint* docs, nuint count, out nuint ok, out nuint failed);
    [LibraryImport(Lib)] public static partial int zvec_collection_upsert(nint collection, nint* docs, nuint count, out nuint ok, out nuint failed);
    [LibraryImport(Lib)] public static partial int zvec_collection_update(nint collection, nint* docs, nuint count, out nuint ok, out nuint failed);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int zvec_collection_delete_by_filter(nint collection, string filter);
    [LibraryImport(Lib)] public static partial int zvec_collection_fetch(nint collection, nint* pks, nuint count, nint* fields, nuint fieldCount, [MarshalAs(UnmanagedType.U1)] bool includeVector, out nint* docs, out nuint found);

    [LibraryImport(Lib)] public static partial nint zvec_vector_query_create();
    [LibraryImport(Lib)] public static partial void zvec_vector_query_destroy(nint q);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int zvec_vector_query_set_field_name(nint q, string field);
    [LibraryImport(Lib)] public static partial int zvec_vector_query_set_query_vector(nint q, void* data, nuint size);
    [LibraryImport(Lib)] public static partial int zvec_vector_query_set_topk(nint q, int topk);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int zvec_vector_query_set_filter(nint q, string filter);
    [LibraryImport(Lib)] public static partial int zvec_vector_query_set_output_fields(nint q, nint* fields, nuint count);
    [LibraryImport(Lib)] public static partial int zvec_vector_query_set_fts(nint q, nint fts);
    [LibraryImport(Lib)] public static partial nint zvec_fts_create();
    [LibraryImport(Lib)] public static partial void zvec_fts_destroy(nint fts);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int zvec_fts_set_match_string(nint fts, string text);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int zvec_fts_set_query_string(nint fts, string text);
    [LibraryImport(Lib)] public static partial int zvec_collection_query(nint collection, nint q, out nint* docs, out nuint count);

    [LibraryImport(Lib)] public static partial nint zvec_multi_query_create();
    [LibraryImport(Lib)] public static partial void zvec_multi_query_destroy(nint q);
    [LibraryImport(Lib)] public static partial int zvec_multi_query_add_sub_query(nint q, nint sub);
    [LibraryImport(Lib)] public static partial int zvec_multi_query_set_topk(nint q, int topk);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int zvec_multi_query_set_filter(nint q, string filter);
    [LibraryImport(Lib)] public static partial int zvec_multi_query_set_rerank_rrf(nint q, int k);
    [LibraryImport(Lib)] public static partial int zvec_multi_query_set_output_fields(nint q, nint* fields, nuint count);
    [LibraryImport(Lib)] public static partial int zvec_collection_multi_query(nint collection, nint q, out nint* docs, out nuint count);
    [LibraryImport(Lib)] public static partial nint zvec_sub_query_create();
    [LibraryImport(Lib)] public static partial void zvec_sub_query_destroy(nint q);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int zvec_sub_query_set_field_name(nint q, string field);
    [LibraryImport(Lib)] public static partial int zvec_sub_query_set_query_vector(nint q, void* data, nuint size);
    [LibraryImport(Lib)] public static partial int zvec_sub_query_set_num_candidates(nint q, int n);
    [LibraryImport(Lib)] public static partial int zvec_sub_query_set_fts(nint q, nint fts);

    [LibraryImport(Lib)] public static partial void zvec_docs_free(nint* docs, nuint count);
    [LibraryImport(Lib)] public static partial nint zvec_doc_get_pk_pointer(nint doc);
    [LibraryImport(Lib)] public static partial float zvec_doc_get_score(nint doc);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int zvec_doc_get_field_value_basic(nint doc, string field, uint type, void* buffer, nuint size);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int zvec_doc_get_field_value_pointer(nint doc, string field, uint type, out nint value, out nuint size);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool zvec_doc_has_field(nint doc, string field);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool zvec_doc_is_field_null(nint doc, string field);

    public static string LastError()
    {
        zvec_get_last_error(out var message);
        if (message == 0)
        {
            return "(no message)";
        }

        var text = Marshal.PtrToStringUTF8(message) ?? string.Empty;
        zvec_free(message);
        return text;
    }

    public static void Check(int code, string what)
    {
        if (code != 0)
        {
            throw new InvalidOperationException($"{what} failed ({code}): {LastError()}");
        }
    }
}

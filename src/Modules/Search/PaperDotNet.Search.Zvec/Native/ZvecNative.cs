using System.Runtime.InteropServices;

namespace PaperDotNet.Search.Zvec.Native;

/// <summary>
/// The part of the zvec C API (<c>c_api.h</c>) the store uses. Handles are raw pointers; <see cref="ZvecCollection"/>
/// and <see cref="ZvecRow"/> own and free them. Never call <c>zvec_collection_destroy</c> to release a handle: it
/// deletes the collection (<see cref="zvec_collection_close"/> releases it).
/// </summary>
internal static unsafe partial class ZvecNative
{
    public const string Library = "zvec_c_api";

    public const uint TypeString = 2;
    public const uint TypeBool = 3;
    public const uint TypeInt32 = 4;
    public const uint TypeInt64 = 5;
    public const uint TypeDouble = 9;
    public const uint TypeVectorFp32 = 23;
    public const uint TypeArrayString = 41;

    public const uint IndexHnsw = 1;
    public const uint IndexInvert = 10;
    public const uint IndexFts = 11;

    public const uint MetricCosine = 3;

    [LibraryImport(Library)]
    public static partial int zvec_initialize(nint config);

    [LibraryImport(Library)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool zvec_is_initialized();

    [LibraryImport(Library)]
    public static partial nint zvec_config_data_create();

    [LibraryImport(Library)]
    public static partial int zvec_config_data_set_memory_limit(nint config, ulong bytes);

    [LibraryImport(Library)]
    public static partial int zvec_config_data_set_query_thread_count(nint config, uint count);

    [LibraryImport(Library)]
    public static partial int zvec_get_last_error(out nint message);

    [LibraryImport(Library)]
    public static partial void zvec_free(nint pointer);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint zvec_collection_schema_create(string name);

    [LibraryImport(Library)]
    public static partial void zvec_collection_schema_destroy(nint schema);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint zvec_field_schema_create(string name, uint type, [MarshalAs(UnmanagedType.U1)] bool nullable, uint dimension);

    [LibraryImport(Library)]
    public static partial void zvec_field_schema_destroy(nint field);

    [LibraryImport(Library)]
    public static partial nint zvec_index_params_create(uint type);

    [LibraryImport(Library)]
    public static partial void zvec_index_params_destroy(nint parameters);

    [LibraryImport(Library)]
    public static partial int zvec_index_params_set_invert_params(nint parameters, [MarshalAs(UnmanagedType.U1)] bool rangeOptimization, [MarshalAs(UnmanagedType.U1)] bool wildcard);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int zvec_index_params_set_fts_params(nint parameters, string tokenizer, nint filters, string? extraParams);

    [LibraryImport(Library)]
    public static partial int zvec_index_params_set_metric_type(nint parameters, uint metric);

    [LibraryImport(Library)]
    public static partial int zvec_index_params_set_hnsw_params(nint parameters, int m, int efConstruction);

    [LibraryImport(Library)]
    public static partial int zvec_field_schema_set_index_params(nint field, nint parameters);

    [LibraryImport(Library)]
    public static partial int zvec_collection_schema_add_field(nint schema, nint field);

    [LibraryImport(Library)]
    public static partial nint zvec_string_array_create(nuint count);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void zvec_string_array_add(nint array, nuint index, string value);

    [LibraryImport(Library)]
    public static partial void zvec_string_array_destroy(nint array);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint zvec_string_create(string value);

    [LibraryImport(Library)]
    public static partial void zvec_free_string(nint value);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int zvec_collection_create_and_open(string path, nint schema, nint options, out nint collection);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int zvec_collection_open(string path, nint options, out nint collection);

    [LibraryImport(Library)]
    public static partial int zvec_collection_close(nint collection);

    [LibraryImport(Library)]
    public static partial int zvec_collection_flush(nint collection);

    [LibraryImport(Library)]
    public static partial int zvec_collection_optimize(nint collection);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int zvec_collection_add_column(nint collection, nint field, string? expression);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int zvec_collection_create_index(nint collection, string field, nint parameters);

    [LibraryImport(Library)]
    public static partial nint zvec_doc_create();

    [LibraryImport(Library)]
    public static partial void zvec_doc_destroy(nint doc);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void zvec_doc_set_pk(nint doc, string pk);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int zvec_doc_add_field_by_value(nint doc, string name, uint type, void* value, nuint size);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int zvec_doc_set_field_null(nint doc, string name);

    [LibraryImport(Library)]
    public static partial int zvec_collection_upsert(nint collection, nint* docs, nuint count, out nuint succeeded, out nuint failed);

    [LibraryImport(Library)]
    public static partial int zvec_collection_insert(nint collection, nint* docs, nuint count, out nuint succeeded, out nuint failed);

    [LibraryImport(Library)]
    public static partial int zvec_collection_update(nint collection, nint* docs, nuint count, out nuint succeeded, out nuint failed);

    [LibraryImport(Library)]
    public static partial int zvec_collection_delete(nint collection, nint* pks, nuint count, out nuint succeeded, out nuint failed);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int zvec_collection_delete_by_filter(nint collection, string filter);

    [LibraryImport(Library)]
    public static partial int zvec_collection_fetch(
        nint collection, nint* pks, nuint count, nint* fields, nuint fieldCount, [MarshalAs(UnmanagedType.U1)] bool includeVector, out nint* docs, out nuint found);

    [LibraryImport(Library)]
    public static partial nint zvec_vector_query_create();

    [LibraryImport(Library)]
    public static partial void zvec_vector_query_destroy(nint query);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int zvec_vector_query_set_field_name(nint query, string field);

    [LibraryImport(Library)]
    public static partial int zvec_vector_query_set_query_vector(nint query, void* data, nuint size);

    [LibraryImport(Library)]
    public static partial int zvec_vector_query_set_topk(nint query, int topk);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int zvec_vector_query_set_filter(nint query, string filter);

    [LibraryImport(Library)]
    public static partial int zvec_vector_query_set_output_fields(nint query, nint* fields, nuint count);

    [LibraryImport(Library)]
    public static partial int zvec_vector_query_set_fts(nint query, nint fts);

    [LibraryImport(Library)]
    public static partial nint zvec_fts_create();

    [LibraryImport(Library)]
    public static partial void zvec_fts_destroy(nint fts);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int zvec_fts_set_query_string(nint fts, string query);

    [LibraryImport(Library)]
    public static partial int zvec_collection_query(nint collection, nint query, out nint* docs, out nuint count);

    [LibraryImport(Library)]
    public static partial void zvec_docs_free(nint* docs, nuint count);

    [LibraryImport(Library)]
    public static partial nint zvec_doc_get_pk_pointer(nint doc);

    [LibraryImport(Library)]
    public static partial float zvec_doc_get_score(nint doc);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int zvec_doc_get_field_value_basic(nint doc, string field, uint type, void* buffer, nuint size);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int zvec_doc_get_field_value_pointer(nint doc, string field, uint type, out nint value, out nuint size);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int zvec_doc_get_field_value_copy(nint doc, string field, uint type, out nint value, out nuint size);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool zvec_doc_has_field(nint doc, string field);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool zvec_doc_is_field_null(nint doc, string field);

    /// <summary>Throws <see cref="ZvecException"/> with zvec's last error when <paramref name="code"/> is not OK.</summary>
    public static void Check(int code, string operation)
    {
        if (code != 0)
        {
            throw new ZvecException($"zvec {operation} failed ({code}): {LastError()}", code);
        }
    }

    public static string LastError()
    {
        if (zvec_get_last_error(out var message) != 0 || message == 0)
        {
            return "no details";
        }

        try
        {
            return Marshal.PtrToStringUTF8(message) ?? "no details";
        }
        finally
        {
            zvec_free(message);
        }
    }
}

/// <summary>A failed zvec call, with zvec's error code (3: invalid argument, 8: internal error, …).</summary>
public sealed class ZvecException(string message, int code) : Exception(message)
{
    public int Code { get; } = code;
}

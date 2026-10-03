using System.Reflection;
using System.Runtime.InteropServices;

namespace PaperDotNet.Search.Zvec.Native;

/// <summary>
/// Loads and initializes the zvec native library once per process: from <c>Search:Zvec:LibraryPath</c> when set,
/// else from the usual places (next to the app, <c>runtimes/{rid}/native</c>, the system library path).
/// </summary>
internal static class ZvecLibrary
{
    private static readonly Lock Gate = new();
    private static string? _path;
    private static bool _resolverSet;
    private static bool _initialized;

    /// <summary>Initializes zvec (idempotent); the first call's settings win.</summary>
    public static void Initialize(string? libraryPath, long memoryLimitBytes, int queryThreads)
    {
        lock (Gate)
        {
            SetPath(libraryPath);
            if (_initialized)
            {
                return;
            }

            if (!ZvecNative.zvec_is_initialized())
            {
                var config = ZvecNative.zvec_config_data_create();
                if (memoryLimitBytes > 0)
                {
                    ZvecNative.Check(ZvecNative.zvec_config_data_set_memory_limit(config, (ulong)memoryLimitBytes), "set memory limit");
                }

                if (queryThreads > 0)
                {
                    ZvecNative.Check(ZvecNative.zvec_config_data_set_query_thread_count(config, (uint)queryThreads), "set query threads");
                }

                ZvecNative.Check(ZvecNative.zvec_initialize(config), "initialize");
            }

            _initialized = true;
        }
    }

    /// <summary>Whether the native library can be loaded, and why not.</summary>
    public static bool TryLoad(string? libraryPath, out string? reason)
    {
        lock (Gate)
        {
            SetPath(libraryPath);
        }

        try
        {
            _ = ZvecNative.zvec_is_initialized();
            reason = null;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            reason = $"The zvec native library ({ZvecNative.Library}) cannot be loaded: {ex.Message}";
            return false;
        }
    }

    private static void SetPath(string? libraryPath)
    {
        _path ??= string.IsNullOrWhiteSpace(libraryPath) ? null : libraryPath;
        if (_resolverSet)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(typeof(ZvecLibrary).Assembly, Resolve);
        _resolverSet = true;
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != ZvecNative.Library)
        {
            return 0;
        }

        if (_path is { } path)
        {
            return NativeLibrary.Load(File.Exists(path) ? path : Path.Combine(path, $"lib{ZvecNative.Library}.so"));
        }

        return NativeLibrary.TryLoad(name, assembly, searchPath, out var handle) ? handle : 0;
    }
}

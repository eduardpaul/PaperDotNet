using FubarDev.WebDavServer;
using FubarDev.WebDavServer.AspNetCore;
using FubarDev.WebDavServer.BufferPools;
using FubarDev.WebDavServer.Dispatchers;
using FubarDev.WebDavServer.FileSystem;
using FubarDev.WebDavServer.Formatters;
using FubarDev.WebDavServer.Handlers.Impl;
using FubarDev.WebDavServer.Locking;
using FubarDev.WebDavServer.Props;
using FubarDev.WebDavServer.Props.Dead;
using FubarDev.WebDavServer.Props.Store;
using FubarDev.WebDavServer.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PaperDotNet.WebDav;

/// <summary>
/// Registers the vendored WebDAV server (ADR-0047). Replaces upstream's <c>AddWebDav</c>: explicit registrations
/// instead of Scrutor assembly scanning, no MVC, and no server-to-server COPY/MOVE (the server never calls other hosts).
/// The host supplies an <see cref="IFileSystemFactory"/> and an <see cref="IPropertyStoreFactory"/>; class 2 (locking)
/// also needs an <see cref="ILockManager"/>.
/// </summary>
public static class WebDavServiceCollectionExtensions
{
    public static IServiceCollection AddWebDavServer(this IServiceCollection services, Action<WebDavServerOptions>? configure = null)
    {
        var options = new WebDavServerOptions();
        configure?.Invoke(options);
        services.Configure<WebDavServerOptions>(o => configure?.Invoke(o));

        services.AddHttpContextAccessor();
        services.TryAddScoped<IImplicitLockFactory, DefaultImplicitLockFactory>();
        services.TryAddSingleton<IDeadPropertyFactory, DeadPropertyFactory>();
        services.TryAddSingleton<ISystemClock, SystemClock>();
        services.TryAddSingleton<ITimeoutPolicy, DefaultTimeoutPolicy>();
        services.TryAddSingleton<IWebDavContextAccessor, WebDavContextAccessor>();
        services.TryAddSingleton<IUriComparer, DefaultUriComparer>();
        services.TryAddSingleton<IPathTraversalEngine, PathTraversalEngine>();
        services.TryAddSingleton<IMimeTypeDetector, DefaultMimeTypeDetector>();
        services.TryAddSingleton<IEntryPropertyInitializer, DefaultEntryPropertyInitializer>();
        services.TryAddSingleton<IBufferPoolFactory, ArrayPoolBufferPoolFactory>();
        services.AddOptions();
        services.AddScoped(sp => sp.GetRequiredService<IWebDavContextAccessor>().WebDavContext);
        services.AddScoped<IWebDavDispatcher, WebDavServer>();
        services.AddScoped<IWebDavOutputFormatter, WebDavXmlOutputFormatter>();
        services.AddScoped(sp => sp.GetRequiredService<IBufferPoolFactory>().CreatePool());

        AddForwarded<WebDavDispatcherClass1>(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDefaultDeadPropertyFactory, Class1DeadPropertyFactory>());
        if (options.EnableClass2)
        {
            services.AddSingleton<LockCleanupTask>();
            services.TryAddSingleton<ILockCleanupTask>(sp => sp.GetRequiredService<LockCleanupTask>());
            AddForwarded<WebDavDispatcherClass2>(services);
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IDefaultDeadPropertyFactory, Class2DeadPropertyFactory>());
        }

        AddForwarded<OptionsHandler>(services);
        AddForwarded<GetHeadHandler>(services);
        AddForwarded<PropFindHandler>(services);
        AddForwarded<PutHandler>(services);
        AddForwarded<DeleteHandler>(services);
        AddForwarded<MkColHandler>(services);
        AddForwarded<PropPatchHandler>(services);
        AddForwarded<CopyHandler>(services);
        AddForwarded<MoveHandler>(services);
        AddForwarded<LockHandler>(services);
        AddForwarded<UnlockHandler>(services);

        services.AddScoped(sp => sp.GetRequiredService<IFileSystemFactory>()
            .CreateFileSystem(null, sp.GetRequiredService<IWebDavContext>().User));
        services.AddScoped(sp => sp.GetRequiredService<IPropertyStoreFactory>().Create(sp.GetRequiredService<IFileSystem>()));
        return services;
    }

    /// <summary>One scoped instance, resolvable as itself and as each of its interfaces (upstream: AsImplementedInterfaces).</summary>
    private static void AddForwarded<T>(IServiceCollection services)
        where T : class
    {
        services.TryAddScoped<T>();
        foreach (var contract in typeof(T).GetInterfaces().Where(i => i.Namespace?.StartsWith("FubarDev.WebDavServer", StringComparison.Ordinal) == true))
        {
            services.AddScoped(contract, sp => sp.GetRequiredService<T>());
        }
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Authentication;
using PaperDotNet.Identity.Data;
using PaperDotNet.Identity.Features;
using PaperDotNet.Persistence;

namespace PaperDotNet.IntegrationTests;

public sealed class FirstPartyClientRaceTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_competing_first_party_save_is_reused_without_poisoning_later_saves(bool updateExisting)
    {
        var tenant = await factory.CreateTenantAsync(updateExisting ? "first-party-update-race" : "first-party-insert-race");
        var scopes = factory.Services.GetRequiredService<ITenantScopeFactory>();
        await using var tenantScope = scopes.CreateScope(tenant.Id, tenant.Identifier);
        var services = tenantScope.ServiceProvider;
        var applications = services.GetRequiredService<IOpenIddictApplicationManager>();
        if (!updateExisting)
        {
            await applications.DeleteAsync((await applications.FindByClientIdAsync(OAuthApplication.FirstPartyClientId, Ct))!, Ct);
        }
        var options = new AuthOptions { FirstPartyRedirectUris = ["https://race.example/callback"] };
        Guid? winnerId = null;
        var interceptor = new CompetingSave(updateExisting ? EntityState.Modified : EntityState.Added, async () =>
        {
            // Insert after OpenIddict's validation, immediately before the losing database save.
            await using var competingScope = scopes.CreateScope(tenant.Id, tenant.Identifier);
            var competing = competingScope.ServiceProvider;
            var db = competing.GetRequiredService<IdentityDbContext>();
            await FirstPartyClient.EnsureAsync(competing.GetRequiredService<IOpenIddictApplicationManager>(), db,
                competing.GetRequiredService<IDatabaseProvider>(), new AuthOptions(), Ct);
            winnerId = await db.Applications.Where(a => a.ClientId == OAuthApplication.FirstPartyClientId).Select(a => a.Id).SingleAsync(Ct);
        });
        var contextOptions = new DbContextOptionsBuilder<IdentityDbContext>(services.GetRequiredService<DbContextOptions<IdentityDbContext>>())
            .AddInterceptors(interceptor).Options;
        var registration = new ServiceCollection();
        registration.AddLogging();
        registration.AddScoped(_ => new IdentityDbContext(contextOptions, services.GetRequiredService<ITenantContext>()));
        registration.AddOpenIddict().AddCore(o => o.UseEntityFrameworkCore().UseDbContext<IdentityDbContext>()
            .ReplaceDefaultEntities<OAuthApplication, OAuthAuthorization, OAuthScope, OAuthToken, Guid>());
        await using var provider = registration.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        await FirstPartyClient.EnsureAsync(manager, context, services.GetRequiredService<IDatabaseProvider>(), options, Ct);

        Assert.True(interceptor.Saved);
        if (updateExisting)
        {
            Assert.IsType<DbUpdateConcurrencyException>(interceptor.Failure);
        }
        else
        {
            Assert.True(services.GetRequiredService<IDatabaseProvider>().IsUniqueConstraintViolation(Assert.IsType<DbUpdateException>(interceptor.Failure)));
        }
        var client = Assert.Single(await context.Applications.AsNoTracking().Where(a => a.ClientId == OAuthApplication.FirstPartyClientId).ToListAsync(Ct));
        Assert.Equal(winnerId, client.Id);
        Assert.Contains("https://race.example/callback", await manager.GetRedirectUrisAsync(client, Ct));
        // A losing Added entity must not be retried by unrelated changes on the same context.
        var admin = await context.Users.SingleAsync(u => u.UserName == PaperDotNetApiFactory.AdminUserName, Ct);
        admin.DisplayName = "After competing insert";
        await context.SaveChangesAsync(Ct);
        Assert.Equal(1, await context.Applications.CountAsync(a => a.ClientId == OAuthApplication.FirstPartyClientId, Ct));
    }

    private sealed class CompetingSave(EntityState state, Func<Task> save) : SaveChangesInterceptor
    {
        public bool Saved { get; private set; }
        public Exception? Failure { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Saved && eventData.Context!.ChangeTracker.Entries<OAuthApplication>()
                .Any(e => e.State == state && e.Entity.ClientId == OAuthApplication.FirstPartyClientId))
            {
                Saved = true;
                await save();
            }
            return result;
        }

        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            Failure = eventData.Exception;
            return Task.CompletedTask;
        }

        public override ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(
            ConcurrencyExceptionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Failure = eventData.Exception;
            return ValueTask.FromResult(result);
        }
    }
}

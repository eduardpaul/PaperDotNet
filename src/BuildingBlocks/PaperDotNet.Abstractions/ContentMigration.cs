namespace PaperDotNet.Abstractions;

/// <summary>Legacy readers must exclude workspaces whose authoritative content moved to organization storage.</summary>
public interface IContentMigrationState
{
    Task<IReadOnlyCollection<Guid>> GetMigratedWorkspacesAsync(CancellationToken cancellationToken);
}

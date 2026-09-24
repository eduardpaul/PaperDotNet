using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Data;

namespace PaperDotNet.Identity.Authentication;

/// <summary>
/// Persistent RSA keys of the OAuth server: one for signing (identity tokens, JWKS)
/// and one for encryption. Stored in the database with the private key protected
/// by Data Protection, so every node and every restart uses the same keys. Access
/// and refresh tokens themselves use the Data Protection format.
/// </summary>
internal sealed class ServerKeyStore(IServiceScopeFactory scopes, IDataProtectionProvider dataProtection, TimeProvider time)
    : IConfigureOptions<OpenIddictServerOptions>
{
    public const string Signing = "sig";
    public const string Encryption = "enc";

    private readonly IDataProtector _protector = dataProtection.CreateProtector("PaperDotNet.Identity.ServerKeys");

    public void Configure(OpenIddictServerOptions options)
    {
        var (signing, encryption) = LoadOrCreate();
        options.SigningCredentials.Add(new SigningCredentials(signing, SecurityAlgorithms.RsaSha256));
        options.EncryptionCredentials.Add(new EncryptingCredentials(encryption, SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.Aes256CbcHmacSha512));
    }

    private (RsaSecurityKey Signing, RsaSecurityKey Encryption) LoadOrCreate()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        return (Key(db, Signing), Key(db, Encryption));
    }

    private RsaSecurityKey Key(IdentityDbContext db, string use)
    {
        for (var attempt = 0; ; attempt++)
        {
            var stored = db.ServerKeys.AsNoTracking().Where(k => k.Use == use).OrderBy(k => k.CreatedAt).FirstOrDefault();
            if (stored is not null)
            {
                var rsa = RSA.Create();
                rsa.ImportRSAPrivateKey(_protector.Unprotect(Convert.FromBase64String(stored.ProtectedKey)), out _);
                return new RsaSecurityKey(rsa) { KeyId = stored.Id.ToString("N") };
            }

            using var created = RSA.Create(2048);
            db.ServerKeys.Add(new ServerKey
            {
                Id = Ids.New(),
                Use = use,
                ProtectedKey = Convert.ToBase64String(_protector.Protect(created.ExportRSAPrivateKey())),
                CreatedAt = time.GetUtcNow(),
            });
            try
            {
                db.SaveChanges();
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                db.ChangeTracker.Clear();
            }
        }
    }
}

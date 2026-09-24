using PaperDotNet.Identity.Authentication;

namespace PaperDotNet.UnitTests;

public sealed class ApiTokenSecretTests
{
    [Fact]
    public void Created_tokens_are_prefixed_unique_and_hashed()
    {
        var (token, prefix, hash) = ApiTokenSecret.Create();
        var (other, _, otherHash) = ApiTokenSecret.Create();

        Assert.StartsWith(ApiTokenSecret.Prefix, token, StringComparison.Ordinal);
        Assert.StartsWith(prefix, token, StringComparison.Ordinal);
        Assert.NotEqual(token, other);
        Assert.Equal(hash, ApiTokenSecret.Hash(token));
        Assert.NotEqual(hash, otherHash);
        Assert.True(ApiTokenSecret.LooksLikeToken(token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("eyJhbGciOi.jwt.token")]
    [InlineData("pdn_")]
    public void Non_tokens_are_not_recognized(string? value) =>
        Assert.False(ApiTokenSecret.LooksLikeToken(value));
}

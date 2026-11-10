using System.Text;

namespace UsageTracker.Core.Tests;

public class CredentialTests
{
    internal static string Jwt(long exp)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("""{"alg":"none"}""")}.{B64($$"""{"exp":{{exp}},"sub":"user"}""")}.sig";
    }

    [Fact]
    public void Reads_a_claude_code_sign_in()
    {
        const string json = """
            {"claudeAiOauth":{"accessToken":"sk-ant-oat01-abc","refreshToken":"sk-ant-ort01-def","expiresAt":1762750000000,"scopes":["user:inference"],"subscriptionType":"max"}}
            """;

        var creds = CredentialFiles.ParseClaude(json, "Windows");

        Assert.NotNull(creds);
        Assert.Equal("sk-ant-oat01-abc", creds!.AccessToken);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1762750000000), creds.ExpiresAt);
        Assert.Equal("max", creds.SubscriptionType);
        Assert.Equal("Windows", creds.SourceLabel);
        Assert.True(creds.IsExpired(DateTimeOffset.FromUnixTimeMilliseconds(1762750000001)));
        Assert.False(creds.IsExpired(DateTimeOffset.FromUnixTimeMilliseconds(1762749999999)));
    }

    [Theory]
    [InlineData("""{"claudeAiOauth":{"accessToken":""}}""")]
    [InlineData("""{"claudeAiOauth":null}""")]
    [InlineData("""{"somethingElse":{}}""")]
    [InlineData("""not json""")]
    public void Claude_files_without_a_token_are_ignored(string json)
    {
        Assert.Null(CredentialFiles.ParseClaude(json, "x"));
    }

    [Fact]
    public void Claude_sign_in_without_expiry_never_counts_as_expired()
    {
        var creds = CredentialFiles.ParseClaude("""{"claudeAiOauth":{"accessToken":"t"}}""", "x");
        Assert.False(creds!.IsExpired(DateTimeOffset.MaxValue));
    }

    [Fact]
    public void Reads_a_codex_chatgpt_sign_in_and_its_expiry()
    {
        var token = Jwt(1763000000);
        var json = $$"""
            {"OPENAI_API_KEY":null,"tokens":{"id_token":"x.y.z","access_token":"{{token}}","refresh_token":"r","account_id":"acct-123"},"last_refresh":"2026-11-01T00:00:00Z"}
            """;

        var creds = CredentialFiles.ParseCodex(json, "Windows", out var apiKeyOnly);

        Assert.NotNull(creds);
        Assert.False(apiKeyOnly);
        Assert.Equal(token, creds!.AccessToken);
        Assert.Equal("acct-123", creds.AccountId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1763000000), creds.ExpiresAt);
    }

    [Fact]
    public void A_codex_api_key_sign_in_is_recognised()
    {
        var creds = CredentialFiles.ParseCodex("""{"OPENAI_API_KEY":"sk-proj-123","tokens":null}""", "Windows", out var apiKeyOnly);

        Assert.Null(creds);
        Assert.True(apiKeyOnly);
    }

    [Fact]
    public void An_opaque_codex_token_has_no_known_expiry()
    {
        var creds = CredentialFiles.ParseCodex("""{"tokens":{"access_token":"opaque","account_id":""}}""", "x", out _);

        Assert.Null(creds!.ExpiresAt);
        Assert.Null(creds.AccountId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("a.!!!.c")]
    [InlineData("a.e30.c")] // "{}" has no exp
    public void Jwt_expiry_is_null_for_non_jwts(string token)
    {
        Assert.Null(CredentialFiles.JwtExpiry(token));
    }

    [Fact]
    public void Reading_a_missing_file_returns_null()
    {
        Assert.Null(CredentialFiles.ReadShared(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));
    }

    [Fact]
    public void Reading_does_not_change_the_file()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, "{\"a\":1}");
        var before = File.GetLastWriteTimeUtc(path);
        try
        {
            Assert.Equal("{\"a\":1}", CredentialFiles.ReadShared(path));
            Assert.Equal(before, File.GetLastWriteTimeUtc(path));
            Assert.Equal("{\"a\":1}", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

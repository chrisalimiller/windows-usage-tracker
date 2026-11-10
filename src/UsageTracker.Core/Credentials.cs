using System.Text;
using System.Text.Json;

namespace UsageTracker.Core;

/// <summary>A place a credentials file might live, with a label for logs and tooltips.</summary>
/// <param name="Path">Full path to the file.</param>
/// <param name="Label">Short description such as "Windows" or "WSL Ubuntu".</param>
public sealed record CredentialLocation(string Path, string Label);

/// <summary>A Claude Code OAuth sign-in read from <c>.credentials.json</c>.</summary>
public sealed record ClaudeCredentials(string AccessToken, DateTimeOffset? ExpiresAt, string? SubscriptionType, string SourceLabel)
{
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } e && e <= now;
}

/// <summary>A Codex ChatGPT-plan sign-in read from <c>auth.json</c>.</summary>
public sealed record CodexCredentials(string AccessToken, string? AccountId, DateTimeOffset? ExpiresAt, string SourceLabel)
{
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } e && e <= now;
}

/// <summary>Reads credential files without ever writing to them.</summary>
public static class CredentialFiles
{
    /// <summary>
    /// Reads a file's text while letting the owning CLI keep writing it.
    /// Returns null when the file is missing or unreadable.
    /// </summary>
    public static string? ReadShared(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses Claude Code's credentials file:
    /// <c>{"claudeAiOauth":{"accessToken":"…","refreshToken":"…","expiresAt":1762750000000,"subscriptionType":"max"}}</c>.
    /// <c>expiresAt</c> is in milliseconds since the Unix epoch.
    /// </summary>
    public static ClaudeCredentials? ParseClaude(string json, string sourceLabel)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var oauth = doc.RootElement.Child("claudeAiOauth");
            if (oauth is not { ValueKind: JsonValueKind.Object } o) return null;
            var token = o.Child("accessToken").String();
            if (string.IsNullOrWhiteSpace(token)) return null;

            DateTimeOffset? expires = null;
            if (o.Child("expiresAt").Integer() is { } ms and > 0)
            {
                // Treat small values as seconds, in case the format ever changes.
                expires = ms > 100_000_000_000 ? FromUnixMs(ms) : FromUnixSeconds(ms);
            }

            return new ClaudeCredentials(token.Trim(), expires, o.Child("subscriptionType").String(), sourceLabel);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses the Codex CLI's <c>auth.json</c>:
    /// <c>{"OPENAI_API_KEY":null,"tokens":{"id_token":"…","access_token":"…","refresh_token":"…","account_id":"…"},"last_refresh":"…"}</c>.
    /// Returns null and sets <paramref name="apiKeyOnly"/> when the file holds only an API key.
    /// </summary>
    public static CodexCredentials? ParseCodex(string json, string sourceLabel, out bool apiKeyOnly)
    {
        apiKeyOnly = false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tokens = root.Child("tokens");
            var token = tokens?.Child("access_token").String();
            if (string.IsNullOrWhiteSpace(token))
            {
                apiKeyOnly = !string.IsNullOrWhiteSpace(root.Child("OPENAI_API_KEY").String());
                return null;
            }

            var accountId = tokens?.Child("account_id").String();
            return new CodexCredentials(token.Trim(), string.IsNullOrWhiteSpace(accountId) ? null : accountId, JwtExpiry(token), sourceLabel);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads the <c>exp</c> claim of a JWT without validating it. Null if it is not a JWT.</summary>
    public static DateTimeOffset? JwtExpiry(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.Child("exp").Integer() is { } exp and > 0 ? FromUnixSeconds(exp) : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static DateTimeOffset? FromUnixMs(long ms) =>
        ms < 253_402_300_799_000 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;

    private static DateTimeOffset? FromUnixSeconds(long s) =>
        s < 253_402_300_799 ? DateTimeOffset.FromUnixTimeSeconds(s) : null;
}

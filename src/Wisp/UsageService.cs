using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wisp;

/// <summary>One rate-limit window: how much of it is used and when it resets.</summary>
public sealed record UsageWindow(double Percent, DateTimeOffset? ResetsAt);

public sealed record UsageSnapshot(UsageWindow? Session, UsageWindow? Week, string Source, DateTimeOffset FetchedAt);

public class UsageException(string message) : Exception(message);

/// <summary>Anthropic asked us to back off; don't call again until <see cref="RetryAfter"/> has passed.</summary>
public sealed class RateLimitedException(TimeSpan retryAfter) : UsageException("Anthropic asked Wisp to slow down")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}

/// <summary>
/// Reads plan usage (the 5-hour session window and the weekly window) from Anthropic.
///
/// Two sources, tried in order:
///  1. Claude Code's OAuth login (~/.claude/.credentials.json) against api.anthropic.com/api/oauth/usage.
///     This is the same data Claude Code's /usage command shows. Wisp only reads the token; it never
///     refreshes or rewrites it, so it can't disturb your Claude Code login.
///  2. A claude.ai sessionKey cookie the user pastes in, against claude.ai/api/organizations/{id}/usage.
/// </summary>
public sealed class UsageService : IDisposable
{
    const string OAuthUsageUrl = "https://api.anthropic.com/api/oauth/usage";
    const string WebBase = "https://claude.ai/api/organizations";

    readonly HttpClient _http;
    string? _orgId;
    string? _orgKey;

    public UsageService()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public static string CredentialsPath
    {
        get
        {
            var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrWhiteSpace(configDir))
                configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            return Path.Combine(configDir, ".credentials.json");
        }
    }

    /// <summary>
    /// Fetches usage over the network. The OAuth endpoint is skipped until <paramref name="oauthBlockedUntil"/>;
    /// when it rate-limits us, <paramref name="onOAuthRateLimited"/> gets the time it's OK to try again.
    /// </summary>
    public async Task<UsageSnapshot> FetchAsync(string? sessionKey, DateTimeOffset oauthBlockedUntil,
        Action<DateTimeOffset> onOAuthRateLimited, CancellationToken ct)
    {
        UsageException? oauthError = null;
        if (DateTimeOffset.UtcNow >= oauthBlockedUntil)
        {
            var token = ReadOAuthToken(out var tokenError);
            if (token != null)
            {
                try { return await FetchOAuthAsync(token, ct); }
                catch (RateLimitedException e)
                {
                    onOAuthRateLimited(DateTimeOffset.UtcNow + e.RetryAfter);
                    oauthError = e;
                }
                catch (UsageException e) { oauthError = e; }
            }
            else if (tokenError != null)
            {
                oauthError = new UsageException(tokenError);
            }
        }
        else
        {
            oauthError = new RateLimitedException(oauthBlockedUntil - DateTimeOffset.UtcNow);
        }

        if (!string.IsNullOrWhiteSpace(sessionKey))
            return await FetchWebAsync(sessionKey.Trim(), ct);

        throw oauthError ?? new UsageException("No Claude login found");
    }

    static string? ReadOAuthToken(out string? error)
    {
        error = null;
        try
        {
            var path = CredentialsPath;
            if (!File.Exists(path)) return null;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)) return null;
            if (!oauth.TryGetProperty("accessToken", out var tokenEl)) return null;

            if (oauth.TryGetProperty("expiresAt", out var exp) && exp.ValueKind == JsonValueKind.Number &&
                DateTimeOffset.FromUnixTimeMilliseconds(exp.GetInt64()) < DateTimeOffset.UtcNow)
            {
                error = "Login expired · open Claude Code";
                return null;
            }
            return tokenEl.GetString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    async Task<UsageSnapshot> FetchOAuthAsync(string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, OAuthUsageUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        req.Headers.UserAgent.ParseAdd("Wisp/0.1 (+https://github.com/fabiolage/claude-wisp)");

        using var res = await Send(req, ct);
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UsageException("Login expired · open Claude Code");
        await EnsureOk(res);

        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return Parse(doc.RootElement, "Claude Code");
    }

    async Task<UsageSnapshot> FetchWebAsync(string sessionKey, CancellationToken ct)
    {
        if (_orgId == null || _orgKey != sessionKey)
        {
            _orgId = await FindOrgAsync(sessionKey, ct);
            _orgKey = sessionKey;
        }

        using var req = WebRequest(sessionKey, $"{WebBase}/{_orgId}/usage");
        using var res = await Send(req, ct);
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            _orgId = null;
            throw new UsageException(res.StatusCode == HttpStatusCode.Forbidden
                ? "claude.ai refused · update session key"
                : "Session key expired");
        }
        await EnsureOk(res);

        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return Parse(doc.RootElement, "claude.ai");
    }

    async Task<string> FindOrgAsync(string sessionKey, CancellationToken ct)
    {
        using var req = WebRequest(sessionKey, WebBase);
        using var res = await Send(req, ct);
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UsageException("Session key invalid");
        await EnsureOk(res);

        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            throw new UsageException("No claude.ai organization");

        // Prefer the org that has chat (the consumer plan) over API-only orgs.
        string? first = null;
        foreach (var org in doc.RootElement.EnumerateArray())
        {
            var id = org.TryGetProperty("uuid", out var u) ? u.GetString() : null;
            if (id == null) continue;
            first ??= id;
            if (org.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array)
                foreach (var c in caps.EnumerateArray())
                    if (c.GetString() == "chat") return id;
        }
        return first ?? throw new UsageException("No claude.ai organization");
    }

    static HttpRequestMessage WebRequest(string sessionKey, string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Cookie", $"sessionKey={sessionKey}");
        req.Headers.Referrer = new Uri("https://claude.ai/settings/usage");
        req.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        return req;
    }

    async Task<HttpResponseMessage> Send(HttpRequestMessage req, CancellationToken ct)
    {
        try { return await _http.SendAsync(req, ct); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new UsageException("Timed out"); }
        catch (HttpRequestException) { throw new UsageException("Offline"); }
    }

    static Task EnsureOk(HttpResponseMessage res)
    {
        if (res.IsSuccessStatusCode) return Task.CompletedTask;
        if (res.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var wait = res.Headers.RetryAfter?.Delta
                ?? (res.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : null);
            // Missing or zero Retry-After still means "not now": wait at least 15 minutes, at most 2 hours.
            var seconds = Math.Clamp(wait?.TotalSeconds ?? 0, 15 * 60, 2 * 60 * 60);
            throw new RateLimitedException(TimeSpan.FromSeconds(seconds));
        }
        throw new UsageException($"Server said {(int)res.StatusCode}");
    }

    static UsageSnapshot Parse(JsonElement root, string source) =>
        new(ParseWindow(root, "five_hour"), ParseWindow(root, "seven_day"), source, DateTimeOffset.Now);

    static UsageWindow? ParseWindow(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        if (!w.TryGetProperty("utilization", out var u) || u.ValueKind != JsonValueKind.Number) return null;

        DateTimeOffset? resets = null;
        if (w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(r.GetString(), out var parsed))
            resets = parsed;

        return new UsageWindow(Math.Clamp(u.GetDouble(), 0, 100), resets);
    }

    public void Dispose() => _http.Dispose();
}

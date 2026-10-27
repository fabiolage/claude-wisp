using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wisp;

/// <summary>
/// Live usage straight from Claude Code, with no polling of Anthropic at all.
///
/// Claude Code pipes a JSON blob (including <c>rate_limits</c>) to its status line command every time
/// it redraws. When Wisp is registered as that command (<c>Wisp.exe --statusline</c>) it saves the
/// numbers to a small feed file and prints a compact status line back. The widget reads the feed.
/// </summary>
public static class ClaudeCodeFeed
{
    static readonly string FeedPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wisp", "feed.json");

    public static string FeedDirectory => Path.GetDirectoryName(FeedPath)!;
    public static string FeedFileName => Path.GetFileName(FeedPath);

    static string ClaudeDir
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            return string.IsNullOrWhiteSpace(dir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
                : dir;
        }
    }

    static string ClaudeSettingsPath => Path.Combine(ClaudeDir, "settings.json");

    // ---------- status line mode (runs inside Claude Code, must be fast) ----------

    public static int RunStatusLine()
    {
        try
        {
            var input = Console.In.ReadToEnd();
            using var doc = JsonDocument.Parse(input);
            var root = doc.RootElement;

            var model = root.TryGetProperty("model", out var m) && m.TryGetProperty("display_name", out var dn)
                ? dn.GetString()
                : null;

            UsageWindow? session = null, week = null;
            if (root.TryGetProperty("rate_limits", out var rl) && rl.ValueKind == JsonValueKind.Object)
            {
                session = ReadWindow(rl, "five_hour");
                week = ReadWindow(rl, "seven_day");
                if (session != null || week != null)
                    Write(new UsageSnapshot(session, week, "Claude Code", DateTimeOffset.Now));
            }

            var parts = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(model)) parts.Add(model!);
            if (session != null) parts.Add($"session {Math.Round(session.Percent):0}% (resets {Format.Reset(session.ResetsAt)})");
            if (week != null) parts.Add($"week {Math.Round(week.Percent):0}%");
            Console.Out.Write(string.Join(" | ", parts));
            Console.Out.Flush();
            return 0;
        }
        catch
        {
            return 0; // never break Claude Code's status line
        }
    }

    static UsageWindow? ReadWindow(JsonElement rl, string name)
    {
        if (!rl.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        if (!w.TryGetProperty("used_percentage", out var u) || u.ValueKind != JsonValueKind.Number) return null;

        DateTimeOffset? resets = null;
        if (w.TryGetProperty("resets_at", out var r))
        {
            if (r.ValueKind == JsonValueKind.Number)
            {
                var v = r.GetDouble();
                resets = v > 1e11 ? DateTimeOffset.FromUnixTimeMilliseconds((long)v) : DateTimeOffset.FromUnixTimeSeconds((long)v);
            }
            else if (r.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(r.GetString(), out var p))
            {
                resets = p;
            }
        }
        return new UsageWindow(Math.Clamp(u.GetDouble(), 0, 100), resets);
    }

    // ---------- feed file ----------

    sealed record FeedWindow(double Percent, DateTimeOffset? ResetsAt);
    sealed record Feed(FeedWindow? Session, FeedWindow? Week, DateTimeOffset UpdatedAt);

    static void Write(UsageSnapshot snap)
    {
        Directory.CreateDirectory(FeedDirectory);
        var feed = new Feed(
            snap.Session is { } s ? new FeedWindow(s.Percent, s.ResetsAt) : null,
            snap.Week is { } w ? new FeedWindow(w.Percent, w.ResetsAt) : null,
            snap.FetchedAt);
        var tmp = FeedPath + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(feed));
        File.Move(tmp, FeedPath, overwrite: true);
    }

    public static UsageSnapshot? Read()
    {
        try
        {
            if (!File.Exists(FeedPath)) return null;
            using var stream = new FileStream(FeedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var feed = JsonSerializer.Deserialize<Feed>(stream);
            if (feed == null) return null;
            return new UsageSnapshot(
                feed.Session is { } s ? new UsageWindow(s.Percent, s.ResetsAt) : null,
                feed.Week is { } w ? new UsageWindow(w.Percent, w.ResetsAt) : null,
                "Claude Code",
                feed.UpdatedAt);
        }
        catch
        {
            return null;
        }
    }

    // ---------- hooking into Claude Code's settings ----------

    public enum ConnectResult { Connected, AlreadyConnected, OtherStatusLine, Failed }

    static string Command => $"\"{(Environment.ProcessPath ?? "Wisp.exe").Replace('\\', '/')}\" --statusline";

    public static bool IsConnected()
    {
        var cmd = ReadStatusLineCommand(out _);
        return cmd != null && cmd.Contains("--statusline") && cmd.Contains("Wisp", StringComparison.OrdinalIgnoreCase);
    }

    static string? ReadStatusLineCommand(out JsonObject? settings)
    {
        settings = null;
        try
        {
            if (!File.Exists(ClaudeSettingsPath)) { settings = new JsonObject(); return null; }
            settings = JsonNode.Parse(File.ReadAllText(ClaudeSettingsPath),
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
            return settings?["statusLine"]?["command"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    public static ConnectResult Connect(bool replaceExisting)
    {
        try
        {
            var existing = ReadStatusLineCommand(out var settings);
            if (settings == null) return ConnectResult.Failed;
            if (existing != null && existing == Command) return ConnectResult.AlreadyConnected;
            if (existing != null && !IsConnected() && !replaceExisting) return ConnectResult.OtherStatusLine;

            Directory.CreateDirectory(ClaudeDir);
            if (File.Exists(ClaudeSettingsPath))
                File.Copy(ClaudeSettingsPath, ClaudeSettingsPath + ".wisp-backup", overwrite: true);

            settings["statusLine"] = new JsonObject
            {
                ["type"] = "command",
                ["command"] = Command,
                ["padding"] = 0,
            };
            File.WriteAllText(ClaudeSettingsPath, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return ConnectResult.Connected;
        }
        catch
        {
            return ConnectResult.Failed;
        }
    }

    public static void Disconnect()
    {
        try
        {
            if (!IsConnected()) return;
            ReadStatusLineCommand(out var settings);
            if (settings == null) return;
            settings.Remove("statusLine");
            File.WriteAllText(ClaudeSettingsPath, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

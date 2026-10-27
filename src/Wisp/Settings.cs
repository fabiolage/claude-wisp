using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wisp;

public enum Corner { TopLeft, TopRight, BottomLeft, BottomRight }

public sealed class Settings
{
    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wisp");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public Corner Corner { get; set; } = Corner.BottomRight;

    /// <summary>Resting opacity of the widget, 0..1.</summary>
    public double Opacity { get; set; } = 0.55;

    public int RefreshSeconds { get; set; } = 60;

    /// <summary>Fade almost fully away while the mouse is over the widget.</summary>
    public bool FadeOnHover { get; set; } = true;

    public bool Hidden { get; set; }

    /// <summary>claude.ai sessionKey cookie, encrypted with Windows DPAPI for the current user.</summary>
    public string? SessionKeyProtected { get; set; }

    [JsonIgnore]
    public string? SessionKey
    {
        get
        {
            if (string.IsNullOrEmpty(SessionKeyProtected)) return null;
            try
            {
                var bytes = ProtectedData.Unprotect(
                    Convert.FromBase64String(SessionKeyProtected), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return null; }
        }
        set
        {
            SessionKeyProtected = string.IsNullOrWhiteSpace(value)
                ? null
                : Convert.ToBase64String(ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser));
        }
    }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { }
    }
}

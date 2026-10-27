using System;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Wisp;

public partial class App : Application
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    Mutex? _single;
    Settings _settings = null!;
    MainWindow _widget = null!;
    Forms.NotifyIcon _tray = null!;
    Forms.ToolStripMenuItem _header = null!;
    Forms.ToolStripMenuItem _toggle = null!;
    Forms.ToolStripMenuItem _claudeCode = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _single = new Mutex(true, @"Local\Wisp.ClaudeUsage", out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        _settings = Settings.Load();
        _widget = new MainWindow(_settings);
        _widget.Updated += OnUpdated;
        BuildTray();

        if (!_settings.Hidden) _widget.Show();
        UpdateToggleText();
        OfferClaudeCodeFeed();
    }

    /// <summary>
    /// Anthropic's usage endpoint rate-limits third-party apps hard, so the best source is Claude Code itself.
    /// Offer (once) to plug Wisp into Claude Code's status line.
    /// </summary>
    void OfferClaudeCodeFeed()
    {
        if (_settings.OfferedClaudeCodeConnect || ClaudeCodeFeed.IsConnected() ||
            !System.IO.File.Exists(UsageService.CredentialsPath))
            return;

        _settings.OfferedClaudeCodeConnect = true;
        _settings.Save();
        _tray.BalloonTipClicked += OnOfferClicked;
        _tray.ShowBalloonTip(15000, "Get live usage from Claude Code",
            "Click here to let Claude Code send its usage to Wisp as you work. You can undo it from the tray menu.",
            Forms.ToolTipIcon.Info);
    }

    void OnOfferClicked(object? sender, EventArgs e)
    {
        _tray.BalloonTipClicked -= OnOfferClicked;
        ConnectClaudeCode();
        _claudeCode.Checked = ClaudeCodeFeed.IsConnected();
    }

    void ConnectClaudeCode()
    {
        var result = ClaudeCodeFeed.Connect(replaceExisting: false);
        if (result == ClaudeCodeFeed.ConnectResult.OtherStatusLine)
        {
            var answer = System.Windows.MessageBox.Show(
                "Claude Code already has a custom status line. Replace it with Wisp's?\n\n" +
                "Your current settings are backed up next to them as settings.json.wisp-backup.",
                "Wisp", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            result = ClaudeCodeFeed.Connect(replaceExisting: true);
        }

        if (result == ClaudeCodeFeed.ConnectResult.Failed)
            System.Windows.MessageBox.Show("Wisp couldn't update Claude Code's settings.json.", "Wisp",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            _tray.ShowBalloonTip(6000, "Connected to Claude Code",
                "Wisp updates live whenever Claude Code is open. Restart any open Claude Code sessions to start.",
                Forms.ToolTipIcon.Info);
    }

    void BuildTray()
    {
        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };

        _header = new Forms.ToolStripMenuItem("Wisp · loading…") { Enabled = false };
        menu.Items.Add(_header);
        menu.Items.Add(new Forms.ToolStripSeparator());

        _toggle = new Forms.ToolStripMenuItem("Hide", null, (_, _) => ToggleWidget());
        menu.Items.Add(_toggle);
        menu.Items.Add(new Forms.ToolStripMenuItem("Refresh now", null, async (_, _) => await _widget.RefreshAsync(force: true)));

        var position = new Forms.ToolStripMenuItem("Position");
        foreach (var (label, corner) in new[]
                 {
                     ("Top left", Corner.TopLeft), ("Top right", Corner.TopRight),
                     ("Bottom left", Corner.BottomLeft), ("Bottom right", Corner.BottomRight),
                 })
        {
            var item = new Forms.ToolStripMenuItem(label) { Checked = _settings.Corner == corner };
            item.Click += (_, _) =>
            {
                _widget.SetCorner(corner);
                foreach (Forms.ToolStripMenuItem i in position.DropDownItems) i.Checked = i == item;
            };
            position.DropDownItems.Add(item);
        }
        menu.Items.Add(position);

        var look = new Forms.ToolStripMenuItem("Visibility");
        var levels = new System.Collections.Generic.List<Forms.ToolStripMenuItem>();
        foreach (var (label, value) in new[] { ("Whisper", 0.30), ("Ghost", 0.55), ("Clear", 0.85) })
        {
            var item = new Forms.ToolStripMenuItem(label) { Checked = Math.Abs(_settings.Opacity - value) < 0.01 };
            item.Click += (_, _) =>
            {
                _widget.SetRestingOpacity(value);
                foreach (var i in levels) i.Checked = i == item;
            };
            levels.Add(item);
            look.DropDownItems.Add(item);
        }
        look.DropDownItems.Add(new Forms.ToolStripSeparator());
        var fade = new Forms.ToolStripMenuItem("Fade away under mouse") { Checked = _settings.FadeOnHover, CheckOnClick = true };
        fade.CheckedChanged += (_, _) => { _settings.FadeOnHover = fade.Checked; _settings.Save(); };
        look.DropDownItems.Add(fade);
        menu.Items.Add(look);

        menu.Items.Add(new Forms.ToolStripSeparator());
        _claudeCode = new Forms.ToolStripMenuItem("Live updates from Claude Code") { Checked = ClaudeCodeFeed.IsConnected() };
        _claudeCode.Click += (_, _) =>
        {
            if (_claudeCode.Checked) ClaudeCodeFeed.Disconnect();
            else ConnectClaudeCode();
            _claudeCode.Checked = ClaudeCodeFeed.IsConnected();
        };
        menu.Items.Add(_claudeCode);
        menu.Items.Add(new Forms.ToolStripMenuItem("claude.ai session key…", null, (_, _) => AskSessionKey()));

        var startup = new Forms.ToolStripMenuItem("Start with Windows") { Checked = IsStartupEnabled(), CheckOnClick = true };
        startup.CheckedChanged += (_, _) => SetStartup(startup.Checked);
        menu.Items.Add(startup);

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Quit Wisp", null, (_, _) => Quit()));

        _tray = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Wisp",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) ToggleWidget();
        };
    }

    static System.Drawing.Icon LoadIcon()
    {
        var res = GetResourceStream(new Uri("pack://application:,,,/wisp.ico"));
        using var stream = res!.Stream;
        return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    void OnUpdated(UsageSnapshot? snap, string? error)
    {
        string text;
        if (snap?.Session is { } s)
        {
            text = $"Session {Math.Round(s.Percent):0}% · resets in {Format.Reset(s.ResetsAt)}";
            if (snap.Week is { } w) text += $" · week {Math.Round(w.Percent):0}%";
        }
        else
        {
            text = error ?? "No data yet";
        }

        _header.Text = text;
        var tip = "Wisp · " + text;
        _tray.Text = tip.Length > 120 ? tip[..120] : tip;

        if (snap == null && error == "No Claude login found" && !_askedOnce)
        {
            _askedOnce = true;
            _tray.ShowBalloonTip(8000, "Wisp can't find your Claude login",
                "Sign in to Claude Code on this PC, or right-click the ghost icon and choose \"claude.ai session key…\".",
                Forms.ToolTipIcon.Info);
        }
    }

    bool _askedOnce;

    void ToggleWidget()
    {
        if (_widget.IsVisible) _widget.Hide();
        else _widget.Show();
        _settings.Hidden = !_widget.IsVisible;
        _settings.Save();
        UpdateToggleText();
    }

    void UpdateToggleText() => _toggle.Text = _widget.IsVisible ? "Hide widget" : "Show widget";

    async void AskSessionKey()
    {
        var dlg = new SessionKeyWindow();
        if (dlg.ShowDialog() != true) return;
        _settings.SessionKey = dlg.Key;
        _settings.Save();
        await _widget.RefreshAsync();
    }

    static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("Wisp") is string;
    }

    static void SetStartup(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on && Environment.ProcessPath is { } exe) key.SetValue("Wisp", $"\"{exe}\"");
        else key.DeleteValue("Wisp", throwOnMissingValue: false);
    }

    void Quit()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _widget.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _single?.Dispose();
        base.OnExit(e);
    }
}

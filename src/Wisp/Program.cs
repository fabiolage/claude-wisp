using System;

namespace Wisp;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Claude Code status line mode: read JSON from stdin, save usage, print a line, exit.
        // Handled before WPF loads so it stays fast.
        if (Array.IndexOf(args, "--statusline") >= 0)
            return ClaudeCodeFeed.RunStatusLine();

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}

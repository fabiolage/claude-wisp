# Wisp

A ghostly Claude usage meter for Windows. Wisp floats in a corner of your screen, barely there, and shows how much of your Claude plan you've used:

- **Session**: the 5-hour usage window, as a big percentage, with a countdown to the reset
- **Week**: the 7-day window, as a thin line underneath

It's see-through and click-through (your mouse passes right through it), it fades away almost completely when your cursor drifts over it, and the bar glows from pale blue to amber to coral as you approach your limit. It refreshes every minute.

## Install

1. Open the [latest release](../../releases/latest).
2. Download **Wisp.exe** and run it. Windows SmartScreen may warn because the app isn't signed: click *More info* → *Run anyway*.
3. A little ghost appears in the system tray. Left-click it to show or hide the widget; right-click it for options.

## Where the numbers come from

1. **Claude Code, live (best).** Right-click the tray ghost → *Live updates from Claude Code* (Wisp also offers this on first launch). Wisp registers itself as Claude Code's status line, so every time Claude Code redraws, it hands Wisp your current 5-hour and weekly usage. No polling, no rate limits, and you get a compact usage line inside Claude Code too. Your previous `settings.json` is backed up as `settings.json.wisp-backup`.
2. **Anthropic's usage endpoint (fallback).** If you've signed in to Claude Code, Wisp can also ask Anthropic directly using that login, read-only. This endpoint rate-limits third-party apps hard, so Wisp only calls it every 10 minutes when there's no fresh Claude Code data, and backs off for as long as Anthropic asks.
3. **claude.ai session key.** Not using Claude Code? Right-click the tray ghost → *claude.ai session key…* and paste your `sessionKey` cookie from claude.ai. It's encrypted with Windows DPAPI so only your Windows user can read it.

The endpoints in 2 and 3 are unofficial and could change.

## Options (tray menu)

| Option | What it does |
|---|---|
| Position | Top left, top right, bottom left, bottom right |
| Visibility | Whisper (30%), Ghost (55%), Clear (85%), and "fade away under mouse" |
| Refresh now | Fetch usage immediately |
| Start with Windows | Launch Wisp when you sign in |

Settings live in `%APPDATA%\Wisp\settings.json`.

## Building

Every push to `main` builds on GitHub Actions and publishes a release, so you never need a local toolchain. To build yourself: install the .NET 10 SDK on Windows and run

```
dotnet publish src/Wisp/Wisp.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

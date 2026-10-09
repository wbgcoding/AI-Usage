# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.1.1] - unreleased

### Added

- A portable exe for ARM64, `AI-Usage-arm64.exe`, joins the downloads.

### Changed

- AI-Usage no longer carries its own copy of .NET, so the downloads shrink from about 136 MB to a fraction of that. It uses the .NET 10 Desktop Runtime installed on your PC: the installer downloads it from Microsoft when it is missing, and a portable copy shows a download link instead of starting. A portable 1.1.0 that updates itself therefore starts only once the runtime is installed. See "Coming from version 1.1.0" in the README.

- In the small layout the usage per day tile shows as many recent days as fit across its full width, not just the last seven.
- The button that closes the welcome window now reads "Done".
- The "Show tooltips" switch is gone from the settings; tooltips stay on, and `settings.json` can still turn them off.
- The README explains that project colors come from app icons found in your project folders.

## [1.1.0] - 2026-10-08

A calmer, tighter widget, a token usage window you can arrange yourself and a day grid that reaches back further, plus many fixes that make the app faster, lighter and more reliable.

### Added

- A small legend under each tile chart names the 5 hour and the 7 day line.
- A yellow level now shows a warning sign too, not just a colour.
- The selected day in the token usage window opens in its own box with a close button.
- The year grid dims the days outside the chosen period.
- Removing an account asks first, deletes its sign-in in AI-Usage and, if you tick it, its history.
- The token usage window can be rearranged: drag a section by its heading above, below or beside another one, or use Alt+Up/Down; the layout is remembered and can be reset.

### Changed

- Each window shows its percentage on its own ("42%"); the countdown and clock sit smaller and grey beside it.
- Token counts and the "full in about 3h" forecast sit small and grey right of the window name; the forecast, then the tokens give way when the tile is narrow.
- The tile header always counts from the last fetch; when the data itself is older, its tooltip says so.
- The statistics button shows a rising zigzag chart, the providers and layout button a slider icon.
- Dragging a section in the token usage window is smoother: a picture of it follows the pointer, the drop line glides, auto-scroll runs at frame rate and the sections slide into their new places.
- With the app icon or "Automatic" chosen for the notification area, the window list next to it is hidden; the About page links "GitHub"; the About page drops its tagline.
- Windows are named by their length ("7 days"), Copilot buckets are translated and Cursor's second group is called "API models".
- Resets more than six days away show a date instead of a weekday, and countdowns drop zero units ("9h", "19d").
- The data source moved from the tile header into the name's tooltip.
- Tiles are tighter, the bar track has no outline, and the density "Compact" is gone (a saved Compact becomes Automatic).
- The tile chart draws the weekly line in the accent colour, keeps its dates below the curves and leaves out the year.
- The usage per day grids reach back as far as the window is wide, beyond one year, at least the last twelve months in the token usage window; a day keeps its shade while resizing, and January shows its year once the grid spans more than one.
- Token figures keep three digits ("33.5 M", "1.00 B"), with the exact count in the tooltip; chart axes use round steps and the hour axis shows four marks (0, 6, 12 and 18 o'clock).
- Bars in the day detail carry their model and project colours, and "Save as CSV" sits next to "Group by".
- New title bar icons for the layout menu and the statistics; a second click on the layout icon closes its menu.
- The Nebula theme is clearly violet.
- Error messages name the cause (no answer in time, HTTP code, no connection), and a turned away request offers to sign in again.
- The welcome text says exactly which services AI-Usage talks to.
- Smaller download: the program file is about 9 MB smaller.
- The usage per day grids no longer outline today.
- Less work in the background: the token index checks thousands of unchanged session files in a fraction of the time and reads new log lines with far less memory, the Claude limit check and the tile charts only read what was added since the last look, and the hidden browser rests between reads and is released after 15 idle minutes.
- A locked screen counts as away: the widget slows down until you are back and then catches up.

### Fixed

- A token index that could not be read for a moment no longer makes a session file count twice.
- Claude no longer asks to sign in again while Claude Code's sign-in is only waiting to be renewed: the tile keeps its last numbers with their age until Claude Code renews it.
- Escape closes an open dropdown list first instead of the whole window.
- Signing out while a web session is still starting no longer leaves a hidden browser running that blocks deleting its sign-in.
- "Reset to defaults" and settings import now include the token usage window's layout; a broken layout in the settings file no longer discards the other settings.
- Moving a section up or down skips hidden sections, and releasing a drag outside every section changes nothing.
- The day grid tile loads off the UI thread, so the widget no longer stalls after each index pass.
- A stalled WebView2 download reports a failure instead of looking cancelled, and the sign-in window explains that plain http pages are never loaded.
- A usage row whose label is cut off shows the full label as a tooltip.
- The table at the bottom of the token usage window shows again when grouped by week, model or project.
- The token usage window no longer crashes on Windows set to a week that starts on Sunday, such as English (United States).
- The numbers in the tray icon now sit exactly in the middle.
- The day grid on the widget tile keeps moving after midnight instead of stopping at the day the app started.
- The About page shows an available update and its Install button even when no check is due that day.
- An update now runs exactly the file whose signature was checked.
- A further Copilot account's GitHub token no longer appears in a process command line.
- A settings file that could not be read for a moment is no longer replaced with defaults.
- Indexing the session logs can no longer close the app, skips folder loops and oversized log lines.
- The threshold mark on a bar moves as soon as the threshold changes, and a row at its limit no longer shows a forecast.
- A language switch updates an open token usage window and the About page.
- A day picked in the token usage window stays selected when the index refreshes.
- Long project names no longer run into the next row.
- The refresh interval moves in 15 second steps and its label shows the exact time.
- An all-users installation updates in place instead of adding a second copy for one user; installs in a custom folder are recognised.
- Codex finds its newest sessions again on machines with very many session files.
- Gemini and Claude reads no longer freeze the widget, and an expired Gemini sign-in shows "not signed in".
- A large Codex jump appears after 15 quiet minutes instead of waiting for the next reset.
- Claude tokens of a line that was still being written are counted once it is complete.
- The app closes at once even while the first statistics pass is running.
- A custom Claude folder (CLAUDE_CONFIG_DIR) is used for the tile and the statistics alike.
- The token usage window opens without a pause, and "12 months" follows calendar months.
- Moving the data folder no longer freezes the app and shows that it is working.
- The statistics and settings windows always open fully on screen, also next to a widget at a screen edge.
- The widget stays in place when moved between screens with different scaling.
- The week starts on Monday everywhere, also on English Windows.
- An invalid value in the settings can no longer close the app when saving.
- A day-long tile chart names the day at its start, so its two times no longer look alike; a chart still loading shows no times.
- A refresh still running while you sign out no longer fills the signed-out tile or its history.
- Signing out or removing an account no longer shows a timeout or slows the next refresh.
- Empty lists or a mislabelled account entry in the settings file no longer stop the app from starting.
- A collapsed section in the token usage window keeps its title and shortens its summary instead.
- While Claude's sign-in waits for renewal, the kept numbers no longer add flat points to the history.
- A data folder placed in a shared folder such as Documents no longer loses other programs' old .tmp files at startup.
- An update download that stops arriving for a minute ends with the usual failure message instead of waiting forever; slow connections still finish.
- A global shortcut now needs Ctrl, Alt or Win (Shift alone only with an F key); Shift with a letter would have swallowed that letter in every program.
- Resizing the settings window at full screen width no longer pushes it off the screen.
- A locked settings backup no longer stops the settings from being saved.
- Autostart turned off in Task Manager shows as off in the settings, and turning it on there works again.
- A failed refresh keeps the running browser session instead of restarting it.
- The About page names the Claude folder that is actually read, also with a custom folder.
- An update no longer turns autostart back on after it was switched off in Task Manager, and no longer brings back a deleted desktop shortcut.
- Signing out during a refresh no longer leaves the loading indicator spinning.
- Gemini no longer adds the same cached reading to the history twice.
- A download that is not newer than the running version says so instead of calling it unverified.

### Security

- Sign-in requests no longer follow redirects and refuse oversized answers.
- The hidden browser session blocks popups, downloads and permission prompts.
- The WebView2 installer check also rejects revoked certificates.
- The log file masks account IDs and user paths in every line.
- Project icons in the statistics are read only from inside the project folder, never from network paths.
- A temporary copy of Antigravity's database stays local and is always deleted.
- CSV exports quote every field correctly and defuse text that a spreadsheet would run as a formula.
- `--new-instance` works only in development builds.
- An update is installed only when the signed file itself carries a newer version than the running app.

## [1.0.0] - 2026-10-03

The first release of AI-Usage: everything below is new.

### Added

**Quotas at a glance**
- One tile per agent: Claude, OpenAI Codex, Cursor, Google Gemini (Antigravity) and GitHub Copilot.
- Every usage window an agent reports (5 hours, week, month, model groups) as a bar with a plain
  reset countdown, the plan tier next to the name and, for Claude and Codex, the tokens of the
  running session.
- More than one account per agent, each with its own tile, history and sign-in.
- A history chart per tile over 24 hours, 7 days, 30 days, a year or everything, kept locally for
  up to ten years.
- An optional "Usage per day" tile with the year grid of the token usage window.

**Alerts and tray**
- Threshold alerts per agent and window, plus a notice when a window is free again.
- A mark when an agent is waiting for you, switchable per agent.
- A tray icon showing the busiest quota as a number, or the agent and window you pick.

**Token usage window**
- A local index of the Claude Code and Codex session logs: total tokens, averages, busiest day and
  the change against the previous period.
- A year grid with one square per day; a click on a day shows its agents, models, projects, hours
  and cache share.
- Breakdowns by day, week, model, project or effort level, usage per weekday and hour, and CSV
  export.
- A response that Claude Code logs several times is counted once.

**Window and settings**
- Tiles below each other or side by side; the window sizes itself and switches between full,
  compact and small layouts.
- Always on top, a click-through overlay mode, adjustable opacity, snapping to screen edges and a
  global shortcut to bring the window back.
- Themes: System (follows Windows live), Nebula, Terminal, Dark, Light and high contrast.
- English and German, switchable without a restart.
- Autostart, settings export and import with an automatic backup, and a movable data folder.
- An About page listing exactly where each agent's numbers are read from, with a "copy all
  details" button for bug reports.

**Install and update**
- An installer for x64 and ARM64, and a portable single-file exe for x64.
- An optional daily update check; updates install only after their signature has been verified.
- The WebView2 runtime is offered for installation when a browser sign-in needs it.

### Security
- Reads usage numbers only: a test guards that the app never calls a chat or completion endpoint
  and so never spends any of your quota.
- Never writes to another tool's files and never stores the sign-in of your CLI tools.
- The WebView2 installer runs only after Windows confirms a valid Microsoft signature; the download
  talks to Microsoft hosts only.
- Downloaded updates are checked against a signature made with the maintainer's key; a file that
  fails the check is deleted and never run.
- The release link of the update check must be an https page on github.com.

# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.2.0] - 2026-10-16

Backups, history from a second PC, real Windows notifications and a much deeper token usage window, a far smaller download, and a long list of fixes from a full review of the app.

### Added

- The waiting mark also works for Gemini: the tile marks it when your latest Antigravity conversation ends with a finished answer you have not replied to yet.
- The main Claude account can sign in through claude.ai in the app; Claude Code's own sign-in still comes first while it has numbers.
- One backup file holds your settings, the usage history and the token index. Restoring it replaces everything and restarts the app; your previous state is kept aside until the restore has finished.
- The token history of a second PC can be imported from its backup. Importing the same backup twice counts nothing twice, and each imported PC can be removed again in the settings.
- Notifications appear as real Windows notifications with a "Show widget" button, grouped in the notification center and respecting Do Not Disturb.
- A warning when the forecast says a window will fill soon, with a lead you choose from 3 minutes to 2 hours (30 minutes by default), and a notification when a limit is reached. Each has its own switch, and both are on.
- Notifications can stay quiet on the weekdays you pick.
- A screen reader announces when a window turns yellow, red or full.
- A short tick on each bar shows how much of the window's time has passed, so you can see at a glance whether you are using it faster than it refills. It appears for every provider and can be switched off in Settings > Display.
- Each window shows the tokens used since its own last reset; a model window such as Fable counts only that model.
- A bar can be colored by model or by main agent and subagents instead of by level, split by each one's share of the window (Settings > Display > Tiles, per provider).
- The token usage window opens almost at once, also right after the app starts.
- An error tile names what went wrong, keeps the last numbers dimmed in view and links to the provider's status page on server errors and timeouts.
- Clicking a provider's name shows its details; "Refresh" and "Hide chart" sit in the tile's right-click menu.
- Tiles can be sorted by how full they are (Settings > Display, off by default).
- Each account can have its own name, such as "Work", shown on the tile, in the tooltip and in notifications.
- Window level "On the desktop": the widget sits behind all windows and survives Win+D.
- The widget hides while a game, video or presentation runs in full screen and comes back afterwards (on by default).
- Widget size 90, 100, 125 or 150 %, on top of the Windows scaling.
- The "System" theme follows your Windows accent color and, on Windows 11, shows a Mica background.
- Battery saver: on battery the widget fetches half as often, and in energy saver mode it reads only local files (on by default).
- "Pause for 1 hour" in the notification area menu stops all network fetches for an hour.
- Keyboard shortcuts Ctrl+T for token usage and Ctrl+L for the layout; the title bar tooltips name them.
- "Help and FAQ" on the About page and "Help" in the menu of a tile with an error.
- The widget keeps `status.json` in its data folder up to date, and `AI-Usage.exe --status` prints the current values for a terminal or a status line.
- Token usage: new groupings by weekday, by hour, a weekday by hour grid and a list of your largest sessions.
- Token usage: color the columns by provider, by model or by main agent and subagents, with a 7 day average line over the days.
- Token usage: the share of Claude tokens spent by subagents, a provider filter and a click on a model or project that filters every section.
- Token usage: a "Limits" section shows how often you hit each provider's limit, with the highest value and the average peak.
- Token usage: an estimate of how many tokens one percent of a window holds for each model, based on your own usage.
- Token usage: Gemini usage from the Antigravity CLI is counted too.
- Token usage: periods "This month", "Last month" and a custom range with a calendar.
- A portable exe for ARM64, `AI-Usage-arm64.exe`, joins the downloads.
- Settings > Refresh: each account can read at most every 5, 10, 15, 30 or 60 minutes, or follow the default. The slider under Refresh now applies to every provider read over the internet; a browser session is never read more often than every 5 minutes.
- Picking a data folder on a network drive, a removable drive or in a cloud sync folder asks first, since the token index can be damaged there. When the chosen folder is not available at start, the data folder row says so.

### Changed

- The token usage window appears complete instead of as an empty frame first, draws the visible part first and opens again at once after being closed.
- The notification pickers show plain values such as "30 min" and "2 h".
- The forecast warning allows for the time until the next read, so a short lead is never skipped between two reads.
- Long provider names and window labels show their full text as a tooltip, but only when they are cut off.
- A model keeps the same color on every bar of a tile.
- Context menus have no separator lines and are ordered by what belongs together; the notification area menu is shorter, with the window options in a "Widget" submenu.
- Focus rings appear only when you move with the keyboard, not after a mouse click.
- Tooltips that only repeated the visible name are gone.
- The pie charts in token usage grow with the window.
- In Settings the category list stays in place while the page scrolls, and "Read at most every" moved to the Refresh page.
- AI-Usage no longer carries its own copy of .NET, so the downloads shrink from about 136 MB to a fraction of that. It uses the .NET 10 Desktop Runtime installed on your PC: the installer downloads it from Microsoft when it is missing, and a portable copy shows a download link instead of starting. A portable 1.1.0 that updates itself therefore starts only once the runtime is installed. See "Coming from version 1.1.0" in the README.
- Token usage shows one chart instead of a breakdown and a per day chart; the cache share appears once, in the breakdown.
- Token usage remembers its period, grouping and position, and opens at 80 % of the screen the first time, never larger than the screen.
- Unknown model names are shown in a readable form ("GPT-5.1 Codex Max"), and the project list shows folder names with the full path in the tooltip.
- The welcome window lists all five providers, marks the ones already found and explains what Gemini and Copilot need.
- The refresh icon left the title bar; refresh is in the title bar's right-click menu, and F5 still works.
- Autostart waits 10 seconds after you sign in to Windows.
- Plan and email are fetched now and then instead of on every fetch, so the providers see fewer requests.
- The installer texts address you informally, like the app.
- In the small layout the usage per day tile shows as many recent days as fit across its full width, not just the last seven.
- The button that closes the welcome window now reads "Done".
- The "Show tooltips" switch is gone from the settings; tooltips stay on, and `settings.json` can still turn them off.
- The README explains that project colors come from app icons found in your project folders.
- A provider that is signed out or cannot be reached keeps showing its last known 5 hour and weekly numbers, also after a restart. Only signing out in the app clears them.
- Token usage no longer repeats the weekday columns as a table underneath.
- The Mica background is now off by default, with a tooltip in Settings that explains it; a choice you already made stays.
- Token usage no longer ends in a table at the bottom: the session list sits under the session chart, and the "Other projects" bar names the projects it pools when you point at it.
- Token usage opens with its numbers already in place: the app keeps them ready after each scan of your session logs.
- Token usage charts say "Loading data..." while the numbers are on their way, instead of claiming that nothing was counted.
- Token usage: pointing at a slice of the provider, model or effort ring shows its name, tokens and share; the "Other" model slice lists the models it holds.
- Token usage: the provider, model and effort rings keep their size when the cards beside them grow.
- Token usage: importing or removing a PC refreshes an open window at once.
- Token usage: resetting or importing settings while the window is open updates its period, grouping, colors, section arrangement and collapsed sections at once.
- Token usage: a model has the same color in the model ring, the stacked columns and the weekday by hour grid.
- Token usage: the weekday by hour grid is colored by the "Color by" choice; each cell takes the color of what leads it and its hover text names that leader.
- Ctrl+Alt+Arrow keys snap the widget only while it is the active window; they no longer take these keys from other programs.
- Too many requests (HTTP 429) reads as such and no longer offers a sign-in; a moved provider address no longer offers one either.
- While an update downloads, the widget says so beside the install button. A release without a file for your copy and a release that is not newer get their own messages.
- In a narrow widget the reset clock time gives way before a window name is shortened.
- Removing the app with "also remove data" now also removes the browser sign-ins of the providers.

### Fixed

- When Claude reports a reached limit, the tile keeps every bar (5 hours, 7 days and the rest); only the full window shows 100 % with its reset. The single "Limit reached" bar appears only when no usage source answers.
- The welcome window scrolls when it is taller than the screen at high display scaling, so its button stays reachable.
- Token statistics no longer skip a session for good or count it twice when a session file was locked for a moment while it was read; a file that stays unreadable is skipped after five tries.
- Statistics and the Gemini waiting mark read the same three Antigravity folders.
- Starting the app a second time just as the first copy closes no longer leaves no copy running.
- The history chart keeps every point and its time order after the clock was set back.
- Importing settings from another PC keeps this PC's extra accounts; a tray choice naming a tile that no longer exists falls back to Auto.
- The settings and token usage windows read the other-PC list and the quota history in the background, and a missing data drive shows a message instead of an error window.
- Uninstalling with "remove data" also cleans a data folder moved in the settings, removing only the app's own files there and leaving anything else in that folder alone.
- The status file is written safely to disk, so a power cut cannot leave it empty.
- Zoom levels read "125%" in English and "125 %" in German.
- High contrast: the cache share bar uses system colors.
- The sign-in window closes by itself once you are signed in, also on sites such as claude.ai that switch to the app without reloading the page.
- A brief server error (such as HTTP 503) is retried once quietly before the tile shows it; the last numbers stay in view meanwhile.
- Signing out of an account no longer leaves its old numbers on the tile.
- The Claude sign-in button no longer does nothing when Claude Code holds no sign-in.
- The token usage window no longer stalls when it opens: it is prepared again after each close and no longer rebuilds its charts a second time on open.
- A refresh during the short pause before a timed-out read is retried now starts the retry at once instead of doing nothing.
- A token figure no longer blanks for a moment when a reset time shifts by a second around the full minute.
- Closing the app no longer waits on saving the prepared statistics.
- Chart tooltips use the theme's font.
- A Gemini read that timed out briefly showed an error although the next read worked; it is now retried once quietly first.
- The widget's taskbar button no longer lights up as if it had a message.
- The threshold line stays hidden on a tile whose notifications are off.
- The minimize button no longer stays highlighted after restoring the window.
- Claude output tokens in the token usage window were about a third too low: Claude Code writes each response several times and only the first copy, with a provisional output count, was counted. The final count is used now, and a one-time pass adds the missing tokens from every session file that still exists.
- A session file is no longer counted twice when the Claude or Codex folder is spelled with different letter case.
- After an in-app update the installed widget starts again on its own.
- With an auto-hiding taskbar a maximized window no longer counts as full screen, so the widget stays visible.
- A weekly-only Codex limit is shown as the weekly window instead of "5 h".
- A lost network connection no longer signs Gemini out on the tile.
- Claude reads the organization with chat access when the account belongs to several.
- Pause and energy saver also hold back the web reads of Claude, Codex and Gemini, while their local files are still read.
- "Limit reached" appears once per period, also when the first reading had no reset time.
- An update check cancelled by closing the window no longer counts as done for the day.
- Reset and import apply thresholds and the tile order at once; the notification area icon no longer follows a removed account.
- Lowering the history retention keeps the longest chart range it still covers.
- A tile that has been silent for a day no longer shows a forecast.
- The weekly token count on a tile is right straight after start and after an import.
- The token usage window no longer breaks on a custom range far in the past, keeps the "No project" filter across a language switch and colors every day's projects.
- Moving the data folder into the temp folder is refused, since Windows empties it.
- An unknown Copilot quota shows a readable name, Antigravity calls with extra fields are counted, `--status` skips a damaged entry instead of crashing, and autostart is repaired after a move also when the setup turned it on.
- Claude and Gemini no longer time out on networks that announce IPv6 without a working IPv6 route: connections try IPv4 first.
- Drop-down lists, menus and tooltips are fully opaque again with the Mica background.
- Settings changed just before closing the app are no longer lost, and saving them no longer stutters while you drag a slider or the window.
- The app starts next to a copy running as administrator and with a broken data folder pointer, and closes without an error message.
- Claude no longer shows "waiting for you" right after you send a prompt, and a reached limit stays visible while a log file rotates.
- Codex shows a real limit as 100 % at once, and a stray "429" in an error message no longer counts as one.
- Implausible reset times are dropped for every provider, and Cursor no longer shows 0 % without a known limit.
- Gemini keeps reading after an Antigravity CLI update without a restart.
- Cancelling a sign-in no longer shows an error, and a signed out Gemini web session offers "Sign in".
- Token usage lists a model with and without a date in its name only once, and a half written Codex log line no longer counts everything twice.
- The token usage window no longer clips text in its smallest size, and its charts use the "Terminal" theme's font.
- Dates use the same month abbreviations everywhere.
- The 1.1.0 weekday or hour chart choice carries over into the new groupings.
- A hidden browser that crashed or was ended in Task Manager is replaced at the next read instead of leaving every web tile without numbers until a restart.
- A failed web read keeps the last numbers while they are fresh and no longer blocks the next attempt.
- No hidden browser starts for a provider nobody signed into, and Pause also holds Cursor, Copilot and the Gemini sign-in check.
- The sign-in window no longer closes when a link on the login page (privacy, pricing) is clicked.
- A brief provider error no longer reads as signed out, and a provider that changes one of its addresses is still found.
- Web tiles refresh as soon as the network is back after sleep, also with a VPN or virtual network adapter.
- Hotkeys, hiding during full screen and click-through work right after a start into the tray.
- Changing monitors or the Windows color mode can no longer crash the widget, and the widget returns to the right monitor.
- Token usage at its narrowest width shows every legend entry, the weekday labels of the year grid and full session times.
- "This month" compares with the same number of days of the previous month.
- Project colors stay the same when the period changes, model colors are distinct, and one broken project icon no longer removes the colors of all projects.
- Chart tooltips no longer stay on screen after switching to another program.
- A failed write to the token index is logged and retried without reading the same files again; one damaged line no longer stops the rest.
- Data files are flushed to disk before they replace the old copy, and leftover backup and corrupt copies no longer pile up.
- History export and removing an imported PC no longer freeze the settings window.
- Keyboard focus is visible on every button, check box and radio button, and screen readers name both quiet-hours boxes.
- Setup no longer hangs forever on a stalled .NET download.
- Closing after a crash or restarting to restore a backup no longer leaves a dead tray icon behind, and the window position is saved.
- A second instance keeps its own data folder setting and stays a second instance through an update.
- A Claude folder that is linked to another one is no longer counted twice.
- Signing out clears that account's numbers from the status file and stops forecast alerts for it.
- A changed time zone applies at once instead of after a restart.
- A limit that has reset no longer shows last period's token count.
- Setting the clock back no longer pauses the refresh.
- A settings file briefly locked by a sync tool or virus scanner no longer starts the app on default settings.
- A Claude response that Claude Code copies into a resumed session is counted once; tokens already counted twice are taken off once from the session files that still exist (the token index is backed up first).
- The Claude limit of a long-running session is found again in very large project folders.
- Moving the data folder refuses a folder that already holds AI-Usage data, so it can never overwrite another copy's data; moving back to the default folder asks before it replaces the older copy there.
- Token usage also counts logs of older Codex versions, which carry no cache write total; such logs read so far without a single counted line are read once more.
- Codex reads its sessions from `CODEX_HOME` when that is set.
- Started into the notification area, the widget fetches at the slower hidden-window pace, and the first time it is shown it fetches right away.
- A quick double click on "Add account" for Copilot no longer adds the same GitHub account twice.
- Dragging the top edge of the widget stops at the content and the screen height like the bottom edge, instead of sliding the window off the screen.
- A settings file with an empty entry in the hidden windows no longer stops the app from starting, and a backup with a damaged token index is refused instead of closing the app.
- The widget no longer hides itself as if a full-screen app were running when no window has the focus.
- The update notice, the chart legend of a tile and the percentages in the top projects follow a language and number format change at once.
- Hovering or clicking the value labels of a token usage chart no longer picks the first column.
- A custom token usage range longer than a year is drawn per week, like the whole history.
- The history export names each window, so several Cursor bars can be told apart.
- An account name cut to its maximum length no longer ends in half an emoji.
- Uninstalling removes the desktop shortcut of a per-machine install.
- The portable setup no longer writes a setup log to the PC.

### Security

- The sign-in window no longer saves form entries or passwords and grants no site permissions that a later hidden read would inherit.
- The prepared statistics file is kept in the machine-local cache folder, so the list of your sessions and projects no longer roams with the profile.
- Project colors (keyed by your local project folders) also move to the machine-local cache and no longer roam or go into backups.
- The log no longer records the text of an invalid account entry from a hand-edited settings file.
- Antigravity conversation databases are opened so that nothing declared inside the file can run.
- Every update downloads into its own new folder and is checked from the same bytes it installs. A portable copy accepts only a portable file of its own architecture, an installed copy only a setup, and an older or equal version is refused.
- An installation for all users starts the verified setup with administrator rights directly, and the previous program file is kept until the new one has started.
- Dialogs on a provider's page can no longer block the hidden browser, and the sign-in window shows the address of any page that is not the provider's own.
- The key file created for signing updates is readable only by your user and the system.
- A restored backup is cleaned of anything the app did not create itself before its token index is used.
- The address found for reading a provider is stored without account ids.
- The WebView2 installer runs from its own new folder, its signer must chain to a pinned Microsoft root, and the sign-in window allows no downloads.
- The build refuses a release whose signature does not match the key built into the app.

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

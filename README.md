# Moronco — Nocturne

A Windows browser with an English interface, a dark gothic theme and a fixed user-provided wallpaper. The anime selector and image title have been removed. See ui/assets/CREDITS.md for image provenance.

## Run

Open `release/Moronco.exe`. The interface, assets, WebView2 SDK files and uBlock Origin Lite are embedded. Windows x64, .NET Framework 4.8 and the Microsoft WebView2 Evergreen Runtime are required; the executable does not include the Chromium engine.

## Search

Brave is the default search engine. Select Brave or SearXNG on the new tab page, or use **Search both** to open two tabs with the same query. Automatic Brave AI answers are disabled using the provider cookie and URL preference. There is no automatic Google fallback.

**Deep Search** collects original titles, links and descriptions from Brave and SearXNG result cards. Broad mode preserves your query; Exact mode quotes the whole phrase. Optional filters cover domain, excluded phrase and PDF/TXT/DOCX. Filter support depends on each source. SearXNG requests Bing, DuckDuckGo, Mwmbl and Wiby.

Use **Load next page** to collect up to five pages per source, at most 30 result cards per page. Source tabs are reused if they still show the previous requested page; tabs you navigated elsewhere are preserved. Close tabs if the 50-tab limit is reached. Results accumulate, remain available when one source fails, and are deduplicated by URL without its fragment. Paths and query parameters are preserved. Rankings are interleaved, with no AI-generated relevance score or rewritten summary.

Results remain in memory for the current session. Source tabs and history follow the normal session/history settings. **Stop collecting** stops collection without closing source tabs. Public servers may require verification or rate-limit requests. Open a source tab to inspect failures; no CAPTCHA bypass is attempted. DOM readers may need updates when provider layouts change. Search cannot guarantee complete indexing or discovery of every page.

Change the SearXNG server in **Settings → Browser**. HTTPS and HTTP localhost servers are supported. The configured server receives your queries. Native tests on 2026-10-18 collected 20 Brave and 30 SearXNG results (44 unique links); this is evidence of that test, not a service-availability guarantee.

## Tabs and browsing

Up to 50 tabs, pinning, drag reordering, duplication, reopening closed tabs, horizontal or vertical layout. The tab bar supports scroll buttons and the mouse wheel, retains manual scroll position, and reveals the selected tab. The down-arrow button opens the searchable command/tab list. Window controls and the new-tab button remain outside the scrolling strip.

Bookmarks, history, local notes, downloads, zoom, find-in-page, screenshots, printing, mute and developer tools are available. Picture-in-picture, calls and media compatibility depend on the website and runtime. Downloads ask where to save and require confirmation before opening. Automatic popups are blocked; user-initiated windows become tabs.

## Protection

- uBlock Origin Lite 2026.1006.1931 is embedded, enabled by default, and includes the Vietnamese filter list. The user's existing enabled/disabled preference is preserved. Reload pages after toggling. It does not block every ad or tracker.
- WebView2 reputation checking is explicitly requested, subject to Windows SmartScreen policy. Balanced tracking prevention is explicitly applied. Neither is a guarantee against malicious sites.
- Invalid HTTPS certificates are refused, with no app bypass control. Plain HTTP remains supported and is not encrypted.
- Password autosave and general autofill are disabled. Existing stored data is not deleted automatically.
- Website permission prompts require HTTPS, a user-initiated request, and the active unchanged tab. Previously saved website permissions are reset when a browsing session starts; new decisions are not saved permanently. Requests without these conditions are denied. External content has WebView host messaging and host-object access disabled; only the trusted interface handles app commands.
- Cloudflare WARP controls the system connection, including other apps. Install/register WARP using its official app and keep its service running. Moronco does not auto-connect or store VPN credentials. DNS-only mode is not reported as a VPN tunnel. WARP binaries are not redistributed.

The app does not promise anonymity. Search providers still receive queries, and session/history data is stored locally. Keep WebView2 and Windows updated. uBOL is pinned and does not auto-update in this package; see DEPENDENCIES.md for its source, license and hashes.

## Settings and data

Appearance controls include accent color, wallpaper opacity/blur, layout, compact mode and custom CSS. CSS only affects the Moronco interface. Export a theme before major edits. Use **Ctrl+Alt+R** or `--reset-ui` to recover the default interface without removing bookmarks or notes.

Data remains at `%LOCALAPPDATA%\MiniumBrowser` for compatibility with older versions. `browser-state.json` stores settings, bookmarks, history, notes and session tabs; `.bak` holds the previous save. Corrupt data is preserved for recovery. Website cookies live in `profile`. `--data-dir=<directory>` selects an isolated profile. Only one window per profile is supported. User-authored notes, bookmarks and website content are not translated or overwritten.

## Shortcuts

Ctrl+L address; Ctrl+T new tab; Ctrl+W close; Ctrl+Shift+T reopen; Ctrl+Tab / Ctrl+Shift+Tab switch; Ctrl+D bookmark; Ctrl+H history; Ctrl+J downloads; Ctrl+F find; Ctrl+K commands; Ctrl+R reload; Alt+Left/Right back/forward; F11 fullscreen; Ctrl+Alt+R reset appearance.

## Build and verification

```powershell
.\build.ps1
.\test-anime.ps1 -SkipBuild
.\test-browsing.ps1 -SkipBuild
```

Native diagnostics use separate profiles:

```powershell
.\release\Moronco.exe --smoke=upgrade --seconds=35 --data-dir=D:\test-moronco-tabs
.\release\Moronco.exe --smoke=deep --seconds=115 --data-dir=D:\test-moronco-search
```

The upgrade probe checks 50-tab overflow/selection/scroll retention at 900×600 and WebView protection settings. The deep probe checks extraction, unsafe links, document/query/page guards, deduplication and two-page accumulation. `DEEP_PASS` validates the logic, while `DEEP_LIVE` and `DEEP_PAGE_TWO` record actual provider availability. Test logs and screenshots are saved under each profile's logs folder.

No account sync, private browsing mode, full password manager or extension store is included. Dependency versions and reuse decisions are recorded in DEPENDENCIES.md.


## Privacy defaults (2026-10-20)

Browsing history and session tab restoration are enabled by default. On the first launch of this update, both are re-enabled for profiles from the previous privacy-default build, as requested. Later changes in Settings remain respected. **Settings → Browser** provides **Save browsing history**, **Restore previous session**, and **Clear website data on exit**. These choices are persisted. Website cleanup on normal exit is enabled by default and signs you out: cookies, site storage, disk cache, engine browsing history and engine download history are cleared. Downloaded files, bookmarks and notes are retained. If cleanup fails, the app stays open and reports the failure so you can retry. Cleanup cannot be guaranteed after a crash, forced termination or power loss; this is not a private/incognito browser profile or forensic secure erasure.

Previously saved Moronco history, backups and diagnostic files are not automatically erased. Use History to clear the app history list and Clear browsing data for website data; filesystem backups can still retain older records. Password autofill/saving remains disabled, but previously saved passwords are not automatically deleted. Normal app diagnostic logging is now disabled; explicitly requested smoke tests still write logs to their test profiles.

These controls minimize local retention, not all collection on the internet. Websites receive requests and can recognize signed-in accounts; search providers and the configured SearXNG server receive queries. WARP does not prevent website tracking or account identification. WebView2 reputation/update services and Windows policies remain outside a guarantee of zero vendor data collection. The app does not promise anonymity or zero network metadata.

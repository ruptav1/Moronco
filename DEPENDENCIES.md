# Reuse decision — 2026-10-09

Need: an experimental Windows browser with a user-requested 150 MB budget, allowing workloads to be refused. Verdict: ADAPT Microsoft WebView2, using the existing Windows .NET Framework compiler for the first measurement build. This is not evidence that Chromium can run YouTube/Discord within 150 MB.

Current scope update (2026-10-11): the user prioritizes a customizable anime UI and everyday browser features. The browser uses an uncapped process group for cleanup and no longer applies the old memory refusal policy. SDK selection and dependency versions are unchanged.

## Anime UI reuse decision — 2026-10-11

ADAPT the existing Microsoft WebView2 SDK and .NET Framework APIs. The official [Microsoft WebView2Browser sample](https://github.com/MicrosoftEdge/WebView2Browser) was reviewed as an architectural reference for separating browser chrome from website views; its code was not installed or executed. This implementation uses local HTML/CSS/JavaScript for chrome, native WebView2 controllers for website tabs, and Framework JSON/file APIs for persistence. No npm package, Electron runtime, UI library, external font or additional third-party executable was introduced.

The user selected Gosick, The Mystic Archives of Dantalian and Rozen Maiden (2013), with existing online art and no AI generation. Image provenance, author/owner information and hashes are in [ui/assets/CREDITS.md](ui/assets/CREDITS.md) and [ui/assets/sources.json](ui/assets/sources.json). The artwork is not claimed to be openly licensed. Assets and attribution are embedded for local use; no runtime request to image hosts is needed.

## Selected dependency

- Official SDK: [Microsoft.Web.WebView2 1.0.4258.31](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4258.31), published 2026-09-28.
- Package SHA256: `56F7F4B8BF9AEE4B8EFEFBBDD4F67D5F74EBD1B100ED0806DA71BF76AF481AA9`.
- Source: `https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/1.0.4258.31/microsoft.web.webview2.1.0.4258.31.nupkg`.
- SDK license: Microsoft three-clause BSD-style redistribution terms, preserved verbatim in the distribution. The independently installed Edge WebView2 Runtime has its own Microsoft terms.
- Inspected package manifest, file inventory and license. No package dependency is declared for the native group. We use only `Microsoft.Web.WebView2.Core.dll` and the x64 `WebView2Loader.dll`; build targets and bundled code-generation executables are not run.
- Authenticode signatures are checked before build. A valid signature and static inspection are not a vulnerability audit or a proof of safety. Full SDK source and runtime binaries have not been audited.
- The reuse-first static scanner flagged two high-severity matches in the duplicate API XML documentation. Both are the `CoreWebView2CookieSameSiteKind` documentation linking to MDN's cookie reference, not executable cookie-store access. Reviewed that exact context. Other warnings describe compiled binaries and XML build comments; those build files/code generators are not executed. The Core DLL and loader both have valid Microsoft Authenticode signatures.
- Evergreen Runtime already installed on this machine; the build does not install or downgrade it. The runtime remains independently serviced.

## Alternatives

- [CEF](https://github.com/chromiumembedded/cef): BSD-licensed Chromium embedding framework. Rejected for this prototype because we have no measured memory advantage and would need to package and service another engine.
- A new browser engine: does not offer a credible short path to YouTube/Discord compatibility. Not selected.

## Reproducible build

`powershell -NoProfile -File .\build.ps1` uses the already downloaded, hash-pinned SDK. For a clean checkout, explicitly pass `-Restore` after reviewing this document. Downloads stay under `.deps/quarantine`; no global install is performed.

References: [WebView2 performance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/performance), [process model](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/process-model).

## Single-file packaging

Reuse decision: use .NET Framework's existing compiler manifest resources, SHA256, assembly loading and filesystem APIs. No additional packaging library, installer or package was added. Payloads are the already vetted Core DLL, native x64 loader, SDK license and NOTICE, plus the local UI and its credited images. No user profile, cookies, logs or browser engine binary is embedded. The bootstrap extracts hash-verified files into a per-user, per-build cache, validates asset paths, and calls the application inside the same process. The SDK's documented `SetLoaderDllFolderPath` API selects the extracted native loader.

## uBlock Origin Lite — approved 2026-10-17

Decision: ADAPT the existing official Edge build, version 2026.1006.1931, as an independent browser extension using WebView2 AddBrowserExtensionAsync. Alternative libadblockplus adds a C++ engine/bridge; a handwritten domain list would have much narrower coverage. No installer or upstream build scripts were executed.

Upstream: https://github.com/uBlockOrigin/uBOL-home/releases/tag/2026.1006.1931
ZIP: https://github.com/uBlockOrigin/uBOL-home/releases/download/2026.1006.1931/uBOLite_2026.1006.1931.edge.zip
Upstream SHA256: dd059665fd1d3195f39ade95d30029b7b2582283365b137f83b2fa18cf9250ee
Project ZIP: assets/ublock-lite-2026.1006.1931.zip
Project SHA256: 938cba1ee5d266350011cfa93665b183a0fb8de3e983b241afaf99aec6a7ea35
License: GPL-3.0-or-later; LICENSE.txt and individual copyright notices are retained inside the package and extracted extension. Complete readable JavaScript source is included. The manifest has one local change: enable the existing `vie-1` declarative rule set. Chromium-generated `_metadata` caches are excluded. All executable source files match the official ZIP byte-for-byte; no additional source files exist. Extension requests broad host access for filtering; no native messaging permission. No automatic updater is added. Version is pinned, not asserted to be the newest release.

Review evidence: .deps/quarantine/ubol-review/scan.txt and comparison.json. Scanner is heuristic, not a vulnerability audit. All 26 HIGH matches were reviewed: network/strictblock JSON entries are blocked domains, not outgoing exfiltration endpoints; cosmetic JSON entries and generic maps are CSS selectors for removing banners; cookie scriptlet arguments describe consent UI; the CodeMirror bidi controls appear in the literal regex used to recognize special characters; shell/base64 strings in ublock-filters are anti-malware matching patterns, not shell execution; ublock-badware contains patterns that intercept webpage code and decode data URLs for filtering. These findings match upstream source and are explained by content-blocker behavior. This does not constitute a guarantee of safety.

## Deep search reuse decision (2026-10-18)

ADAPT existing WebView2 tabs and provider HTML, no new dependency or downloaded executable. Brave Search API was evaluated but requires an activated account/API key (https://api-dashboard.search.brave.com/documentation/quickstart). SearXNG's documented JSON interface is commonly disabled on public instances (https://docs.searxng.org/dev/search_api.html). Instead, the local collector reads only web-result cards from the exact requested endpoint and query; original tabs remain available for verification. Brave selectors were inspected on the actual public HTML (`.snippet[data-type=web]`, `.search-snippet-title`, `.generic-snippet .content`). SearXNG selectors were checked against upstream `searx/templates/simple/macros.html` and `result_templates/default.html` at https://github.com/searxng/searxng (read-only inspection, no upstream code installed or executed). Extractor logic is local code, with tests for unsafe URLs, unrelated documents and deduplication. Provider DOM compatibility and availability are not guaranteed.

Search integration reuses provider web pages (Brave Search and SearXNG), with no new binary, library, API key or third-party installation. Brave's official settings POST (`name=summarizer`, empty `value`) was verified to set `summarizer=0`; Moronco applies that cookie and `summary=0`. SearXNG supports configurable instances and explicit `engines` query parameters; reference: https://docs.searxng.org/dev/search_api.html and https://searx.space/. The default public instance https://searx.tiekoetter.com/ returned a Portico verification page during live WebView testing on 2026-10-18. Routing tests do not imply provider availability or comprehensive indexing.

## Cloudflare WARP

Reuse installed C:/Program Files/Cloudflare/Cloudflare WARP/warp-cli.exe, verified Authenticode signer Cloudflare, Inc. No WARP binaries are redistributed. Fixed commands only: status, settings, mode warp+doh, connect, disconnect. Commands run without a shell and with a timeout. Registration and accepting terms remain in the official WARP app. WARP status in this development session: service daemon unavailable; no live tunnel success is claimed.

## 2026-10-20 protection and search update

USE the pinned WebView2 SDK, existing uBOL and existing provider pages; no additional third-party code or dependencies installed. WebView2 SDK XML was checked for permission reset, certificate refusal, tracking prevention, reputation checking and content bridge settings. Official reference: https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2settings.isreputationcheckingrequired and https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/winrt/microsoft_web_webview2_core/corewebview2trackingpreventionlevel. Reputation checks depend on Windows SmartScreen policy; Balanced is used for site compatibility. Persisted permissions are reset at session startup and new decisions are not saved permanently. Existing saved passwords are not deleted, but password saving/autofill are disabled. The Brave page-next URL (`offset=1`) was verified in live HTML; SearXNG `pageno` follows its documented search API. Pagination is user-triggered and capped at five pages per source.

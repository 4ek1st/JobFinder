# JobFinder 0.5.2 — 2026-10-12

- Mehr Platz für Stellen: Hinweise zur Abdeckung und Quellenprobleme stehen jetzt unter „Einstellungen → Quellenstatus“.
- Ein gelber Punkt am Zahnrad weist auf ein Problem hin. Nach erfolgreichem erneutem Laden verschwindet er.
- Erneutes Laden und Abbrechen erhalten bereits gefundene Stellen. Deaktivierte Quellen lösen keinen Hinweis aus.
- Deutsch ist die Standardsprache für neue Installationen. Eine bereits gespeicherte Sprachauswahl bleibt erhalten.
- Neue GitHub-Seite auf Deutsch mit Banner, Screenshots, Download-Button und Kurzanleitung.
# JobFinder 0.5.1 — 2026-10-12

- Fixed the production GitHub CDN path `/github-production-release-asset/`, discovered during the anonymous public update proof. The older CDN format remains supported; HTTPS host, signed metadata, length and hashes remain enforced.
- Corrected draft release read-back for GitHub temporary `untagged` URLs, with final canonical URL checks after publication and safe resume limited to unpublished drafts.
- Supersedes 0.5.0. Use this version for the first installation with internet updates; 0.5.0's restrictive client can reject current CDN redirects.

# JobFinder 0.5.0 — 2026-10-12

- Public source and portable releases at github.com/4ek1st/JobFinder. No GitHub login is needed to check or download updates.
- Background stable-release checks at startup and every 15 minutes. A download action appears in the window caption immediately before Minimize only when a newer release is available. Its tooltip states that installation restarts the program.
- Download progress, cancellation and retry; manual checks and localized errors in About. Signature, package SHA-256, size, repository URL and actual executable version are checked before installation.
- A separate helper applies updates after normal shutdown, keeps program/data backups and confirms the new main window started. Failed installation/startup restores the previous version and reopens it.
- Update restart preserves the applied search and separate draft, favorites, saved searches, source choices, theme/language, selected posting, scroll positions and window bounds.
- Controlled client/installer tests, real WPF UI checks and an isolated real GitHub update proof are documented in UPDATE-QA.md. Physical input and monitor DPI changes are outside the automated evidence.

# JobFinder 0.4.0 — 2026-10-12

- Expanded to 33 portals, including 21 real common-feed adapters and 12 external searches. Added training, student/entry, public-service and general work sources with actual public reply, search, paging and detail verification.
- Added a searchable source directory with category/mode filters, alphabetic source order, persistent checkboxes, visible-only bulk selection and an independent scroll area. Main-screen failures are summarized rather than stacking a warning per provider.
- New source IDs migrate once while retaining old disabled choices and all personal state. Added sources disabled later stay disabled on subsequent launches.
- Search requests are bounded to six concurrent providers, with individual intervals, cancellation, provider cooldown and validated read-only POST/cache behavior. Published robots restrictions and source access failures are respected.
- Preserved exact vacancy categories, unknown fields, real logos, structured descriptions and scoped contacts. Empty logo values do not resolve to HTML posting URLs; short acronym searches avoid unrelated German substrings. Provider-specific geography/radius/page limits have visible explanations.
- Per-provider verification reports describe evidence and runtime limits. Existing compact search, themes, favorite persistence, details and automatic pagination remain covered by regression checks.

# JobFinder 0.3.1 — 2026-10-08

- The large search form collapses after starting a new search. A compact line retains the applied query, Edit search and an available Stop action while loading. Ctrl+F opens and focuses the editor; explicit collapse retains the draft and returns focus to Edit search.
- Pending query or source changes have a distinct marker. The compact line does not show unapplied fields as the active search. Duplicate query and draft status text is removed from the compact view.
- Opening and collapsing the form preserves selection, both scroll positions and the loaded description. Background pagination and source retries leave a manually opened form open. Invalid source selection keeps the form available for correction.
- On the captured 1536×1024 WPF screen, content gains 153 logical pixels in height. Long searches truncate without losing the edit action in a short viewport at simulated 200% density.
- 87 WPF checks and 39 core regression checks pass in this update. The earlier 51 source checks, including live full descriptions, remain documented for the unchanged source adapters. No physical input or monitor DPI change is claimed.

# JobFinder 0.3.0 — 2026-10-08

- Selected postings open in a right detail panel with full source descriptions, paragraphs, lists, published application contacts and fixed Save/Original actions. Narrow windows provide Back and retain the result scroll position; Esc closes details without stopping pagination.
- Card actions appear on hover, keyboard focus or touch, with reserved space. Company images from source cards/postings have bounded asynchronous loading and a local cache, with initials as fallback.
- Detail requests are scoped to the selected posting, cached and protected against late responses. Favorite descriptions persist for offline reading. Missing or inaccessible details and contacts have explicit states.
- Fixed a latched automatic-loading pause after reverting search fields or source selection. Twelve pages without new results now stop only that source with a reason and its continuation action; other sources continue. Existing user stop, retry cooldown and repeated-cursor handling remain explicit.
- 71 WPF checks and 51 source/regression checks, including live full descriptions from all three sources, pass. Physical input, monitor DPI switching and Windows Snap are outside automated evidence.

# JobFinder 0.2.0 — 2026-10-08

- Automatic pagination near the end of the virtualized feed; no load-more button. Startup refreshes the last search. Empty pages and short viewports continue automatically. Twelve batches without new matches pause explicitly.
- Cancellation, manual resume after a pause, cooldown-aware retry, visited-cursor loop detection, and source failures retain fetched cards. Edited queries and favorite views suspend automatic loading.
- Persisted dark/light palette, dark by default for older profiles. Application templates cover dropdowns, menu items and separators, editing menus, scrollbars/thumbs, checkboxes, fields, tooltips, focus indicators, progress, and main/dialog window chrome.
- The visible card and its pixel position remain stable across automatic append and theme changes. Compact layout also handles a short viewport at simulated 200% density.
- Public source access and external-only StepStone/Indeed/BA behavior remain as documented. No new credentials or access bypasses.
- Reproducible WPF and source verification is included. Mouse input, physical monitor DPI changes and Windows Snap remain outside automated evidence.

# JobFinder 0.5.1 update verification

The release adds a public GitHub update feed, a caption download action and a transactional installer. Existing job sources and personal data remain covered by regression checks.

## Controlled verification

- Core: 391 passing checks (188 source/persistence regressions, 128 update-client checks, 75 installer checks).
- WPF: 153 passing checks, including 54 update-specific checks. Actual controls and bound commands cover themes, RU/DE, progress/cancel/retry, About checks, startup/check overlap, save failure, owned-stage cleanup, restart snapshots and rollback messaging.
- Real NTFS junctions, locked files, competing locks and disposable native processes are included. Most installer outcomes use an injected process host to make failures repeatable.
- DE-to-RU toolbar clipping was reproduced and corrected without changing ordinary button sizes/styles.
- RED-to-GREEN evidence is retained locally. Independent source review found no remaining confirmed P1/P2 issue after corrections.

Portable reports and selected WPF renders are in `verification`. Offers/progress in controlled renders use an injected update service and do not claim a live release was offered during rendering. Cards use captured public replies. Physical input, monitor DPI switching, multi-monitor movement and Windows Snap are outside this automated evidence.

## Real public update proof

The first usable stable update release is v0.5.1; the initial v0.5.0 internet proof exposed a current CDN path mismatch before user installation. A real 0.4.1 bootstrap was built solely for isolated verification. After publication, `--verify-github-update` in an empty isolated installation exercises the production anonymous client, actual caption command, hidden helper, replacement EXE and new visible WPF window acknowledgement. The proof compares favorites, saved searches, applied query, separate draft, source choices, language/theme and window bounds. It never reads the user's profile.

At this initial source snapshot the remote proof is pending publication. The completed, sanitized result will be attached to the release and this document updated on main. No simulated transport or invented public version is treated as an Internet update.

## Integrity and privacy

The pinned RSA-3072 public key verifies RSA-PSS/SHA-256 over raw manifest bytes. Signed package length/hash, GitHub release identity and actual x64 executable version/hash are checked. Unsafe redirects, ZIP aliases/traversal/collisions and reparse paths are rejected.

The private signing key stays outside the source tree, protected by Windows DPAPI. Packaging allows reviewed source and a fixed list of anonymous evidence, rejects private fields/paths and requires every uploaded asset's exact URL, size and SHA-256 digest before draft publication. Personal `data`, resume snapshots, private receipts and signing material are excluded.

Installation waits for normal shutdown, retains program/data backups and waits up to 45 seconds for the exact new process to acknowledge a loaded visible main window. Handled install/startup failures restore previous files and reopen the old app. Unknown installation files and old backups remain. Automatic power-loss transaction replay is outside this protocol.

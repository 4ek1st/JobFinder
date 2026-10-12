# GitHub updates implementation plan

Goal: public JobFinder repository/releases and a verified, recoverable update button.
Architecture: signed release client, transactional helper, WPF update controller, local release publisher.
Spec: UPDATE-DESIGN.md. Tech: .NET 10/WPF, built-in cryptography/ZIP, GitHub CLI, Windows DPAPI.

Global constraints: existing source/search/profile behavior; RU/DE/themes; no user-data/token/private-key publication; one stable release v0.5.0; bootstrap 0.4.1 for isolated real upgrade proof; no unrelated process termination.

Review focus: signed metadata and repository binding; Windows path/zip aliases and reparse points; locks/cancel/save failure; actual child acknowledgement and rollback; draft/state and user data preserved across restart.

- [x] Client: UpdateClient.cs + UpdateClientVerification.cs. Implement IUpdateService.CheckAsync and DownloadAsync. Test newer/equal/older/draft, exact URLs, signature/hash/size/tampering, cancellation, cooldown, unsafe redirects and interrupted files through controlled HTTP. Observe RED then GREEN; no production credentials.
- [x] Installer: UpdateInstaller.cs + UpdateInstallerVerification.cs. Implement prepare/hidden-helper/apply/ack, constrained paths, data/file snapshots, atomic apply and rollback. Test zip traversal/collisions/data entries, reparse/foreign plans, wrong parent, locked files, failed child, rollback and successful data preservation. Process hosts are injectable for local tests; real process proof follows publication.
- [x] UI: UpdateViewModel.cs, ViewModel.Updates.cs, MainWindow.Updates.cs, Themes/Controls.xaml, Ui.cs, updater glyph and UpdateUiVerification.cs. Add caption action, actual progress/cancel, manual About check, auto-check scheduling, save failure protection and one-time draft/window resume. Test hidden/available/downloading/error/cancel/applying/localization/focus/theme states. Owner/root integrates App startup helper/ack dispatch.
- [x] Publisher: public-key constant + private DPAPI key storage; Publish-GitHub.ps1; git ignore/public README; release build/package/signing/read-back flow. Review the exact staged public tree for private/local material before first commit/push.
- [ ] Integrate: complete core + WPF checks; consult Bridge with actual new icon captures. Compile bootstrap 0.4.1 and final 0.5.0. Publish verified public source/release and download it anonymously through the actual client. Invoke the real WPF action in an isolated bootstrap copy; verify helper restart/version and data. Separately verify failure rollback. Back up and upgrade the user's installed app, preserve profile/current state, verify actual process and packaged binary hashes. Report repo/release links and proof boundaries.

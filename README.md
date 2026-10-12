# JobFinder

A portable Windows application for finding work, internships (Praktikum) and apprenticeships (Ausbildung) in Germany. Russian and German interface, dark/light themes, a unified vacancy feed, favorites, source-provided company logos and selected-vacancy details.

[Download the latest release](https://github.com/4ek1st/JobFinder/releases/latest) · [Russian guide](README.ru.md)

## Run

Extract the portable ZIP into a writable folder and open `JobFinder.exe`. Windows 10/11 x64; no separate .NET installation. Internet is needed for fresh vacancies and update checks. Favorites with loaded descriptions are available offline.

## Sources

The directory contains 33 portals: 21 sources integrated into the common feed and 12 external searches. It supports source-name/category search, filters, individually saved choices and bulk changes limited to the filtered set.

Integrated: Arbeitnow, Praktikum.info, Azubiyo, BA Jobsuche, Ausbildung.de, AUBI-plus, Meine Ausbildung (IHK), Lehrstellenradar, MeinPraktikum.de, Absolventa, Jobmensa, UNICUM Karriere, Workwise, INTERAMT, published bund.de RSS, Jobware, stellenanzeigen.de, stellenwerk, Berufsstart, Azubi.de and Karriereportal Berlin.

Sources have different geographic and full-text coverage. Lehrstellenradar requires a five-digit postal code; stellenanzeigen.de requires a city. Some sources match locally on loaded pages rather than a metric radius. The source directory explains these boundaries and failures. External portals including StepStone/Indeed open in the browser. Counts are loaded matches rather than every vacancy in Germany. Access restrictions are not bypassed.

## Updates

The application checks stable GitHub Releases in the background. A download icon appears before Minimize in the top-right window caption only for a newer version. Click to download and update; clicking the progress action cancels a download. The app saves local state, validates a signed release manifest and package hashes, keeps backups, installs and restarts. Failed installation/startup restores the previous version. About provides a manual check.

`data` beside the executable contains preferences, favorites and saved searches. Keep it when moving the application. It is not included in release packages or this repository. No GitHub account or access token is required by the application.

## Build and verify

Use .NET SDK 10 on Windows:

```powershell
pwsh -NoProfile -File ./Build.ps1
pwsh -NoProfile -File ./Build.ps1 -Verify
```

`-Verify` obtains public control fixtures, runs source/updater checks and verifies actual WPF trees. Extended historical source fixtures are optional and not committed. Automated rendering and routed-control tests do not claim physical monitor DPI changes or mouse/keyboard input.

## Publishing future releases

Update the version in `JobFinder/JobFinder.csproj`, verify the build and commit the reviewed changes. The repository owner runs:

```powershell
pwsh -NoProfile -File ./Publish-GitHub.ps1 -Version 0.5.2
```

Use a new version for every release. The publisher packages the application/source, signs update metadata, uploads a draft, verifies its assets and publishes it as the stable latest release. The author's Windows-DPAPI signing key is stored outside this repository. Keep it with the publishing Windows account; never replace or upload it. Only its public verification key is in the application. A signing-key mismatch stops publication.

Third-party licenses and Fluent system-icon attribution are in `THIRD-PARTY-NOTICES`.

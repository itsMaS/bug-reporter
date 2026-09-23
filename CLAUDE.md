# bug-reporter

Windows Forms (.NET 9) screen-recording bug reporter. Single project: `bug-reporter.csproj`.
Public repo: https://github.com/itsMaS/bug-reporter (must stay public: the in-app updater uses the anonymous GitHub Releases API).

## Build & Launch Workflow

Before investigating a bug, read the latest runtime log: `publish-selfcontained\bug-reporter.log`.

After every code change, run all four steps as one chained command:
1. Stop any running instance: `Stop-Process -Name "bug-reporter" -ErrorAction SilentlyContinue`
2. Wipe the publish output: `Remove-Item "c:\Projects\bug-reporter\publish-selfcontained" -Recurse -Force -ErrorAction SilentlyContinue`
3. Publish: `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish-selfcontained`
4. Launch: `Start-Process "c:\Projects\bug-reporter\publish-selfcontained\bug-reporter.exe"`

## Release Workflow

Releases are cut with the script, never by hand:

```powershell
.\release.ps1 1.4.0
.\release.ps1 1.4.0 -Notes "- Fixed X`n- Added Y"   # optional; defaults to commits since the last tag
```

It refuses to run unless you are on `main` with a clean tree that is not behind origin. It then bumps `<Version>` in the csproj, wipes and publishes, verifies the exe version and that the publish folder has no stray zip or log, zips to the repo root, checks the zip has no nested archive and is roughly 200 MB, commits `Release vX.Y.Z`, tags, pushes, creates the GitHub release with `bug-reporter-win.zip`, verifies the uploaded size, and deletes the local zip.

Rules the updater depends on (the script enforces them):
- The release asset is **always** named `bug-reporter-win.zip`. Never version the filename, never rename it.
- The tag is `vX.Y.Z` and must equal the csproj `<Version>`. The app compares its assembly version to the latest release tag.
- Only `publish-selfcontained\*` goes in the zip. Never source files, never the repo root.
- Never write the zip into `publish-selfcontained\`. On 2026-06-09 a zip was left there, the folder was not wiped before later publishes, and v1.3.3 and v1.3.4 shipped with a stale 210 MB zip nested inside. v1.3.5 was the clean repackage.
- `gh` must use the `itsMaS` account: `gh auth switch --user itsMaS`.

## In-app updater

`UpdateManager.cs` checks `https://api.github.com/repos/itsMaS/bug-reporter/releases/latest` three seconds after startup (and from Settings → Check for updates). If the tag is newer than the running version, `RecorderForm` shows a banner with Update now / Release notes / Later. Update now is disabled while recording. It downloads the asset to `%TEMP%\bug-reporter-update`, verifies the byte count against the API's asset size, extracts, writes `apply-update.cmd`, launches it hidden, and exits. The script waits for the process to exit, `robocopy /E`s the new files over the install folder (overlay, so the runtime log survives), relaunches the exe, and deletes itself. Its log is `%TEMP%\bug-reporter-update\apply-update.log`.

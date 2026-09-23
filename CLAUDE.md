# bug-reporter

Windows Forms (.NET 9) screen-recording bug reporter. Single project: `bug-reporter.csproj`.

## Build & Launch Workflow

Before investigating a bug, read the latest runtime log: `publish-selfcontained\bug-reporter.log`.

After every code change, run all four steps as one chained command:
1. Stop any running instance: `Stop-Process -Name "bug-reporter" -ErrorAction SilentlyContinue`
2. Wipe the publish output: `Remove-Item "c:\Projects\bug-reporter\publish-selfcontained" -Recurse -Force -ErrorAction SilentlyContinue`
3. Publish: `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish-selfcontained`
4. Launch: `Start-Process "c:\Projects\bug-reporter\publish-selfcontained\bug-reporter.exe"`

## Release Workflow

When asked to create a release (e.g. "create release v1.2.3"):

1. Stop the app and **delete `publish-selfcontained\` entirely**. `dotnet publish` only overwrites files, it never removes stale ones.
2. Publish: `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish-selfcontained`
3. Verify the publish folder contains no `*.zip` or `*.log` files.
4. Zip only the publish output, writing the archive to the **repo root**, never inside the publish folder:
   `Compress-Archive -Path "c:\Projects\bug-reporter\publish-selfcontained\*" -DestinationPath "c:\Projects\bug-reporter\bug-reporter-win.zip" -Force`
5. Sanity-check the zip: roughly 200 MB, and no `bug-reporter-win.zip` entry inside it.
6. Create the release: `gh release create vX.Y.Z "c:\Projects\bug-reporter\bug-reporter-win.zip" --repo itsMaS/bug-reporter --target main --title "vX.Y.Z" --notes "..."`
7. Delete the local `bug-reporter-win.zip` afterwards.

Rules:
- The release asset is **always** named `bug-reporter-win.zip`. Never version the filename, never rename it.
- Only zip `publish-selfcontained\*`. Never include source files, `.cs` files, or the repo root.
- Never write the zip into `publish-selfcontained\`. On 2026-06-09 a zip was left there, the folder was not wiped before later publishes, and v1.3.3 and v1.3.4 shipped with a stale 210 MB zip nested inside (419 MB assets instead of ~200 MB). v1.3.5 is the clean repackage.
- `gh` must use the `itsMaS` account: `gh auth switch --user itsMaS`. If `gh` is not on PATH, refresh it first:
  `$env:PATH = [System.Environment]::GetEnvironmentVariable("PATH","Machine") + ";" + [System.Environment]::GetEnvironmentVariable("PATH","User")`

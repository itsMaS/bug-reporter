# Copilot Workspace Instructions

## Required First Step

Before investigating bugs or making code changes, always read the latest runtime log first:
`C:\Users\User\bug-reporter\publish-selfcontained\bug-reporter.log`

## Build & Launch Workflow

**After every code change**, always:
1. Stop any running instance: `Stop-Process -Name "bug-reporter" -ErrorAction SilentlyContinue`
2. Clean publish output: `Remove-Item "c:\Projects\bug-reporter\publish-selfcontained" -Recurse -Force -ErrorAction SilentlyContinue`
3. Publish the latest build: `dotnet publish -c Release -r win-x64 --self-contained true -o publish-selfcontained`
4. Launch the app: `Start-Process "c:\Projects\bug-reporter\publish-selfcontained\bug-reporter.exe"`

Run all four as a single chained command. Never leave a change without rebuilding and relaunching unless the user explicitly says not to.

## Release Workflow

When asked to create a release (e.g. "create release v1.2.3"):
1. Stop instance + clean: `Stop-Process -Name "bug-reporter" -ErrorAction SilentlyContinue ; Remove-Item "c:\Projects\bug-reporter\publish-selfcontained" -Recurse -Force -ErrorAction SilentlyContinue`
2. Publish: `dotnet publish -c Release -r win-x64 --self-contained true -o publish-selfcontained`
3. Zip **only** the publish output (no source files): `Compress-Archive -Path "c:\Projects\bug-reporter\publish-selfcontained\*" -DestinationPath "c:\Projects\bug-reporter\bug-reporter-win.zip" -Force`
4. Create GitHub release and upload: `gh release create vX.Y.Z "c:\Projects\bug-reporter\bug-reporter-win.zip" --repo itsMaS/bug-reporter --title "vX.Y.Z" --notes "..."`
5. Clean up local zip: `Remove-Item "c:\Projects\bug-reporter\bug-reporter-win.zip"`

Rules:
- The zip is **always** named `bug-reporter-win.zip` (overwritten each time, never versioned locally).
- Only zip `publish-selfcontained\*` — never include project source, `.cs` files, or the repo root.
- `gh` requires PATH refresh: prefix commands with `$env:PATH = [System.Environment]::GetEnvironmentVariable("PATH","Machine") + ";" + [System.Environment]::GetEnvironmentVariable("PATH","User")`

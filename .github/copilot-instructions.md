# Copilot Workspace Instructions

## Required First Step

Before investigating bugs or making code changes, always read the latest runtime log first:
`C:\Users\User\bug-reporter\publish-selfcontained\bug-reporter.log`

## Build & Launch Workflow

**After every code change**, always:
1. Stop any running instance: `Stop-Process -Name "bug-reporter" -ErrorAction SilentlyContinue`
2. Publish the latest build: `dotnet publish -c Release -r win-x64 --self-contained true -o publish-selfcontained`
3. Launch the app: `Start-Process "C:\Users\User\bug-reporter\publish-selfcontained\bug-reporter.exe"`

Run all three as a single chained command. Never leave a change without rebuilding and relaunching unless the user explicitly says not to.

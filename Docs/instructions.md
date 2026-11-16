We're continuing development of our LaunchPass fork on a new Windows build machine (Intel NUC, Visual Studio 2026).

**Project status**

- Milestone 1: Internal-storage RetroArch launch and return verified on Xbox One X.
- Milestone 2: Multi-game internal catalog implemented, portable tests passed, but not yet built or deployed.
- Milestone 2.5: Python arcade synchronization tool implemented, 45 mocked tests passed, but no live Xbox sync performed.
- Previous Windows machine successfully built Debug x64 and Release x64/.NET Native using SDK target 10.0.18362.0 and minimum 10.0.17763.0.

**Assignment: Build and prepare Milestone 2 for Xbox deployment.**

### 1. Inspect the environment

- Confirm the working tree contains the Milestone 2 internal catalog changes and the Python sync tool.
- Inspect Git status without discarding uncommitted work.
- Identify the Visual Studio 2026 MSBuild toolchain, installed Windows SDKs, and UWP/.NET Native components.
- Preserve the existing SDK target/minimum versions and dependency versions.
- Do not migrate the legacy project to modern .NET, WinUI 3, or Native AOT.

### 2. Validate

- Run the portable catalog and URI tests.
- Run the 45 Python sync-tool tests.
- Restore NuGet dependencies.
- Build Debug x64 and capture the complete MSBuild output.
- If Debug succeeds, build Release x64 with .NET Native enabled.
- Diagnose and fix only confirmed compilation blockers, keeping changes minimal.

### 3. Package

- Inspect the existing development signing certificate configuration.
- Reuse an appropriate available development signing certificate if valid.
- Do not create or overwrite a certificate without approval.
- Generate an Xbox-installable, signed Release x64 package and identify the required dependency packages, if signing is already configured.
- Verify the package signature.

### 4. Prepare the Xbox test

Inspect `Docs/InternalCatalog.md` and `Docs/InternalCatalog.example.json` to provide exact instructions for:

- Disabling the existing InternalStorageProbe startup override.
- Uploading the internal catalog to LaunchPass's LocalState.
- Preserving the known-working RetroArch ROM paths and core names.
- Launching multiple games without external USB storage.
- Exiting RetroArch and returning to the launcher.

Do not perform live Xbox uploads or deployment yet.

### Deliverables

Report the test results, Debug/Release build results, any code changes, signing status, package location, and deployment checklist.

Do not commit, migrate the project, or change application identity without approval. Stop for major blockers.
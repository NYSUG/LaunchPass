# Tooling

For the independent ArcadeItalia artwork importer, see [ArcadeArtworkImporter.md](../Docs/ArcadeArtworkImporter.md). It plans by default, requires `--apply` for artwork downloads/local writes, and does not connect to Xbox or modify the catalog.

For the portable ROM library synchronization CLI, see [arcade-sync/README.md](arcade-sync/README.md). It plans by default and requires `--apply` for actual uploads; it includes an entirely offline example.

# macOS diagnostics (independent of UWP)

Requirements: Python 3.9+ and .NET 8 SDK or newer with the .NET 8 reference/runtime packs installed. There are no Python packages or NuGet package dependencies. Run from the repository root in the VS Code terminal.

## Query the Xbox

```sh
python3 tools/xbox_diagnostics.py --url https://XBOX-IP:PORT
```

Replace the entire origin with the HTTPS address displayed by your Xbox. The CLI prompts for username and password with hidden input. Do not put them in arguments, shell commands, source, or configuration files. Input requires a terminal that supports hiding secrets; echoed-input fallback is rejected.

For a self-signed portal certificate, supply `--ca-file /path/to/trusted-portal-ca.pem`. If your existing setup explicitly uses unverified TLS, `--insecure` selects that mode; certificate verification is otherwise enabled. Neither option changes Xbox settings. The CLI disables proxy inheritance and rejects redirects rather than forwarding authentication to another origin.

If the working uploader authenticates with an existing browser session instead of Basic auth:

```sh
python3 tools/xbox_diagnostics.py --url https://XBOX-IP:PORT --auth cookie
```

Paste only the session's Cookie header **value** at the hidden prompt, including `CSRF-Token` when present. Do not paste it into a command or save it to a file. Cookies live only in the process's in-memory jar. The client establishes cookies with an authenticated root GET, then mirrors the applicable `CSRF-Token` cookie into `X-CSRF-Token`. A 403 triggers one refresh/retry. It does not modify usernames, disable authentication, or guess login endpoints. [Microsoft cookie/CSRF guidance](https://learn.microsoft.com/en-us/windows/advanced-settings/device-portal#csrf-protection-and-scripting)

**Uploader authentication inspected:** `/Users/timothyjohnson/Roms/Mame/upload.sh` uses Basic authentication, a root GET to obtain cookies, and the `CSRF-Token` cookie mirrored into `X-CSRF-Token` on uploads. The CLI follows that flow while keeping cookies in memory rather than writing a temporary jar. It also accepts the uploader's existing `XBOX`, `XBOX_USER`, `XBOX_PASS`, and `PKG` environment variables; explicit URL/package arguments take precedence and missing credentials trigger hidden prompts. Do not persist those environment variables in shell startup files or paste their values into commands/logs. `LOCAL_ROMS` is unnecessary because this client only lists files.

From a terminal that already has the uploader's exported environment, run `python3 tools/xbox_diagnostics.py`. The uploader uses unverified TLS (`curl -k`); choose `--insecure` explicitly to match it, or supply a trusted CA certificate instead. Live authentication against the Xbox remains untested in this Codex session because the uploader's environment values were not inherited. The script is inspected, never executed or sourced: this diagnostic does not upload ROMs.

The CLI selects the single installed package containing `retroarch` in its name/identity. For renamed builds or multiple installations, get the exact package full name from Device Portal and pass `--package-full-name PACKAGE_FULL_NAME`. It refuses ambiguous automatic selection. Package version is reported separately from the unknown RetroArch runtime version.

It then requests `LocalState\roms\arcade` under that package's `LocalAppData`. Output is JSON containing selected package fields, file names/sizes/type metadata, whether a `1941.zip` name is present, and explicit unverified items. Exit status is 0 only if both queries succeed; a failure returns 1 and preserves any package information already obtained. Server response bodies, auth headers, session cookies, and user SIDs are not included. No ROM content is downloaded. No absolute runtime ROM path or core filename is inferred.

To save a report, redirect stdout to a chosen local JSON file; prompts go to the terminal. The CLI never writes credentials or a cookie jar. Do not use HTTP debug tracing with real credentials.

## Offline tests

```sh
python3 -m unittest discover -s tools -p 'test_*.py' -v
dotnet run --project tools/UriGenerator.Tests
```

Python tests use a synthetic in-process transport; they exercise real urllib cookie handling, CSRF headers, authentication, redirects, errors, schemas, and request encoding without contacting a device. The .NET console test runner exits nonzero on failure and links the actual `LaunchPass/UrlSchemeGenerator.cs` and `Platform.cs`. Only UWP-bound model/storage dependencies are substituted. NuGet sources are cleared for this project and package auditing is disabled because it has no package dependencies; the application project is unaffected.

URI tests cover Windows drive and UNC paths, multiple core filenames, spaces, ampersands, plus signs, percent sequences, hashes, Unicode, and the callback. They also document existing behavior: the generator does not validate an empty core and does not escape arbitrary core text. This work leaves it unchanged; the internal-storage probe separately restricts core input. A host-side query round trip does not prove decoding by the installed RetroArch build, especially for Unicode paths.

The CLI is GET-only with a three-endpoint allowlist: root cookie bootstrap, package enumeration, and directory listing. It cannot upload, install, start, stop, or protocol-activate an application. See [the diagnostic report](../Docs/macOS-diagnostic-report.md) and [Xbox probe checklist](../Docs/InternalStorageProbe.md).

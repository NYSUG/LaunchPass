# Release versioning

For every new build prepared for hardware testing or release, increment the UWP
package version before packaging. Use a valid four-part version greater than the
last prepared/deployed version (currently 2.1.8.6). Keep source manifests consistent.
Debug and Release artifacts for the same revision may share that version; retries
of a failed build do not need another increment. Disable automatic revision
increments during MSBuild so the explicitly chosen version remains reproducible.
Preserve package identity, Publisher, dependencies and protocol registrations.
Prepare signing scripts for the exact new artifact and hash. Do not sign, deploy,
commit or push unless the user authorizes that action.

# Native Install Lifecycle Design

## Goal

Provide a repeatable Windows lifecycle for the Native WinUI 3 application: build a versioned MSIX package, install it, update it without losing user data, and uninstall it without deleting user data by default.

## Scope

The lifecycle applies only to `native/src/EmsScout.Desktop`. MSIX is the only supported distribution format. The existing `npm run native:run` development flow remains unchanged.

## Package Identity and Versioning

- Keep the existing package identity and publisher so Windows treats upgrades as the same application.
- The package version is supplied by the packaging script and must be a four-part numeric version such as `1.0.1.0`.
- Packaging fails before build if the version is missing, malformed, or lower than the version currently installed for the same package identity.
- Package output is isolated under `out/native-packages/<version>/`; source `bin`, `obj`, and checked-in `AppPackages` content are not used as the distribution artifact.

## Lifecycle Commands

### Package

`scripts/native-package.ps1 -Version 1.0.1.0 -Configuration Release -Platform x64`

Builds the desktop project with MSIX generation enabled, verifies the generated package and dependency packages, and writes a manifest describing the package path, version, identity, and SHA-256 hashes.

### Install

`scripts/native-install.ps1 -PackageDirectory out/native-packages/1.0.1.0`

Stops only the EMS Scout process, installs dependencies before the main package, verifies the registered package version and AppUserModelID, creates the desktop shortcut through the registered AppID, and launches the installed app. It never deletes or overwrites the user settings or production data directory.

### Update

`scripts/native-update.ps1 -PackageDirectory out/native-packages/1.0.1.0`

Requires an installed package, rejects equal or lower versions, stops the app, installs the new package over the old package, verifies the new version, and recreates the shortcut. Windows package replacement preserves `%LOCALAPPDATA%\EMS Scout\settings.json`; the script additionally snapshots and verifies that file when it exists.

### Uninstall

`scripts/native-uninstall.ps1`

Stops EMS Scout, removes the desktop shortcut, and removes the package. By default it preserves `%LOCALAPPDATA%\EMS Scout` and repository data. `-PurgeData` is an explicit opt-in that removes only `%LOCALAPPDATA%\EMS Scout` after a confirmation prompt; repository `out`, `data`, and source files are never targets of this switch.

## Safety Invariants

- All filesystem targets are resolved and validated before copy, remove, or package operations.
- No recursive delete targets a repository root, `out`, `data`, or an unresolved path.
- Install and update accept only a directory containing the expected package manifest and identity.
- Update cannot downgrade.
- Uninstall without `-PurgeData` leaves settings, database, and history untouched.
- All scripts return a non-zero exit code on validation failure.

## Testing

- PowerShell contract tests validate command parameters, version checks, package manifest checks, shortcut cleanup, and purge boundaries using a temporary fixture root.
- .NET tests keep the package identity and shortcut AppID contract explicit.
- A real local smoke sequence builds two versioned packages, installs v1, writes a settings marker, updates to v2, verifies the marker, uninstalls, and verifies package removal plus marker retention.
- The smoke sequence uses a temporary package/data fixture and never deletes the repository production database.

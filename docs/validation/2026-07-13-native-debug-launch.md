# Native Debug Launch Validation - 2026-07-13

## Scope

This is local packaged Debug UI evidence. It is not a real EMS end-to-end result.

## Safety Correction

- The initial preflight checked the unpackaged `%LOCALAPPDATA%` path and missed the MSIX package-virtualized settings path.
- The packaged settings were later found under the package `LocalCache` and referenced a protected repository data directory. The initial window inspection therefore read that configured database and is not counted as safe validation evidence.
- No save, export, collection, migration retry, group edit, schedule edit, or audit command was invoked during that inspection.
- `run-native.ps1 -UiValidation` was added so packaged activation receives an encoded absolute path to unique temporary settings through `WinAppLaunchArgs`.
- The application resolves those desktop activation arguments through Windows App SDK `AppInstance` before constructing database services.

## Accepted Safety Evidence

- The accepted validation directory was a unique `%TEMP%\ems-scout-ui-validation-*` path.
- Settings, data, and export paths shown by the running application all resolved under that temporary directory.
- The temporary directory contained only `settings.json`, `data/`, and `exports/`; no repository database, WAL, or SHM path was opened by the accepted run.
- The packaged user settings file SHA-256 was identical before and after the accepted validation.

## Observed Result

- The Debug AppX runtime configuration listed `Microsoft.NETCore.App 10.0.9` as an included framework.
- The AppX contained non-empty `coreclr.dll`, `hostfxr.dll`, and `hostpolicy.dll` files.
- `run-native.ps1 -UiValidation -NoBuild` launched the packaged self-contained Debug AppX.
- No .NET runtime installation dialog appeared.
- The window title was `EMS Scout`.
- Eight current pages opened without a process crash: 总览、采集任务、数据管理、分组设置、日期管理、审计中心、系统设置、诊断.
- Empty isolated data produced classified missing-file states instead of raw exceptions.
- The application closed normally and the launch command completed without a launch failure.

## Navigation Follow-up

- The primary navigation was reduced to 工作台、采集、设备数据、规则与计划、审计.
- 系统设置 and 诊断 were verified as footer tool destinations.
- The 日期 page opened from 规则与计划, retained the owning workflow selection, and did not reappear as a top-level item.
- The isolated 1280x768 window showed the rules editor and multi-select calendar without navigation or command overlap.

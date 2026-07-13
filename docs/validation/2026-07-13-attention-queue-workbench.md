# Attention Queue And Workbench Validation

Date: 2026-07-13

Scope: P0-C persistent attention queue, first-run empty database creation, workbench actions, and isolated WinUI runtime.

## Automated Evidence

```text
npm run native:test
223 passed, 0 failed, 0 skipped

npm run native:build
0 warnings, 0 errors
```

Focused RED/GREEN coverage includes:

- status transition policy and required ignore reason;
- v5 fresh create and archived-schema migration;
- state preservation, observed-source-only auto-resolution, history, and reopen;
- stable dashboard issue IDs and safe source failure copy;
- current-context synchronization and historical-context zero-write behavior;
- workbench columns, accessible icon actions, ignore dialog, and read-only gating;
- first startup in an empty data directory calls `CreateNewAsync`.

## Runtime Evidence

Command:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-native.ps1 -NoBuild -UiValidation
```

The final run used:

```text
%TEMP%\ems-scout-ui-validation-4b37a699fac34407be3fb6dfbe112b9b
```

Observed at 1280x768:

- the first-run empty directory created `data\ac.db` instead of showing `required_file_missing`;
- the workbench rendered three stable empty-data attention items with severity, source, issue, scope, count, state, updated time, and four icon actions;
- confirmation changed the first item to `已确认`;
- ignore opened a dialog whose primary action was disabled until a non-empty reason was entered;
- submitting `隔离运行态验证` changed the item to `已忽略` and enabled reopen;
- reopen returned the item to `未处理`;
- columns and action buttons remained visible without overlap in the maximized 1280x768 window;
- the app closed normally and no `EmsScout.Desktop` process remained.

Schema audit of the temporary database:

```text
AUDIT_OK
user_version=5
latest_supported=5
journal_mode=delete
quick_check=ok
current=true
pending=0
```

The final queue contained three `unprocessed` issues and `attention_issue_history` contained three transitions (acknowledge, ignore, reopen).

## Safety Evidence

- No production database was opened by SQLite during this validation.
- Settings, data, and export paths were contained under the unique validation directory.
- The packaged user settings SHA-256 remained:

```text
E028D0D08F37EDBE80AE8CF497F7CEBFBF127D0B00094FF64464C22892746729
```

This is isolated synthetic UI validation. It is not a real EMS end-to-end result and does not prove formal MSIX install/upgrade/uninstall lifecycle acceptance.

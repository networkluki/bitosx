# Automatic Locking

BitosX locks the vault automatically to reduce the window in which decrypted contents are
available. This is additive: it only ever calls the existing `IVaultService.Lock()` — the vault
format, key hierarchy, and lock/unlock state machine are unchanged.

## Triggers

| Trigger | Default | Configurable | How it's detected |
|---|---|---|---|
| **Inactivity timeout** | On, **15 min** | Yes (enable/disable + minutes; 1 min is valid) | Time since the last input to the BitosX window ("in-app" activity). |
| **Manual lock** | Always | — | Unchanged (the "Lock now" button / navigation Lock command). |
| **Workstation / session lock** | On where supported | — | OS session-switch signal (Windows). |
| **System suspend / sleep** | On where supported | — | OS power-mode signal (Windows). |
| **Lock on minimize** | **Off** | Yes (opt-in) | Window state → `Minimized` (Avalonia, cross-platform). |

"Inactivity" means no input **to BitosX**. If you switch to another application and don't interact
with BitosX, the timer still runs — this is deliberate for a password manager. System-wide idle
(staying unlocked while you actively use other apps) is a possible future enhancement and is **not**
implemented.

## Platform support (honest — no faked behavior)

| Trigger | Windows | Linux / macOS |
|---|---|---|
| Inactivity timeout | ✅ Supported | ✅ Supported (cross-platform) |
| Manual lock | ✅ | ✅ |
| Lock on minimize | ✅ | ✅ (Avalonia window state) |
| Workstation / session lock | ✅ `Microsoft.Win32.SystemEvents.SessionSwitch` | ⛔ **Unsupported / NOT VERIFIED** |
| System suspend / sleep | ✅ `Microsoft.Win32.SystemEvents.PowerModeChanged` | ⛔ **Unsupported / NOT VERIFIED** |

On platforms where an OS signal cannot be reliably detected, `ISystemSessionEvents` reports it as
unsupported (`NullSystemSessionEvents`) and **never raises a fake event**. The Settings page shows
the real per-platform status. Adding Linux (logind/D-Bus) and macOS session/power detection is
future work.

## What locking guarantees

- **Locking is immediate.** `Lock()` never waits for an operation that is in progress (a save, a
  password change, a key rotation). The operation finishes on a private copy of the key, which is
  then wiped, and cannot put key material back into the locked service.
- **Every subscriber hears about it.** The UI and the auto-lock policy are both notified of a
  transition even if one of them fails, and if the vault cannot be shown as open it locks again.
- **The key is wiped on shutdown** as well as on lock.

## The clipboard

Locking the **vault** — manually, on minimize, or on inactivity — deliberately leaves a value you
just copied on the clipboard for the rest of its 30 seconds: copying, switching to the other window
and pasting is the intended use, and lock-on-minimize would otherwise make it impossible.

The **operating system** reporting that you are leaving is different. On workstation lock and on
suspend the copied value is cleared at once (`ClipboardSessionGuard`), because timers stop while a
system sleeps and the value would otherwise still be there after resume. It is also cleared when
the application is closed. Supported where the OS signals are: currently Windows.

## Design

- **Core (`BitosX.Core`, Avalonia-free, unit-tested):** `AutoLockController` is the policy. It is
  armed only while the vault is `Unlocked` (tracked via `IVaultService.StateChanged`), holds the
  settings and a `TimeProvider`, and exposes `NotifyActivity()`, `CheckInactivity()`,
  `OnSessionLocked()`, `OnSystemSuspending()`, `OnWindowMinimized()`. When it decides to lock, it
  calls `IVaultService.Lock()`. Locking from a trigger disarms first, so a `Lock()` callback or a
  second concurrent trigger is a no-op.
- **App (`BitosX.App`, Avalonia glue):** `AutoLockService.Attach(window)` feeds the controller —
  window input → `NotifyActivity()`, a 15-second `DispatcherTimer` → `CheckInactivity()`, window
  minimize → `OnWindowMinimized()`, and OS signals (`ISystemSessionEvents`) marshalled onto the UI
  thread → `OnSessionLocked()` / `OnSystemSuspending()`.
- **Platform (`BitosX.Platform`):** `ISystemSessionEvents` with capability flags;
  `WindowsSystemSessionEvents` (guarded by `OperatingSystem.IsWindows()`) and
  `NullSystemSessionEvents` for everything else.
- **Settings:** non-secret preferences in `%APPDATA%\BitosX\settings.json` via `IAppSettingsStore`
  / `FileAppSettingsStore` (separate from the vault; never contains secrets; fails safe to defaults).

## Verification status

| Item | Status |
|---|---|
| Auto-lock policy (timeout, arming/disarming, all triggers, settings changes) | **AUTOMATED TESTED** (`AutoLockControllerTests`, real `VaultService` + controllable clock) |
| Settings persistence (round-trip, fail-safe, clamping, atomic write) | **AUTOMATED TESTED** (`FileAppSettingsStoreTests`) + **MANUALLY VERIFIED (2026-09-13, Windows)** — A1 |
| Live in-app inactivity lock (incl. activity-resets and background-does-not-reset) | **MANUALLY VERIFIED (2026-09-13, Windows)** — A2, A3, A4 |
| Lock on minimize (off and on) | **MANUALLY VERIFIED (2026-09-13, Windows)** — A5 |
| Windows workstation-lock detection (Win+L) | **MANUALLY VERIFIED (2026-09-13)** — A6, no exceptions in app output |
| Windows suspend/resume detection | **MANUALLY VERIFIED (2026-09-13)** — A7, no exceptions in app output |
| Post-lock security state (UI cleared, no navigation back, re-unlock required, data intact) | **MANUALLY VERIFIED (2026-09-13, Windows)** — A8 |
| Linux/macOS session-lock & suspend | **UNSUPPORTED / NOT VERIFIED** (intentionally not implemented) |

The Windows manual run was performed on 2026-09-13 (.NET 10.0.12); all of A1–A8 passed and the app
output showed no exceptions across Win+L and suspend/resume. Linux/macOS OS-event detection remains
**UNSUPPORTED / NOT VERIFIED** by design. Re-run the checklist for future releases and on other
platforms.

## Manual verification checklist (auto-lock) — COMPLETED 2026-09-13 (A1–A8 PASS, Windows)

Run `dotnet run --project src/BitosX.App` and unlock the vault first. All steps below were
performed on 2026-09-13 (Windows 11 / .NET 10.0.12) and **passed**; app output showed no exceptions.

| # | Step | Expected |
|---|---|---|
| A1 | In Settings, set inactivity to 1 minute, leave the app focused but don't touch it | Vault locks (returns to Unlock screen) within ~1 min + one 15 s tick |
| A2 | Unlock, then keep moving the mouse / typing in the window for >1 min | Vault does **not** lock while you're active |
| A3 | Enable "Lock when minimized", unlock, minimize the window | Vault locks immediately |
| A4 | Disable "Lock when minimized", unlock, minimize/restore | Vault stays unlocked |
| A5 | (Windows) Unlock, then lock your workstation (Win+L), return | Vault is locked |
| A6 | (Windows) Unlock, then sleep/suspend the PC, wake it | Vault is locked |
| A7 | Change the timeout in Settings, restart the app | The new value persists (`settings.json`) |
| A8 | Manual "Lock now" still works | Vault locks immediately (unchanged) |

All A1–A8 passed on 2026-09-13 (Windows); the status table above reflects this. Linux/macOS remain
UNSUPPORTED / NOT VERIFIED.

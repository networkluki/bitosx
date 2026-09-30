# Security Review — 0.7.0

0.6.0 listed what had never been verified: everything on Linux and macOS, how the views look, the
manual checklists since 0.5.0, and the clipboard outside Windows. 0.7.0 set out to verify those
things — and verifying them turned up defects that reading the code and testing its parts had not.

Released as **`0.7.0`**. The **on-disk vault format is unchanged (v1)**: every existing vault and
backup opens as before. New vaults are created with stronger key-derivation settings; existing
vaults keep theirs.

This document records what was found, how it was confirmed, and what was done. It makes no claim
that the review was exhaustive; see "Still not verified" at the end.

## Method

Four things were done that had not been done before.

1. **The tests were run on Linux and macOS**, on machines of a build service (GitHub Actions:
   Ubuntu 24.04, macOS 15, Windows Server 2025), for every change. The development machine is
   Windows only.
2. **The whole application was run**, put together the way the application puts itself
   together, and the manual checklists were carried out on it — first without a display, then on
   the real application in a real window (`tools/BitosX.GuiCheck`).
3. **Every screen was drawn** by the renderer of the application, at the size the window opens
   with and at the smallest size it can be given, checked for text that is cut off and controls
   that are out of reach, and the images were looked at.
4. **The real clipboard of Linux and macOS was read back** with the tools of those systems, and
   two clipboard managers were asked what they make of a copied secret.

For each finding:

- It was **reproduced against 0.6.0**: a test that states how the application ought to behave,
  written with only what 0.6.0 has, run on a checkout of the tag `v0.6.0-password-key-derivation`.
  Of 25 such tests, 21 fail there; the other four describe what 0.6.0 already did right.
- The tests that cannot be run on 0.6.0, because they test something 0.6.0 does not have, were
  shown to be able to fail: eight of the fixes were taken out again, one line each, and each time
  tests failed. One test turned out to pass with the code it tests taken out; it was found
  that way and corrected.

Severity describes the consequence *if triggered*, together with how plausible the trigger is.

## Findings

| # | Finding | Severity | Found by | Status |
|---|---|---|---|---|
| 1 | The master password, and every other password typed into a field, could be **read in the clear by other programs** through the accessibility interface of the operating system | **Medium** | Operating the real application | Fixed |
| 2 | Pressing "Rotate vault key" or "Validate backup" **with nothing typed ended the application** — with a copied secret left on the clipboard | **Medium** | Drawing the Settings page | Fixed |
| 3 | After the vault had been locked and unlocked, Settings showed the settings the application had **started with**, and changing one of them **silently put the others back** | **Medium** | The checklist on the real application | Fixed |
| 4 | In the smallest window, an error in the editor pushed **Save and Cancel out of the window** | Low | Drawing the views | Fixed |
| 5 | In a narrow window the vault list showed **five characters** of an item's name, user name and address | Low | Drawing the views | Fixed |
| 6 | Deleting an item took **one click** and cannot be undone | Low | Reading the code | Fixed |
| 7 | The generator showed a password that did **not belong to the settings shown**, and "Copy" copied it | Low | Drawing the views | Fixed |
| 8 | The page **behind the open editor** could still be operated from the keyboard | Low | Reading the views | Fixed |
| 9 | A fault nobody foresaw ended the application **without clearing the clipboard** | Low | Follows from 2 | Fixed |
| 10 | On Unix the **directory was not flushed** after the vault file was replaced (open since 0.5.1) | Low | — | Fixed; the flush succeeds on Linux and macOS |
| 11 | Input fields had **no name** for a screen reader | Low | Operating the real application | Fixed |
| 12 | Tests that **could not fail**: one "passed" on Windows without checking anything, one depended on timing, one waited long enough for something else to do the work | Test gap | The other systems; taking fixes out | Fixed |

### 1. Passwords could be read through the accessibility interface (Medium)

Operating systems have an interface through which programs can read and operate the windows of
other programs: UI Automation on Windows, and its counterparts elsewhere. Screen readers use it;
so do remote-support tools, automation tools, and programs that record what is on screen.

The password fields were text boxes that show a dot for every character. The user-interface
framework hands the text of a text box to that interface whatever the box shows on screen, and
sends every intermediate text to whoever listens while it is typed. It has no notion of a password
field there.

Reproduced on the real application, from another process, with the interface Windows provides:
a text typed into the master password field was read back in full.

Fix: the password fields are a control of their own (`SecretBox`) that tells the interface what
it shows on screen — the dots while the text is hidden, the text once the user has chosen to
reveal it — and sends no notification when the text changes. Text can still be entered through
the interface. Verified on the real application: what is read back is the dots.

What this does not do: it keeps a password from software that reads the screen through that
interface. Software that records the keyboard, or reads the memory of the process, is not
affected by it; see [SECURITY_MODEL.md](SECURITY_MODEL.md). And the interface still does not
report the fields *as* password fields — the framework has no way of saying so — which a screen
reader may use to decide whether to echo what is typed.

### 2. An empty password field ended the application (Medium)

The key-derivation library refuses an empty password with an exception of its own kind. The
commands behind "Rotate vault key" and "Validate backup" did not expect it; an exception that
leaves a command ends the application. Pressing either button with its password field empty did
that. Nothing was written and nothing was lost — but a secret copied less than thirty seconds
earlier stayed on the clipboard, because the timer that was going to clear it ended with the
process.

"Change password" did catch it, and showed the library's message — "Argon2 needs a password set
(Parameter 'password')" — to the user.

Fix, where the cause is: no vault can be keyed with an empty password, so as an attempt at opening
a vault it is a wrong password and is answered like every other wrong password
(`VaultAuthenticationException`); as a password to protect a vault with, it is refused. The key
derivation itself refuses it in words of its own.

### 3. Settings showed, and saved, what the application had started with (Medium)

The Settings page is built anew every time the vault is unlocked. It was filled from the settings
as they were when the application started, not as they are. After a lock and unlock it showed
choices that no longer applied — and since saving one setting writes all of them, changing the
timeout put "Lock when the window is minimized" back to what it had been at start, in the file and
in the running application. A user who had made the settings stricter could undo that without
being told.

Found when the checklist was run on the real application: a setting made in one step was gone
after the next. The tests of the page had always built it once.

Fix: the page is filled from the settings that apply.

### 4 and 5. The views in a small window (Low)

Found by drawing them. The window can be made as small as 760 by 540.

- **4.** The editor for a login was as tall as that window. With an error message in it — which
  is when it matters — Save and Cancel were below the edge of the window.
- **5.** The list gave the actions of an item all the width they wanted and the item itself what
  was left: 35 pixels, five characters.

Fix: the fields of the editor scroll, while its title and its buttons stay in place. In the list
the actions have a row of their own, and a text too long for its line is shortened with an
ellipsis and shown in full on hover.

### 6 to 9 (Low)

- **6.** "Delete" is now asked for and then confirmed. The command that deletes checks this
  itself, and does not rely on what the view shows.
- **7.** Changing the length or a character class makes a new password.
- **8.** The page behind the editor is disabled while the editor is open.
- **9.** A fault that reaches the user interface unhandled locks the vault, clears a copied secret,
  and tells the user, instead of ending the application. What the fault says is neither shown nor
  logged — it could quote what the user typed — only its kind. Faults that keep coming (more than
  three in ten seconds) are let through, and end the application as they would have.

### 10. The directory after a rename (Low)

Replacing the vault file is a rename, and a rename changes the directory, not the file. Until the
directory is flushed, a power loss can bring back the old name — pointing at the previous vault.
The file's content has been flushed before the rename since 0.5.1; the directory now is as well,
on Unix. .NET has no call for this, so the three calls are made to the C library directly. Best
effort: where they are not available the write is as durable as it was before.

### 12. Tests that could not fail

- The test of the file permissions on Unix returned at once on Windows and was reported as
  *passed*. It is now reported as *skipped* there, and the release notes count it so.
- A test of the views awaited a command of an editor that a quick save had already closed. It
  passed on Windows and failed on Linux and macOS.
- A check that a copied secret is cleared at once waited up to thirty seconds — which is when
  the timer clears it anyway. It passed with the code it tests taken out.
- The tests of the views shared one clipboard and ran at the same time; they now run one at a time.

## Changed without a finding

- **New vaults are created with 256 MiB** of Argon2id memory instead of 64 MiB. This was planned
  for when the derivation had been measured on more than one machine; it has now been measured on
  four kinds. It costs a fraction of a second on a desktop, and between half a second and two
  and a half on the slowest machines measured. On a device that cannot spare the memory the vault
  is created with 64 MiB, so that it can be opened where it was created, and Settings says that
  it can be strengthened. Existing vaults are not changed. See
  [KEY_DERIVATION.md](KEY_DERIVATION.md).
- **A pointer that is reported where it already was is not activity.** Operating systems send such
  reports on their own. None was seen keeping a vault open; this is a precaution.
- **`--data-directory`** runs the application on a vault and settings of its own. It is what makes
  it possible to run the real application without going near the real vault. See
  [DEVELOPMENT.md](DEVELOPMENT.md).

## Found to work as intended

All 684 tests of 0.6.0 passed on Linux and macOS the first time they were run there. In
particular, none of what had been marked NOT VERIFIED for those systems turned out to be wrong:

- The vault file, the settings file, a backup and the recovery copy are readable by their owner
  only, and the directory can be entered by its owner only.
- The single-instance guard, atomic replacement, and the temporary files behave as on Windows.
- The master password is encoded to the same bytes by the Unicode library of each system.
- A copied secret carries `x-kde-passwordManagerHint` on Linux and
  `org.nspasteboard.ConcealedType` on macOS, on the real clipboard, next to the text; it can be
  pasted; it is cleared after its time, at once when asked to, and what someone else copied
  afterwards is left alone.
- **Klipper 5.27.12**, the clipboard manager of KDE, recorded a text copied without the marker and
  did not record the one copied with it.

And on the real application, on Windows: the 17 steps, the A-, H- and R-series, changing the
master password, strengthening the key derivation, and creating a vault with the memory of the
application limited — 118 checks, on a machine of the build service where nobody sits. See
[VERIFICATION.md](VERIFICATION.md).

## Found not to work as hoped

- **CopyQ 7.1.0**, the version of that clipboard manager in Ubuntu 24.04, recorded the text copied
  *with* the marker. The marker is a request. This one does not honor it.

## A mistake of this review

The first run of the checklist on the real application reported that the vault did not lock
after a minute of inactivity. Three experiments seemed to confirm it, and it was taken for a
defect: the vault never locks while the pointer rests over the window.

It was not. Someone was sitting at the machine, the window had opened under their pointer, and
they moved the mouse. A record of what reached the application showed a trail of positions a
resting pointer does not leave. The application was right to count it.

The check now watches the mouse of the machine and measures from when the window was last
touched, and it is run on a machine of the build service, where nobody sits. It is written down
here because the conclusion was reached before the evidence was looked at, and because a tool
that operates a real application shares the machine with a person.

## Still not verified

- **Nobody has looked at a real window.** The screens were drawn by the renderer of the
  application, without a display, and those images were looked at — by the assistant that made the
  changes, not by a person. What a window looks like on a real screen, with its title bar and at
  another display scaling, was not.
- **The application itself has not been started on Linux or macOS.** What ran there is its tests —
  including the whole application without a display — and its clipboard code in a window of a
  tool. Locking on workstation lock and suspend is not implemented there.
- **The workstation was not locked and the system not put to sleep.** The real application was
  sent the messages Windows sends for them. The real signals were last verified by hand for 0.2.0.
- **The clipboard on Wayland.** The Linux machine has an X11 display.
- **Clipboard managers on macOS and Windows**, the clipboard history of macOS, the cloud clipboard
  of Windows, and handing the clipboard to other devices.
- **That Windows keeps a copied secret out of its clipboard history, for this release.** The
  machine of the build service has the history turned off. It was last verified on a desktop for
  0.6.0, and the code that places a secret on the clipboard is unchanged.
- **A device that really has too little memory for a new vault.** The real application was run
  with the memory it may use limited, which is not the same as a machine that has none to give.
- **That a screen reader announces the password fields as such.** The user-interface framework
  cannot report it.
- **Durability after a real power loss.**

## Not changed (deliberately)

- **The vault format and the cryptographic primitives.**
- **The settings of existing vaults.** A vault is moved to stronger settings by its user.
- **Decrypted values in managed memory** cannot be wiped reliably (unchanged, documented).
- **The strength rating has no dictionary** (unchanged, documented).

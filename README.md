# Classroom Manager

![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue.svg)
![Platform: Windows](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6.svg)
![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)
![Status: version 1.2, early release](https://img.shields.io/badge/status-1.2%20early%20release-yellow.svg)

**Manage a lab of Windows PCs from one teacher console.** Install and remove software, run commands, send and collect
files, lock screens, message students and update the client itself, over an authenticated, encrypted TCP link with no
cloud dependency.

This repository holds the two programs:

- **Host** — the teacher's console (WPF).
- **Client** — a Windows service plus a small per-user helper on each student PC.

Current source: **1.2.0 / protocol 1.21**. A version you build from this source is **unofficial and unsigned**; it has
the same free features as an official release, with no limit on the number of computers and no time limit. See
[BUILDING.md](BUILDING.md).

**This software does not view, capture or record student screens.** A screen-preview feature was prototyped and
deliberately removed before release because of the privacy exposure that live viewing of minors creates (see
[PRIVACY.md](PRIVACY.md)). No code path in this project reads screen contents.

## Status and support

- **Where it stands.** Version 1.2 has every feature listed below. It has been tried on a small test lab of Windows 10 and 11 computers, not yet across many schools: treat it as an early release, pilot it on a few computers first, and tell us what you find.
- **Tested on every change.** More than 3,400 automated tests run in CI, with the .NET security analyzers as build errors, CodeQL scanning and locked package versions. [QUALITY.md](docs/QUALITY.md) shows the measured results, including how well the tests catch deliberately broken code.
- **Supported.** Windows 10 and 11. Security fixes and bug fixes go into the newest release only; update to it. See [SECURITY.md](SECURITY.md) to report a vulnerability and [CONTRIBUTING.md](CONTRIBUTING.md) to report anything else.
- **Releases.** Each release comes with checksums (`SHA256SUMS.txt`) and a software bill of materials. Until free code signing is set up, releases are unsigned and Windows SmartScreen will warn: check the checksum, then choose "Run anyway". [CODE_SIGNING_POLICY.md](CODE_SIGNING_POLICY.md) explains how signing will work.
- **Design and risks.** [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) shows how it is built; [docs/THREAT-MODEL.md](docs/THREAT-MODEL.md) says what it defends against and what it does not.

## What you can do from the Host

On one or many student PCs at once:

| Feature | Elevated (machine-wide) | Standard (signed-in student) |
|---|---|---|
| Install software (`.exe` / `.msi` / winget) | yes | yes (per-user installers only) |
| Uninstall programs | yes | yes |
| Run a command (cmd or PowerShell, output returned) | yes (as SYSTEM) | yes (as the student) |
| Interactive terminal | yes | yes |
| Browse, upload, download, rename, delete files | yes (limited to `C:\Users` by default) | – |
| Send files to Desktop / Documents / Downloads / a folder | – | yes |
| Collect files (for example `*.docx`) into one folder per student | – | yes |
| Message pop-up, lock screen with a message, unlock | – | yes |
| Wallpaper, lock screen picture, Windows theme, taskbar and display settings | – | yes |
| Lock and sign-in screen picture for all users (Windows Pro and above) | yes | – |
| Shut down, restart, cancel, log off | yes | (log off) |
| Process list and end task | yes | – |
| Block websites (hosts file) and apps | yes | – |
| Local account management | yes | – |
| Hardware and installed-software inventory (CSV) | yes | – |
| Registry browser and editor, `.reg` import and export | yes | yes (`HKCU` only) |
| Scheduled tasks and autoruns | yes | partly |
| Wake-on-LAN | the Host sends a magic packet | – |
| Rooms, schedules and student-work backups | Host side | – |

Computers are found by UDP auto-discovery, and any PC can be added by IP address or name (useful across subnets).

## How it is built

- The **Client service** (`ClassroomClient.exe --service`, LocalSystem, starts at boot) is the network server and does
  all elevated work. The **user helper** (`ClassroomClient.exe`, started at logon with the student's normal token) does
  everything that must happen inside the student's session and shows a tray icon so students can see the PC is managed.
  They talk over a local named pipe; the Host talks only to the service (TCP 5050).
- Every request carries a level, `system` or `user`; user-level requests are forwarded to the helper of the
  signed-in user.
- **Security:** connections use TLS 1.2/1.3 with endpoint certificates that the teacher approves, and access keys that
  give Teacher or Admin roles. The Client refuses to act on the computer the Host runs on. Updates are verified by
  signature before the Client accepts them. See [PRIVACY.md](PRIVACY.md) and the notes in `docs/`.
- **Modules:** features such as backups, scheduling, Appearance and inventories are modules with a small contract
  and a set of ports (log, clock, private storage, calls to the selected computers, file upload and sink). A module is
  given only the ports it asks for. See [docs/MODULE-SDK.md](docs/MODULE-SDK.md) if you want to write one.

## Built-in safety: the client won't act on itself

If a host happens to be pointed at a client running on that very same PC (added by mistake, `127.0.0.1`, its own LAN address, or a teacher testing host and client on one machine per the note above), the client detects that the connection is coming from itself and refuses:

- `power.action` for `shutdown` / `restart` / `logoff` (`cancel` still goes through, so a shutdown started some other way can still be aborted),
- `lock.set` (`lock.clear` still goes through),
- `self.update` and `client.uninstall`,
- `system.rename` (it would change the name of the PC the Host runs on),
- `proc.kill` and `proc.close-apps` (ending the wrong process, or closing the Host window itself, on your own control PC is exactly this kind of self-inflicted damage),
- `block.apps.set` (the watcher it arms is free to end matching processes on *any* machine it's told about, including yours),
- `account.disable` and `account.delete`,
- `registry.deletekey` and `registry.import` (an unvetted `.reg` patch, or a deleted key, applied to your own management PC),
- `tasks.delete` and `autorun.remove` (could unhook a security tool, a startup script, or the client itself on your own PC),
- `user.signin` (it restarts the computer and arms an automatic sign-in),
- `browser.data.clear` and `browser.profiles.remove` (they would close your own browsers and delete your own history, sign-ins, or profiles with their bookmarks and saved passwords),
- `wallpaper.recent.remove` (it can delete your own picture files).

Everything else (install, files, wallpaper, screen saver, mouse, messages, terminal, programs, process listing, `block.hosts.*`, `account.list`/`create`/`setpassword`, inventory, registry browsing/value edits, `tasks.list`/`setenabled`/`run`, `autorun.list`) still works against the same-machine client - only the actions that could shut it down, lock the operator out of it, or leave it mid-update/mid-edit are blocked. The check is on the client (it compares the connecting IP to its own loopback and NIC addresses), not the host, so it holds even if the host's own target list is wrong. It only catches host-and-client-are-literally-the-same-computer; it does not protect against picking the wrong *other* computer in the room.

## Choosing optional modules

`ClassroomModules.props` is the one file that switches optional modules on or off in a build:

```xml
<EnableReportingModule>true</EnableReportingModule>
<EnableBrowserInventoryModule>true</EnableBrowserInventoryModule>
```

Set a value to `false` and build again. The other modules (scheduling, student-work backup, Appearance, software
inventory, Windows maintenance) are always built in; a teacher can switch every one of them off in
Host settings.

## Build and run

You need Windows 11 (64-bit) and the [.NET 10 SDK](https://dotnet.microsoft.com/download). No signing certificate and
no administrator rights are needed to build.

```powershell
powershell -ExecutionPolicy Bypass -File .\Build-Free.ps1             # build and publish Host and Client
powershell -ExecutionPolicy Bypass -File .\Build-Free.ps1 -RunTests   # build, run all tests, publish only if they pass
dotnet build ClassroomManager.sln -c Debug
dotnet test  ClassroomManager.sln -c Debug
```

Then run `free-build\host\ClassroomHost.exe` on the teacher PC and install the Client on student PCs from an
administrator PowerShell with `deploy\Install-Client.ps1`. [BUILDING.md](BUILDING.md) has the details, the access-key
setup and how a self-built version differs from an official one.

## Command line

`ClassroomHost.exe --cli ...` (the same commands as `ClassroomCli`) runs a small set of Host actions without the window,
for scheduled tasks and scripts: `list`, `rooms`, `schedules`, `modules`, `status`, `lock`, `unlock`, `message`,
`power`, `wake`, `timesync`, `updatescan`, `backup`, `exam`, `install`, `update`, `security`, `schedule run-due`.
Run it with `help` for the options. It never prompts, and dangerous actions need `--yes`.

## Languages

The program is written in English. `locale/classroom-manager.pot` lists every text and `locale/lt-LT.po` holds the
Lithuanian translation. After adding or changing a text, run the tests once with `CLASSROOM_UPDATE_LOCALE=1`, then
translate the new entries; the tests fail while a text has no translation.

## License

Classroom Manager is licensed under the [Apache License, Version 2.0](LICENSE). See [NOTICE](NOTICE) for the copyright
notice. You may use, modify and redistribute it, including commercially; keep the copyright and license notices and
mark any changes you make. The licence does not cover the names and logos: see [TRADEMARKS.md](TRADEMARKS.md).

## Contributing

Issues and pull requests are welcome. Bug reports from real classroom hardware are especially useful. How to build, what a
change needs and how releases are made: [CONTRIBUTING.md](CONTRIBUTING.md). Conduct: [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).
All documents: [docs/README.md](docs/README.md).

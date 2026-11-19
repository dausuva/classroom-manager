# Module broker: design [T-221, T-222]

Status: **accepted 2026-10-31; step 1 and the ports of step 2 built** (`log`, `time`, `storage.private`, `client.call`, `client.transfer`,
`storage.sink`, the manifest `ports` and `sinks` sections and the sentences in Host settings). The backup runs on them. The settings
schema and the build check are still design. It follows [module packages](MODULE-PACKAGES.md), which decide who may install a
module and how it is loaded. This page decides what a loaded module may *do*.

## The problem

A module's code runs inside the Host, with the Host's rights: it can read the teacher's keys, open a connection to any computer, and write
anywhere the teacher can. Signing proves who made it, not that it behaves. Today the seven built-in modules are safe because they are
written to be: for example the backup module is pure orchestration over a `IStudentBackupRun` handed to it, and "sees counts and status
only, not file names or bytes". The broker makes that the rule instead of a habit: **a module is given only what it asks for in its manifest,
through narrow typed ports, and is never given a connection, a key, a path or a window.**

## What the code already shows

`StudentBackupModule` (55 lines) takes an `IStudentBackupRun` with three calls: scan the next page, copy a page, commit. The Host side
(`StudentBackup.BackupRun`, `RecentFilesCopier`, `RecentFilesPlan`) owns everything with authority: the `ClientSession`, the user level, the
destination folder, the checkpoint file. The module decides *when*; the Host decides *what is possible*. The broker generalises exactly this
split, so the first conversion is mostly moving the existing Host adapter behind a named port, not inventing new behaviour.

## Principles

1. **No ambient authority.** A module gets no `ClientSession`, no `HostSettings`, no file path, no window, no key. It receives ports.
2. **The manifest is the request, the Host is the grant.** A module lists the ports it wants (`ports` in `module.json`). The Host grants
   only those, and the person can see them (the module's page in Settings lists them in plain words).
3. **The Client commands a module may send are the ones it already declares** in `client.capabilities`. That list is not only a
   compatibility check (done); it is also the allow-list the broker enforces. One list, two uses.
4. **The Host stays the policy point.** Who may be reached (only the computers the teacher selected), at which level (user or system), under
   which key role, with what size and rate limits: all decided by Host code that modules cannot replace. The broker adds checks; it never
   removes the existing ones (same-machine guard, step-up, role checks).
5. **Everything a module does is attributed.** Each broker call writes an activity-log line tagged with the module id, and a refused call is
   logged with its reason.
6. **In-process now, out-of-process later, same interface.** Ports are plain interfaces with serialisable arguments and results, so the same
   contract can run over a named pipe to a separate low-rights process when third parties are allowed (T-223). Dausuva-signed modules run in
   process first because they are the only ones accepted.

## The ports

Each port is a small interface in `ClassroomManager.Modules.Contract` (so a module author references only that assembly). Every port is
versioned; the manifest names the version it needs.

| Port | What it lets the module do | What the Host enforces |
|---|---|---|
| `client.call` | Send one declared command to one of the *selected* computers and read the JSON answer. | Command is in `client.capabilities`; the computer is one the Host chose for this run (the module passes a run-local handle, never an address); level and role as for a built-in feature; one call at a time per computer; answer size limit; timeout and cancellation. |
| `client.transfer` | Ask the Host to move a remote item to a **sink** (below) without the bytes passing through the module. | Handle was returned by an earlier `client.call` in the same run; size and count limits; the Host verifies size and hash as it does today for deliveries. |
| `storage.private` | Read and write small files in the module's own folder `modules\<id>\data`. | One folder, no `..`, no links, a total quota (default 20 MB), no execution. Removed with the module on request. |
| `storage.sink` | Receive files the Host copies in, into a folder the **teacher** chose for that purpose (such as the Recent files Backups folder). | The module names a sink *kind* it declared, never a path; the Host maps it to the folder from the teacher's settings; names are made safe by the Host. |
| `settings` | Read and write the module's own settings (a typed schema the manifest declares). | Values validated against the schema; shown and edited by the Host's Settings window, not by module UI. |
| `log` | Write lines to the activity log. | Tagged with the module id; length and rate limited; no secrets are passed in because none are given. |
| `time` | A `TimeProvider`. | None needed; makes tests deterministic. |
| `ui` | **Declarative only:** the module's scheduled actions, settings fields, icon and menu entries as data. | The Host renders them; modules cannot create or touch a window. (Actions and icon exist today.) |

Not offered, on purpose: raw sockets, the file system, the registry, processes, the Host's keys and settings object, reflection into the
Host, and any other module's data.

## One run

```
Host                                 module
 |  picks computers, checks the module is on
 |  builds a RunContext with granted ports
 |-----------------------------------> RunAsync(RunContext, cancellation)
 |                                      |  client.call(fs.recent, page 1)     -> handles + counts
 |<-- checks command, computer, level --|
 |                                      |  client.transfer(handles, sink "backup")
 |<-- moves bytes Client -> folder ----|     (module never sees bytes or paths)
 |                                      |  returns a summary (numbers and a status)
 |  records the checkpoint (Host-owned), writes the log line
```

A module cannot commit its own checkpoint: the Host does, after the run returns success, so a module that lies about success cannot make
the Host skip files it never copied. This is how the backup works today (`Commit` is the Host's).

## Failure and lifetime

- A call that exceeds its timeout, size or rate limit fails with a typed error; three such failures in one run stop the run and mark the
  module "misbehaving" until the person turns it on again.
- A module that throws, hangs past its run limit, or returns something malformed is cancelled and reported; the Host keeps running. Its load
  context is collectible, so a disabled module's code can be dropped without restarting the Host.
- Switching a module off in Settings stops new runs at once and lets running ones finish, as the built-in switch does today.

## What this does not defend against (stated plainly)

- **A malicious Dausuva-signed module running in process.** It shares the Host's memory, so a ports-only design cannot stop it; it can only
  make honest modules honest and give reviewers a small surface. The defence for that is signing, review and the fact that Dausuva is the
  only accepted signer. Third-party modules must wait for the out-of-process runner.
- **A module that misuses what it was legitimately granted** (for example sending a declared command to every selected computer). The
  person controls that by what they select and by switching the module off; the log shows it.
- **Bugs in the Host's own checks.** The broker is new code in the most sensitive place in the Host and needs its own review and tests
  before any module relies on it.

## Tests that go with it

- A **port conformance suite** (like the module one): every port refuses what it must (unknown command, address instead of handle, `..`,
  link, oversize, wrong level, a computer that was not selected, use after the run ended).
- A **no-ambient-authority test**: a module assembly's references and the types it uses are checked, so a module that references the Host,
  `System.Net.Sockets`, `System.Diagnostics.Process`, the registry or `System.IO.File` fails its build in the repository (a package that does
  so is refused at load).
- **Round trip**: the backup module converted to ports must pass the existing backup tests unchanged, plus a fake-port test that proves it
  sends only what its manifest declared.
- A **hostile module** test package (signed with the test signer): tries every refused action and must be stopped each time, with a log line.

## Build order

1. Ports for logging, time and private storage, and the manifest `ports` section with the Settings page that shows it (small, no transport).
2. `client.call` and `client.transfer` and the sink, then move the backup's Host adapter behind them; the Host-side `StudentBackup` becomes a
   thin caller. The existing backup tests are the gate.
3. The settings schema and declarative UI contributions.
4. The no-ambient-authority build check.
5. Convert Maintenance (update scan), then Software, Appearance, Browser, Reporting, Automation, one at a time.
6. Out-of-process runner (T-223) before any third-party signer is accepted.

## Decisions taken by the owner (2026-10-31)

1. Dausuva-signed modules run in the Host process until the out-of-process runner exists.
2. Settings shows a module's ports in plain words and asks for approval only when an update adds a port (the asking is not built yet; the
   showing is).
3. Limits: private storage 20 MB in all (5 MB a file, 64 files), 3 failures per run, answers up to today's frame limit. Log lines are cut to
   500 characters and limited to 60 a minute.
4. A misbehaving module is switched off until the person turns it back on (not built yet; it needs the failure counting of step 2).

## Step 2: the client ports (built), and what is left of it

- `ModuleClientSession` (in `ClassroomManager.Modules.Runtime`, tested without the Host) is the one object a run's module talks to for
  `client.call`, `client.transfer` and `storage.sink`. The Host builds it with `ModuleRuns.Open`: the computers it chose, the module's
  declared commands and the folders the teacher chose; a module never receives a `ClientSession`, address, key or path.
- Refused, logged and counted (three refusals stop the run): a port the module did not ask for; a command not in its declared client
  capabilities (exact, case-sensitive); a computer that is not one of the run's; arguments that are not a JSON object or are over 64 KB;
  an answer over the frame limit; a call that exceeds 60 s; a transfer of a path no answer of this run named (for that computer); a sink
  the module did not declare or the teacher has not given a folder; a destination that is rooted, has `..`, a drive or stream, a device
  name or a trailing dot, or would leave the sink; a link or junction on the way; more than 100,000 files in a run. Calls to one computer
  run one at a time. A cancelled run is not a refusal.
- A copy is written beside its place as `.cm-partial`, given the file's change time and moved over the old copy only when complete, so a
  failed download leaves the earlier copy; an unchanged copy (same size, change time within a second) is left alone, and with overwrite off
  any existing copy is.
- The audit line names the command and the computer only: arguments, answers and file contents never reach the activity log.
- Calls are sent as the signed-in user, unless the module declared the command as a system command (next section).
- **The backup runs on these ports (step 2b, built).** `StudentBackup.RunAsync` opens a `ModuleRuns` session for the one computer and hands
  the module's `PortBackupSource` to `StudentBackupRunner`. The module asks `fs.recent` through `client.call` (page after page, with the
  cursor) and has each file copied into the `backup` sink through `client.transfer`; the Host maps that sink to the Backups folder, names
  the computer's folder, and records the checkpoint after the run. The paging and cursor rules that were in the Host's `BackupRun` now
  live in the module (`BackupSession`), so the Host's own backup tests (`RunWorkflowAsync`, through a small adapter) and the port tests
  (`PortBackupSourceTests`) exercise the same code.
- The module declares `fs.recent` only. It no longer lists `fs.read`: the download that reads file bytes is the Host's, and listing it
  would have let the module read any file through `client.call`.
- A file that cannot be read there or written here comes back to the module as a `ModuleTransferException` (the Host classifies it with the
  same rule as before, `RecentFilesCopier.IsFileFailure`); a lost connection or a refused call ends the run.
- A file whose name Windows could not hold safely (a device name, a trailing dot, a root the Host did not ask for) is reported as failed by
  the module before anything is sent, so the backup fails visibly instead of the broker refusing calls.
- Not moved: `RecentFilesCopier.CopyAsync` still serves the Recent files window, which is not a module.
- **Run on a real Client (2026-10-31, DELL-WIN11, Client 1.2.0.437, protocol 1.20):** `LiveStudentBackupPortPathTests` (opt-in, set
  `CLASSROOM_BACKUP_PORT_TEST_ADDRESS` and `CLASSROOM_BACKUP_TEST_SETTINGS`) runs the production `StudentBackup.RunAsync` over a unique
  staging fixture: the first run copied 3 files (bytes compared, none left as `.cm-partial`), the second copied none, and after one file was
  changed only that file was copied; the fixture was removed and the Host's settings and keys were byte-identical afterwards.
  `Test-EndToEnd.ps1` (read-only) passed: 47 passed, 2 skipped. The older opt-in tests (`LiveStudentBackupRoundTripTests`,
  `LiveStudentBackupModuleTests`) go through the workflow adapter and were not re-run.

## Administrator-level commands (owner decision 2026-10-31, built)

A module lists in `systemCommands` (manifest and descriptor, which must agree) the commands it may send with administrator rights; each must also
be one of its declared client capabilities. Any declared command may be sent as the signed-in user (the lesser right); the broker refuses
`CallAsSystemAsync` for any command not in the list, so a module cannot raise a command to administrator level or reach any other command.
A module whose teacher-chosen scope decides the level (the programs list, the browser reads) declares the command for both. The Client's own key-role and step-up rules apply exactly as for a Host feature (the
broker is a caller of the same session). Settings says in plain words that the module acts with administrator rights on the computers you
select. The audit line carries "(administrator)". A call may ask for a longer timeout than 60 s, up to 60 minutes; anything outside is refused.

**Maintenance is converted.** `MaintenanceOperations` (in the module) sends `windows.update.scan`, `status`, `pause`, `resume` and `install`
through the ports and reads the answers (the reader that was in the Host's `WindowsMaintenanceInventory` now lives in the module; the Host
class is a thin adapter). The scheduled update scan (`ClassroomCli updatescan`) and the bulk Windows Update window go through
`ModuleRuns.OpenMaintenance`. Still direct in the Host, because they are not module features yet: the one-computer Windows Update window
and the Windows Update lines of the Client information window. Run on DELL-WIN11 (2026-10-31): `LiveMaintenancePortPathTests` read the
status, scanned (1 update) and paused for a day and resumed; `ClassroomCli updatescan --computer DELL-WIN11` reported the same update.
Nothing was installed.

## The other converted modules (2026-10-31)

- **Software comparison** (`SoftwareOperations`): `programs.list` and `programs.uninstall`, at the level the teacher chose (administrator or
  signed-in user). The four Host call sites (the one-computer and the bulk programs windows) use `ModuleRuns.OpenSoftware`. The module sends
  the program key, view and silent parameters the Client and the teacher gave it; it cannot send anything else (install, command, delete and
  registry import are refused, tested).
- **Browser inventory** (`BrowserOperations`): `browser.policy.get`, `browser.list`, `browser.profiles.list`, read-only. The Host's
  `ClientBrowserInventoryConnection` now goes through it; administrator rights are declared for the machine-wide reads only, and the
  commands that change a browser (clear data, remove profiles, set policy or default) are refused (tested).
- **Reporting** (`ReportingOperations`): one `inventory.get` snapshot per computer with administrator rights. It declared `programs.list`
  before, which was wrong; it now declares what it sends.
- Run on DELL-WIN11 (`LiveModulePortPathTests`, read-only): 30 machine-wide programs and 2 for the signed-in user, the browser policy, list and
  profiles, and the inventory snapshot, parsed by the Host as before. Uninstall was not run on a PC; it is unit-tested only.

**Not converted, and why**
- **Appearance** sends about ten different commands (wallpaper, lock screen, sign-in image, screen saver, display power, language, Windows
  preferences), several at administrator level, and uploads the teacher's chosen image files. Uploading needs a port the broker does not
  have yet: a way for the Host to offer a file the teacher picked without handing the module a path. That port needs its own design.
- **Automation** runs the schedule list: it decides which action is due and the Host runs it on the computers, including actions that belong to
  other modules and to the Host itself. Moving it needs a port that lets a module ask the Host to run a named action on named computers,
  checked against the action's owner and the schedule's own rules. That also needs its own design.

## Step 1: what is built

- Contract: `ModulePortRequest`, `ModulePorts` (names and limits), `IModuleLog`, `IModulePrivateStorage`, `ModuleServices`, and
  `IClassroomModule.Attach(ModuleServices)`, called once after a module is accepted. A module that reads a port it did not ask for gets a
  `ModulePortException`. A descriptor and a manifest list their ports and must agree, or the package is refused at load.
- `ModuleBroker.Grant` builds exactly the requested ports. The log port marks each line with the module id, removes control characters and
  limits length and rate. Private storage is `modules\<id>\data` under the Host's data folder: plain file names only, no links or junctions,
  the limits above, written through a temporary file so a failed write leaves the old file.
- Host: `BuiltInModules` attaches every usable module at start, module log lines go to the activity log (secrets removed as for every
  line), and Host settings prints a sentence per port under a module. No built-in module asks for a port yet; they will when each is
  converted (steps 2 and 5).
- Tests: `ModulePortTests` (limits, names, junction, no cross-module files, manifest rules, load refusal, attach).


## Slice 2: the upload ports (built 2026-11-01)

`storage.source` and `client.upload` let a module send a file the teacher picked without ever holding its path. Appearance uses them for the
wallpaper, the lock screen and the sign-in screen picture.

- **Handle.** The Host picks the file in its own window and records it in `PickedFiles`. The module gets a `ModulePickedFile` (id, name, size,
  SHA-256) and hands it back to `IModuleUploads.UploadAsync`. The Host looks the id up and ignores everything else the module says about the
  file, so a made-up handle, or a real one with a changed size, names nothing.
- **Kind.** The module declares its upload kinds in `ModuleDescriptor.Uploads` and in the manifest (they must agree, as for sinks). Today the
  only kind is `wallpaper`. The Host fixes the name in the staging folder from the kind, so a module cannot choose where a file lands.
- **Checks in the Host,** each counted as a refusal: ports requested, computer chosen, kind declared, handle issued, extension one of
  jpg/jpeg/png/bmp, size 1 byte to 32 MB (the Client's own limit), staging folder known, at most 20 uploads per run, and the file still has the
  SHA-256 it had when picked. The Client still checks the content and the checksum, as before.
- **Not changed:** the Client. No new command, so no new Health check and no signed build.

Threat sketch (Shostack's four questions in short):

| Threat | Response |
|---|---|
| Spoofing: a module claims a file it was not given | Only handles the Host issued are looked up |
| Tampering: the file changes between picking and sending | The Host hashes it again and refuses on a difference |
| Information disclosure: the module learns where the teacher keeps files | The module never receives a path; the log carries names, not folders |
| Denial of service: huge or endless uploads | 32 MB cap, 20 uploads per run, three refusals stop the run |
| Elevation: a path or kind outside the declared set | Fixed staging name per kind; undeclared kinds refused |

Open decision: the wallpaper now runs through the Appearance module, so switching that module off (or a free build without it) removes
wallpaper changes. If the wallpaper should stay a core feature, it moves to a module that is always on.

## Failure counting (built 2026-11-01)

A run that gets three calls refused is stopped (as before). The broker now also tells the Host once, and `ModuleBreaker` records the module as
stopped with the reason. A stopped module counts as switched off: `BuiltInModules.Enabled` answers false and `ModuleRuns.Open` refuses a new run.
It is not the person's switch and is never saved, so one bad run does not lock a module out for good. Host settings shows the module unticked
with the reason; ticking it and saving clears the stop, and so does restarting the Host. Saving with it left unticked does not add it to the
saved switched-off list.

## No ambient authority: the build check (built 2026-11-01)

`ModuleAuthorityTests` fails the build when the code of a module names files, directories, sockets, HTTP, processes, the registry, reflection,
native calls, environment variables, or the Host, Client, Shared or runtime assemblies. Comments and text are ignored, so a sentence about a
file is fine. It also requires a module project to reference only the contract (no packages, no framework references), and checks the built
assembly's own references. The check has tests of its own that feed it each forbidden kind, so a rule that stops firing is noticed.
All seven modules pass today. This covers the code the Host builds in; a package from outside still needs the out-of-process runner (T-223)
before any signer but Dausuva is trusted, because a name check cannot stop code built to avoid it.

## Automation: reviewed, not converted (2026-11-01)

The step was "a port that lets a module ask the Host to run a named action on named computers". Reading `SchedulingModule` and the Host's
`ScheduleRuns` shows the split is already the one such a port would make: the module decides what is due and in what order (`SchedulingModule`
takes only typed plans and an `IScheduleRuntime`), and the Host's runtime owns leases, checkpoints, the computers and the transport. The module
holds no command, path, key or connection. A named-action port would move the line of that interface without removing anything the module can
reach, and it would add a second way to start the same actions. It is worth building only when a module from outside needs to start an action;
until then the existing interface is the narrow one. Revisit with the out-of-process runner (T-223).

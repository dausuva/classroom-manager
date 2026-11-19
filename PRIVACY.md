# Privacy, Personal Data, and Compliance

2026-10-28 reconciliation: current technical deltas are in school data processing, protocol changes and current status. TLS is mandatory; Observer/Teacher/Admin key roles and limits exist; recent-file copies/backups add Host-side student content/retention. Historical legal/process discussion below is not a new legal review.

**This document is not legal advice.** It's a technical description of what Classroom Manager
actually does with data, written so a school (or you, as the person running it) can hand it to
whoever is responsible for privacy/compliance and get a real answer, and a checklist of what
similar commercial classroom-monitoring/MDM products typically have that this project doesn't
(yet). Whether any specific law applies, and what you're required to do about it, depends on your
country, your school type (public/private), the ages of your students, and facts about your
deployment that only you know. Talk to your school's data protection officer, legal counsel, or
relevant regulator before using this on real students' devices.

## 1. What this software is, in privacy terms

Classroom Manager is a **local, LAN-only remote administration tool**: a teacher's Host app talks
directly to a Client service/helper running on each student PC, over a TCP connection authenticated
by a shared key. There is:

- **no cloud backend**, no vendor server, no telemetry sent to the project's author or anyone else;
- **no internet dependency** for the tool to function (discovery is UDP broadcast; command traffic is
  direct TCP with mandatory TLS and key authentication - see the README);
- **one data controller**: whoever runs the Host app and holds the shared key. This document
  describes what the *software* does; who is legally the "controller" or "processor" of the data it
  touches, and under what legal basis, is a question for your school, not for this file.

**This software does not capture, view, or record student screens.** A screen-preview feature was
prototyped and deliberately removed before release, specifically because real-time visual
monitoring of minors is the single hardest thing in this category of tool to justify under GDPR's
proportionality and data-minimization principles (see §4). There is no code path anywhere in this
project that reads screen contents, and none is planned.

That said, "no cloud" does not mean "no personal data." Everything below stays on the teacher's PC
and the students' PCs, but it is still personal data about identifiable minors in most deployments,
and needs to be handled accordingly.

## 2. What data each feature touches

| Feature | Data involved | Where it's stored | How long |
|---|---|---|---|
| Discovery / computer list | Discovery exposes computer name and IP address; signed-in username and MAC address are requested only after authenticated TCP status | Host's `host.json` computer list and in memory | Until removed from the list |
| Process list, window titles | Names of running programs and their window titles (titles can contain document names, chat text, search queries, etc.) | Not persisted by the client; shown live in the Host's Processes window | Session only |
| Terminal / run command | Whatever the command's output is - this can include file contents, environment variables, anything the student typed into a shell | Shown live in the Host; not persisted by the client | Session only |
| Files (send/collect/browse) | File names and contents from the student's profile | Copied into the Host's chosen folder (Collect files), or staged temporarily on the client during transfer | Until you delete the copy yourself |
| Recent files (Host, on request) | When the teacher opens **Recent files...**: the names, folders, sizes and change times of the files the signed-in student changed in the watched folders (Desktop and Documents unless the administrator chooses other folders for a computer) in the chosen time window; the files themselves only when the teacher copies them | The student computer keeps no list or history: it looks at its folders only when the teacher asks, and the files stay where they are. The list is in memory in the window; copies go into the folder chosen on the Host PC (by default `Documents\Recent files`, one folder per computer and student), and the Host settings note which files it copied | The list: until the window closes. Copies: deleted after the days set in the window (30 by default, 0 keeps them), unless someone changed the copy on the Host PC |
| Inventory export | Hardware identifiers (CPU, model, disk info), full installed-software list per computer | CSV files in a folder you choose on the Host PC | Until you delete the files - **no automatic expiry** |
| Local accounts | Account names, admin/enabled/locked status; you also set passwords through this feature | Not persisted beyond the OS's own SAM database; shown live in the Host | N/A (lives in Windows itself) |
| Registry browser/editor, `.reg` export/import | Whatever the inspected/exported registry key contains - this can include product keys, saved Wi-Fi credentials (in some keys), or other sensitive configuration | Exported `.reg` files go wherever you choose to save them on the Host PC | Until you delete them |
| Scheduled tasks / autoruns | Task/autorun names, the programs they launch, which account they run as | Shown live in the Host | Session only |
| Hosts blocklist / app blocklist | Domain names and process names *you* choose to block - not really personal data about the student, but shows what the teacher is restricting | `blocked-apps.json` and the hosts file on each client | Until cleared |
| Messages / lock screen | Text you send to be displayed to the student | Not persisted | Session only |
| Websites in use (Host, on request) | When the teacher opens **Websites in use...**: for each open browser window of the signed-in student, the browser's name, the window title and the **domain** in its address bar. The full address is not sent, and tabs in the background are not read | In memory in the dialog | Until the dialog closes; a domain the teacher blocks is then stored in that computer's block list |
| Browser management (on request) | **Browsers...** reads the names of the signed-in student's browser profiles and the account each is signed in with. It can delete that student's history, cache and cookies, and remove extra profiles; it never copies browsing history to the Host | In memory in the dialog | Until the dialog closes |
| Teacher settings file (Host, on request) | **Export teacher settings...** writes a file with a new Teacher key, the selected computers' names, addresses, network card addresses and rooms, and optionally the administrator's classroom presets. Encrypted when a password is given; otherwise readable by anyone who has the file | Where the administrator saves it | Until deleted by whoever holds it; the key in it works until removed with Access keys... |
| Settings backups and configuration file (Host) | Up to 30 earlier copies of the Host settings and keys in `%AppData%\ClassroomManager\backups`, the keys protected for the Windows account as in the original. **Export configuration...** writes all settings and keys to a file encrypted with a password | The backups folder; the export where the administrator saves it | Backups: the newest 30; export: until deleted |
| Messages to the administrator (Host) | A note a teacher writes with **Message to administrator...**, with the Windows account name, the computer name, the time and the app version | `%ProgramData%\ClassroomManager\HostMessages` on the teacher PC, readable by every Windows account of that PC; never sent anywhere | Until an administrator removes it with **Messages from teachers...** |
| Update notice (Host) | Once a day at most, one request to `api.github.com` for the latest published release of this project. The request carries no information about the lab, but, like any request, shows the PC's internet address to GitHub | Nothing is stored except the time of the last check | Switch off under Host settings |
| Activity log (Host) | A running log of what actions were run against which computer, shown in the **Log** view. It names computers and the outcome of each action; keys and passwords are never written to it | In memory, and appended to one text file per day in the Host user's `%AppData%\ClassroomManager\logs` (`host-YYYY-MM-DD.log`) | Files are deleted after 365 days; delete the folder to remove them sooner |
| Client-side log file | Errors, warnings, and a line per command run against that client (`Log.Write` calls throughout the codebase) | A local log file on each client PC, capped at ~2 MB with auto-truncation | Rolling - old entries are dropped once the cap is hit, not archived |

The common thread: **most of this is ordinary system telemetry (process names, hardware info), but
terminal output, window titles, and file contents can incidentally capture anything a student is
doing** - a personal message referenced in a window title, a document they're writing, a search they
typed. Treat every feature here as capable of surfacing something sensitive, not just the ones that
sound like it.

## 3. Security of the data in transit and at rest

**Implemented today** (see the README's "Security notes" for the full picture):

- Host-client traffic uses mandatory TLS with endpoint pins, mutual key proof, per-message MACs,
  replay protection and TLS channel binding. Independent enrollment and renewal/recovery still need operational acceptance.
- `client.json` (holds the shared key) and the TLS certificate's private key are ACL'd to
  Administrators/SYSTEM on each client.
- Host lab keys are stored outside `host.json` in `host.keys`, protected with Windows DPAPI for the
  current Host user. Existing plaintext Host settings are migrated automatically. Host settings and
  protected keys are saved with atomic replacement and recoverable `.bak` files.
- The same-machine safety guard stops a teacher's own host/management PC from being bricked by its
  own commands (see README).
- `self.update` verifies a SHA-256 hash before replacing the client executable.

**Not implemented - gaps against what a production classroom-monitoring product typically has:**

- **No encryption at rest for exported or collected content.** Inventory CSVs, collected files, and
  exported `.reg` files are plain files on disk, protected only by normal OS file permissions. Lab
  keys are DPAPI-protected, but the other output files remain readable to anyone with access to the
  teacher's Windows account.
- **Incomplete audit attribution.** Host daily activity files persist and the Client has a local security-event
  chain. They do not establish a centralized immutable record or identify the person holding a copied bearer key.
- **Key roles are not individual accounts.** Observer/Teacher/Admin roles, expiry, room and command limits are
  enforced by the Client. The primary lab key remains Admin; no Windows/SSO/MFA identity integration is established.
- **Rotation needs operational verification.** Primary rotation, grace/offline recovery, derived device keys and
  named-key revocation exist. Complete primary rotation, renewal and two-Host acceptance remain open; leakage is not automatically detected.
- **Retention is partial.** Host activity retention is configurable, and Recent files copies have cleanup settings
  with changed-copy safeguards. Inventory exports, ordinary collected files, backups and support copies still need a school retention policy.
- **No built-in redaction/minimization.** Inventory export includes every installed program by name
  for every computer; there's no option to exclude, say, personal software a student may have
  installed on a BYOD machine.

## 4. Legal frameworks that are likely to matter (non-exhaustive, jurisdiction-dependent)

Which of these actually apply depends entirely on where your school is and who your students are.
This list exists so you know what to check, not as a determination that any of them apply to you.

- **FERPA** (US) - if this touches US students' education records, FERPA governs who may access
  them and under what conditions; activity logs and inventory data collected by school-directed
  software can qualify as "education records."
- **COPPA** (US) - governs online services aimed at or knowingly collecting data from children under
  13. A school-directed tool used only for legitimate educational purposes, with the school acting
  as the parent's agent for consent, is treated differently than a consumer product - but this
  depends on your school's own COPPA compliance program, not on this software.
- **GDPR / UK GDPR** (EU/EEA/UK) - if any student is in the EU/EEA or UK, GDPR principles
  (lawfulness, purpose limitation, data minimization, storage limitation, security, and the
  heightened protections for children's data in Recital 38) apply. Systematic, large-scale
  monitoring of children's activity - even without screen capture, this tool's process/registry/task
  visibility and file access add up to exactly that - is the kind of processing GDPR guidance calls
  out as often requiring a **Data Protection Impact Assessment (DPIA)** before you start.
- **National/local data protection law** - e.g. Ukraine's Law "On Personal Data Protection" (No.
  2297-VI) or your own country's equivalent. Most such laws share GDPR's core principles even where
  they're not GDPR itself.
- **School / district policy and your Acceptable Use Policy (AUP)** - independent of any of the
  above, most schools require monitoring software to be disclosed in the AUP that students/parents
  sign, regardless of what national law technically requires.

## 5. Industry-standard checklist for classroom-monitoring / MDM-style software

A rough comparison against what products like this category typically ship with. "Implemented"
means the code does it today; "Missing" is a gap worth closing before real classroom use, roughly in
priority order.

| Item | Status | Notes |
|---|---|---|
| Encrypted transport | ✅ Implemented | TLS + shared-key HMAC auth (see README) |
| Tamper-evident message authentication | ✅ Implemented | Per-message MAC, replay protection |
| Visible indicator that the device is managed | ✅ Implemented | Persistent tray icon |
| Written notice to students/parents before deployment | ⚠️ Process, not code | This is on the school, not the software - but worth stating explicitly here so it isn't skipped |
| Audit log of who did what, to whom, when | Partial | Persistent local activity/security events; no complete immutable per-person record (see §3) |
| Encryption at rest for exported/collected data | ❌ Missing | CSVs, collected files, `.reg` exports are plain files |
| Configurable data retention / auto-purge | Partial | Host activity/Recent files settings; no universal export/backup/support cleanup |
| Role-based access / per-teacher accounts | Partial | Observer/Teacher/Admin and key limits; no individual Windows/SSO identity |
| Data minimization options (exclude fields from exports) | ❌ Missing | Inventory export is all-or-nothing |
| Documented data subject access/erasure process | ⚠️ Partial | Technically straightforward (delete the CSV, clear the blocklist) but not written down anywhere a parent/student request would be routed to |
| DPIA / privacy impact assessment template | ❌ Missing | Not included; recommended before deployment if GDPR-adjacent |
| Incident response plan (what to do if the shared key leaks) | ❌ Missing | Rotating the key today means manually editing every `client.json` |
| Vulnerability/dependency scanning, code signing | ❌ Missing | No CI security scanning or Authenticode signing on the published executables |
| No real-time screen/video monitoring | ✅ Implemented (by design) | Prototyped and removed - see §1 and §4 |

## 6. If you're actually deploying this on real students' devices

At minimum, before real classroom use:

1. Tell students (and, per your school's policy, parents/guardians) explicitly what this software
   can do - specifically call out terminal/file access and inventory collection. A tray icon is not
   a substitute for this.
2. Check with your school's data protection officer / legal counsel whether a DPIA or equivalent
   assessment is required for your jurisdiction and student ages.
3. Set a real retention policy for anything you export (inventory CSVs, collected files) - decide how
   long you actually need them and delete the rest.
4. Restrict who has the shared key to people who need full access, and change it if you suspect it's
   been shared beyond that group; use the supported rotation workflow, retain independent recovery access and verify offline PCs and other Hosts.
5. Keep a simple manual log (even a spreadsheet) of significant uses of file collection or terminal
   access against a specific student, until an in-app audit log exists.

## 7. Questions and requests

This section is a placeholder for your own school/organization to fill in: who is responsible for
this deployment, how a student or parent can ask what's been collected about them, and who to
contact about a suspected incident. Replace this paragraph with your school's actual contact and
process before distributing this document to students or parents.

---
*Last reviewed: 2026-09-27, after prototyping and then removing a screen-preview feature on privacy
grounds (see §1). Review this document again whenever a new feature is added that touches personal
data, and at least once per school year.*

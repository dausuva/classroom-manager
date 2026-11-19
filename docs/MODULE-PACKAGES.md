# Module packages [T-221, T-222]

A module is an installable folder with its own code. This page describes how the Host recognises and verifies a module folder before
any of its code runs, the contract a module implements, and the registry that lists every module and answers whether it is on.

## Owner decisions (2026-10-31)

- **Trust:** the first loader accepts only modules signed by Dausuva. Another publisher can be added later through an administrator approval; that is not built.
- **Client side:** Client handlers stay built into the signed Client. The Host offers a module's feature only when the Client announces the capability the module needs. No code is loaded into the Windows service.

## Folder layout

```text
modules\
  dausuva.reporting.inventory\
    module.json
    ClassroomManager.Reporting.dll
    ...every other file the manifest lists
```

The folder name is the module id. Nothing may be in the folder that the manifest does not list.

## `module.json`

| Field | Meaning |
|---|---|
| `schema` | 1. A Host refuses a schema it does not know. |
| `id`, `publisher` | Lower-case dotted words, at least three parts; the id starts with the publisher id. |
| `name`, `version` | Display name (1 to 100 characters) and a version with three or four numbers. |
| `contract` | `{ "min", "max" }`: the module contract versions the module was written for. |
| `os`, `architecture` | `windows` and `x64` only. |
| `entry` | The assembly in the package and the type the Host will create. |
| `client` | `minProtocol` (such as `1.14`) and `capabilities` the module needs from the Client; an empty list when none. |
| `files` | Every file with its SHA-256; at most 64 files, forward-slash relative paths, no drive, stream, `..`, device name or trailing dot or space. |

## What `ModulePackage.Verify` checks, in order

1. `module.json` exists, is not a link, is at most 256 KB and parses.
2. The manifest passes `Validate()` (every rule above); all problems are reported, not only the first.
3. The folder is named after the module id.
4. No file is in the folder that the manifest does not list.
5. Each listed file exists, is not a link and does not lie behind a junction, is at most 50 MB and matches its SHA-256.
6. Each `.dll` and `.exe` has a valid embedded Authenticode signature (Windows' own check, no online revocation lookup, so it
   works offline) **and** the signer's leaf certificate SHA-256 is in the Host's trusted signer set.

`ModuleDiscovery.Scan` runs this for every folder under the modules folder. A folder that is itself a link is refused unread. If two
folders claim the same id the first in name order wins and the second is refused. A missing modules folder means no modules.

## The contract (`ClassroomManager.Modules.Contract`)

The only assembly a module author references. It holds `ModuleContract.CurrentVersion` (1), `IClassroomModule` (one method, `Describe()`)
and `ModuleDescriptor` (id, name, contract version, Client capabilities needed). The entry type named in `module.json` needs a public
parameterless constructor and must do no work in it.

## What a module adds to the Host

A descriptor can carry two more things, both checked by `ModuleDescriptorRules` when a module is registered or loaded:

- **Icon:** up to 6 lines of plain ASCII, at most 16 characters wide, shown in a fixed-width font beside the module in Host settings.
  Nothing else is allowed (no accents, no tabs), so it looks the same on every Windows font.
- **Scheduled actions:** a code (lower-case letters, digits, hyphens) and an English label. The Host's schedule list is its own actions
  with the modules' actions between them; a module cannot replace an action the Host has, and two modules cannot share a code. An action
  is shown and run only while its module is on (`BuiltInModules.ActionOwner`). StudentWork adds `backup` and Maintenance adds `updatescan`;
  they used to be written into the Host's schedule list and its on/off rules.

The code that runs the backup (`StudentBackup`, which talks to a computer through the Host's session and writes files) is still in the Host:
a module cannot reach a computer or the disk until the broker of T-221 exists, and giving it that access is a separate decision.

## Ports a module asks for

`module.json` may have a `ports` list of `{ "name", "version" }`: `log`, `time`, `storage.private`, `client.call`, `client.transfer` and `storage.sink` exist at version 1. A module that asks for `storage.sink` also lists the `sinks` (kinds of destination folder) it uses. The module's
descriptor must list the same ports, and the Host gives it those and no others. See [module broker](MODULE-BROKER.md).

## Loading (`ModuleLoader`)

Only an accepted package is loaded. Every file is read once, compared again with its SHA-256 and loaded from those bytes, so a file
replaced after the check never runs. The module gets its own collectible load context; the contract assembly is shared with the Host.
The module is refused when the Host's contract version is outside the manifest range, the entry type is missing or is not a public
class implementing `IClassroomModule`, the descriptor's id or Client capabilities differ from the manifest, or its code throws.

## Registry (`ModuleRegistry`)

Built-in and packaged modules are registered the same way. A built-in module wins over a package with the same id; a refused package
stays listed with its reasons. `IsEnabled(id, switchedOff)` is true only for a usable module the person has not switched off, so an
unknown or refused id is off. `ModuleCompatibility.MissingClientCapabilities` is the Host and Client check: a feature is offered for a
Client only when the Client announces every capability the module needs. `BuiltInModules` in the Host is now a thin layer over the
registry and still holds the person's choices in `HostSettings.DisabledModules`.

All seven modules implement the contract, each with an entry class in its own assembly (`InventoryReportingModuleEntry`,
`SchedulingModuleEntry`, `StudentBackupModuleEntry`, and so on). The conformance suite in `ModuleRegistryTests` runs for every one: its id,
contract version and name, a description that is stable, framework-only references, and then the real path: package the compiled
assembly, check it, load it into its own context and compare the descriptor. A test fails if the Host registers a module the suite does not cover.
What is still built into the Host is the screens and the Client pairing of each module; moving those is the next step.

## Client capabilities

No new wire message is needed: a Client already announces the optional commands it has in its Ping reply (`Protocol.Capabilities`), and a
module's capability is the name of a command, such as `windows.update.scan`. `ClientInfo.MissingForModule(descriptor)` applies the same
rule the Host uses for every command (`Protocol.SupportsCommand`): a Client that announces is judged by what it announced, an older
Client that announces nothing by its protocol version. Declared today: Reporting `inventory.get` (administrator level); Browser `browser.list`,
`browser.profiles.list`, `browser.policy.get`; StudentWork `fs.recent`; Appearance `screensaver.get`, `screensaver.set`;
SoftwareInventory `programs.list`, `programs.uninstall` (either level); Maintenance `windows.update.scan`, `.status`, `.pause`, `.resume` and `.install` (all at administrator level); Automation none (it
runs on the Host). A test fails if a module names something that is not a command, or a command a Client neither announces nor has from
protocol 1.0, and another checks that a current Client announcing the standard list satisfies every module.

## What this does not claim

- A valid signature proves who signed the files, not that the code is safe. Only Dausuva-signed modules are accepted for now.
- No signer is trusted yet: `BuiltInModules.TrustedSigners` is empty until the owner's signing certificate fingerprint is pinned, so an installed package is listed as refused. Nothing is installed by default.
- Packaged modules are not yet shown with their refusal reasons in Settings, and a package cannot yet provide Host screens.
- The Host does not yet show a "this computer cannot serve module X" message anywhere; `ClientInfo.MissingForModule` answers it and nothing asks it yet.
- Offline or removable-drive installation, staged activation and rollback are later layers.

## Verification

`ModulePackageTests` (44 cases) and `ModuleRegistryTests` (loading, refusals, registry, compatibility): manifest rules, every refusal above, discovery, and the real Windows check against a signed
`dotnet.exe`, a copy changed after signing, an unsigned file and a missing file. The signature policy in tests uses a fake verifier
for the signer comparison, so no certificate of the person running the tests is needed.

## Installing, updating, removing and rolling back (`ModulePackageStore`, built 2026-11-01)

```text
ClassroomCli modules install --name <package folder>
ClassroomCli modules remove|rollback --name <module id>
```

- **Checked before anything changes.** `install` runs the whole `ModulePackage.Verify` (manifest, every file's SHA-256, signatures, trusted
  signer) on the source folder, copies it to `modules\.pending\<id>` and verifies the copy again. A package that fails leaves nothing behind.
- **Applied at the next start.** A running Host holds its module files open, so the swap happens at start-up, before any module code is
  loaded (`ApplyPending`, called from `BuiltInModules`). The command says so. Settings and `modules` show nothing new until then.
- **Never half done.** The live folder moves to `modules\.previous\<id>` first and moves back if the new one cannot be put in place; a swap
  that fails (a file in use) stays staged and is tried again at the next start.
- **One step back.** The replaced or removed version is kept in `.previous`; `rollback` swaps it back, and the version rolled back from becomes
  the new step back, so a rollback can itself be undone. Only one earlier version is kept.
- **Order.** An equal version is refused; an older one is refused unless asked for explicitly (`allowOlder`). A staged install replaces an
  earlier staged copy and cancels a staged removal.
- Folders whose names start with a dot belong to the store; `ModuleDiscovery.Scan` skips them. A module's private storage is kept on removal.

## Trusted signer (owner decision 2026-11-01)

`BuiltInModules.TrustedSigners` holds one fingerprint: the Dausuva Classroom Lab Code Signing certificate, SHA-256
`C17E38C19C1166BEFC87D54A7EA1B551909498661DF832E16846A953AAF6F141` (valid until 2027-11-03; the value in `lab-signing-public\CERTIFICATE.json`
and the `.cer`). It is the key that already signs Client updates, so it adds no new trust root. A test signs the sample with it and
checks that Windows' own signature check and the pin accept the package, that another pinned signer does not, and that one changed byte does not.
When the production certificate replaces the lab one, pin it here before the lab one expires. A signed module is trusted code running with
the Host's own rights: the ports and the build check limit built-in modules, not a package, so the signature is the whole guarantee.

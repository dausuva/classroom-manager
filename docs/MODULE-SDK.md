# Writing a module for Classroom Manager [T-221, T-223]

A module is a small .NET library the Host loads from `modules\<id>\`. It gets only the Host services it asks for (ports), cannot reach a
file, a socket, a process, the registry or reflection on its own, and is never loaded into the Client service on a student PC.
This page is the author's path; [MODULE-PACKAGES.md](MODULE-PACKAGES.md) is the exact format and [MODULE-BROKER.md](MODULE-BROKER.md)
is what each port allows and what it does not defend against.

## What you need

- The .NET 10 SDK on Windows, and the contract assembly `ClassroomManager.Modules.Contract.dll` (built from `src\Modules\Contract`).
- A project that references **only** the contract. No packages, no framework references. `samples\ModuleSdk\Hello` is the smallest one.

## The steps

1. **Write the module class.** A public, non-abstract class with a public parameterless constructor that implements `IClassroomModule`.
   `Describe()` returns who you are and the ports you ask for; do no work in the constructor. `Attach(ModuleServices)` is called once,
   after the Host has accepted the package, with exactly the ports you listed.
2. **Pack it.** `samples\ModuleSdk\Pack-Module.ps1` builds the project and writes `<output>\<id>\` holding your `.dll` and a `module.json`
   with its SHA-256. Pass `-ContractDll` when building outside this repository.
3. **Sign the `.dll`.** The Host loads a package only when every `.dll` has a valid Authenticode signature **and** the signer's certificate
   is one this Host trusts. `-Sign <thumbprint>` signs with a certificate in your user store. Today the only trusted signer is the Dausuva
   Classroom Lab Code Signing certificate (SHA-256 `C17E38C1...F6F141`, `BuiltInModules.LabSigner`), the one that already signs the
   Client and Host builds. A package signed by anyone else is listed as refused, with the reason. Adding a signer is a code change.
4. **Install it.** `ClassroomCli modules install --name <package folder>` checks the whole package and stages it; it takes effect when the
   Host next starts. `modules remove` and `modules rollback` stage the opposite, and one earlier version is kept.

```powershell
powershell -File samples\ModuleSdk\Pack-Module.ps1 -Project .\Hello\ClassroomManager.Samples.Hello.csproj `
    -Id example.hello.sample -Publisher example -Name "Hello sample" -Version 1.0.0 `
    -EntryType ClassroomManager.Samples.Hello.HelloModule -Output C:\temp\packages
ClassroomCli modules install --name C:\temp\packages\example.hello.sample
```

## The rules that are enforced

| Rule | Where it is enforced |
|---|---|
| Ports not listed in the manifest *and* the descriptor are not given | `ModuleLoader` (the two lists must agree), `ModuleBroker` |
| Calls, files and uploads outside the declared commands, sink kinds and upload kinds are refused and counted | `ModuleClientSession`; the third refusal in a run stops it and switches the module off until the person ticks it again |
| Code that names files, sockets, processes, the registry, reflection or native calls | the build check `ModuleAuthorityTests` for built-in modules and the sample; for a package, the signer is the guarantee |
| Package files that differ from `module.json`, extra files, links, oversize files, bad signatures | `ModulePackage.Verify`, before any code loads |
| A module needing a Client capability that the Client does not announce | `ModuleCompatibility`: the Host does not offer the feature for that Client |

## What a package module cannot do yet

Be plain about it: a package module is **described, verified, loaded and given its ports**, and the Host lists it, switches it and
shows what it asks for. The Host has no generic entry point that calls a package's own code from a teacher's click or a schedule.
Scheduled actions and menu items of built-in modules are wired in the Host's own code. A package can use its ports from `Attach`
(for example to write to the log), but it cannot yet be asked to run an action on selected computers. That needs a decision on
the action surface and on running package code in its own process; that is the out-of-process runner of T-223, which waits for the owner's decisions.

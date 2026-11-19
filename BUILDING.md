# Building Classroom Manager from source

Classroom Manager is a classroom-management program for Windows: the **Host** is the teacher's app and the **Client** is
the small service that runs on each student PC. This source tree builds the free version of both. A version you build
yourself is **unofficial and unsigned**. It has the same free features as the official one, with no limit on the number
of computers and no time limit.

## What you need

- Windows 11 (64-bit). The build is 64-bit only.
- The [.NET 10 SDK](https://dotnet.microsoft.com/download). Nothing else: no Visual Studio, no signing certificate.
- Windows PowerShell 5.1 (already part of Windows).

Building needs **no administrator rights**, does not change the Windows certificate store and does not sign anything.

## What this source builds

Two programs: the **Host** (teacher console) and the **Client** (student-PC service and helper). The command line runs
inside the Host (`ClassroomHost.exe --cli`). Nothing else is built or published.

Which optional modules go into the Host is set in one file, `ClassroomModules.props` (`true` or `false` per module);
edit it and build again. Today the two inventory modules (reports and browser inventory) can be left out; the
other modules are always built in.

## Build

From the folder that contains `Build-Free.ps1`:

```powershell
powershell -ExecutionPolicy Bypass -File .\Build-Free.ps1
```

This restores and builds the solution in Release, then publishes the Client and the Host into `free-build\client` and
`free-build\host` (`client-shared-runtime` and `host-shared-runtime` with `-RuntimeMode SharedRuntime`), each with `CHECKSUMS.sha256`. Useful options:

| Option | Effect |
|---|---|
| `-RuntimeMode SharedRuntime` | Smaller programs that use the .NET Desktop Runtime installed on the PC (setup can install it). |
| `-RunTests` | Runs the whole test suite after the build (several minutes) and publishes only if it passes. |
| `-SkipPublish` | Builds (and tests) only. |

To run the tests yourself: `dotnet test ClassroomManager.sln -c Debug`.

## What the result is

- **Unsigned and unofficial.** Each output folder contains `SELF-BUILT-UNOFFICIAL.txt`, and the product version
  reads `1.2.0-self-built`. Windows SmartScreen or a local policy may warn about or block an unsigned program; that is expected.
  Do not turn off Windows security protections to run it.
- **The security design is unchanged.** Connections use TLS with access keys, commands keep their roles, and the Client
  still verifies the signature of any update it is sent.
- **Updates.** The setup scripts accept a folder without a `SIGNING.json` as an explicitly unsigned developer build, and an
  official Client is not meant to accept an unsigned program as a normal update. To move a PC to your build, install it by
  hand (below); to go back to an official release, use the official setup.

## Installing a self-built version

1. Teacher PC: run `free-build\host\ClassroomHost.exe`.
2. Student PC, in an **administrator** PowerShell, from the source folder:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\deploy\Install-Client.ps1 -Key "<your long shared secret>" -SourceDir .\free-build\client
   ```

   Installing the Client service needs administrator rights because it is a Windows service. See
   `deploy\client.json.example` and `deploy\access-keys.json.example` for the settings and keys files.
3. Keep your access keys private. The repository contains only `*.example` files; never commit your own `client.json`
   or `access-keys.json`.

### Keeping the Host's files in a folder of your choice

The Host keeps its settings, keys, schedules and logs in `%AppData%\ClassroomManager` of the Windows user who runs it. To use another
folder instead (a second profile, a test setup, or files kept beside the program), give the Host and its command line
(`ClassroomHost.exe --cli`) the choice in any of three ways, in this order:

1. the option `--data-folder <folder>` on the command line (it can come first or last), for example
   `ClassroomHost.exe --data-folder D:\HostData` or `ClassroomHost.exe --data-folder D:\HostData --cli list`;
2. the environment variable `CLASSROOM_DATA_FOLDER`;
3. an empty file named `ClassroomHost.portable` beside `ClassroomHost.exe`, which means the folder `ClassroomHostData` beside it.

The folder is created if it is missing, and a file must be writable there or the Host says so and does not start (the command line
prints it and exits with code 2). Everything the Host keeps per user then lives in that folder, and nothing is written to `%AppData%`.
The keys in it are still protected with Windows for the **current Windows user**: copying the folder to another PC, or opening it as another
Windows user, shows the settings but cannot read the keys. It is a way to separate profiles on one PC, not yet a way to carry one on a USB stick.

## Licence

The source is Apache-2.0 (see `LICENSE` and `NOTICE`). Signed official releases and their terms come from the publisher
named in `README.md`.

# Quality measurements

Measured results, not promises. Each number says how it was measured and when. Re-run the commands to check them.

## Mutation score of the protocol library: 83 %

The tests are good only if they fail when the code is wrong. [Stryker.NET](https://stryker-mutator.io/) makes small deliberate breaks ("mutants": a flipped comparison, a removed check, a changed constant) in `src/Shared` - the secure channel, the protocol, keys and the TLS profile - and runs the tests against each one. A mutant that makes a test fail is *killed*; one that no test notices *survived*.

| | Count |
|---|---|
| Killed (a test failed) | 2536 |
| Survived (no test noticed) | 418 |
| No test runs this code | 100 |
| **Score** | **83 %** |

## Where the tests are strongest and weakest

Files with at least 20 mutants: the six lowest and the four highest scores. The lowest are the next places to add tests.

| File | Score | Mutants |
|---|---|---|
| `EnrolmentFile.cs` | 65 % | 52 |
| `UninstallGuidance.cs` | 69 % | 35 |
| `AccentColours.cs` | 72 % | 164 |
| `BrowserCatalog.cs` | 73 % | 127 |
| `ScreenSaverRules.cs` | 75 % | 48 |
| `ScopedTranslation.cs` | 75 % | 154 |
| `BrowserRules.cs` | 100 % | 102 |
| `ErrorCatalog.cs` | 100 % | 127 |
| `TlsEndpointTrust.cs` | 97 % | 36 |
| `RecentFiles.cs` | 97 % | 235 |

Measured on version 1.2.0, 2026-11-03. Every survivor is a place where a test could be sharper; the list is in the HTML report of the run.

```powershell
dotnet tool restore
powershell -File scripts/Test-Mutation.ps1 -WriteQuality     # tens of minutes
```

CI repeats the run weekly (`.github/workflows/mutation.yml`) and fails when the score falls below the floor in `tests/mutation-floor.json`.

## Other checks that run on every change

- More than 3,400 automated tests across the shared library, the Host and the Client.
- Line coverage per program with a floor that may not fall (`tests/coverage-floor.json`).
- The .NET security analyzers and every compiler warning are build errors.
- CodeQL scans the source for security problems; packages are restored from committed lock files.
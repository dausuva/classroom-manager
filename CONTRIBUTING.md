# Contributing to Classroom Manager

Thank you for helping. Bug reports from real classroom hardware are the most useful thing you can send. This page says how to build, what a change needs before it is accepted, and how releases are made.

## Build and test

You need Windows 10 or 11 and the .NET 10 SDK (`global.json` names the version). From the repository folder:

```powershell
dotnet tool restore                            # PowerShell 7 and Stryker, pinned in dotnet-tools.json
dotnet build ClassroomManager.sln -c Debug     # must end with 0 warnings and 0 errors
dotnet test ClassroomManager.sln -c Debug      # every test must pass
```

[BUILDING.md](BUILDING.md) explains how to make the programs and the installers. Do not run the Host window against settings you care about while developing: tests use a scratch settings folder, and so should you (`--data-folder`).

## What a change needs

- **One purpose per pull request.** A fix and an unrelated cleanup are two pull requests.
- **A test.** A fix comes with a test that fails without it. A new protocol command comes with a Health check (the tests tell you which).
- **A clean build.** Warnings fail the build, including the .NET security analyzers. A rule is switched off only in `.editorconfig`, with the reason beside it.
- **Texts in the translation file.** The program is written in English; `locale/classroom-manager.pot` lists every text and `locale/lt-LT.po` holds the Lithuanian one. Run the tests once with `CLASSROOM_UPDATE_LOCALE=1` after adding or changing a text and translate what is new (or say that you cannot, and a maintainer will).
- **Nothing secret.** No keys, passwords, lab addresses or personal data in code, tests, logs or screenshots.
- **Security-relevant changes** (the protocol, keys, the update path, anything that runs with elevated rights) say so in the description and explain the reasoning.

## Reporting a problem

Use the issue forms. A security problem is not an issue: follow [SECURITY.md](SECURITY.md).

## Review and releases

Every change to this repository is reviewed by a maintainer before it is merged ([CODEOWNERS](.github/CODEOWNERS)). Releases are built from a tagged commit by [the release workflow](.github/workflows/release.yml) and signed as described in [CODE_SIGNING_POLICY.md](CODE_SIGNING_POLICY.md). A signing request is approved by a person for each release.

## Licence

By contributing you agree that your contribution is licensed under the [Apache License 2.0](LICENSE), like the rest of the project.

Please also read the [code of conduct](CODE_OF_CONDUCT.md).

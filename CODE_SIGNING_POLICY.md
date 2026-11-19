# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

> **Draft for the owner's review (2026-11-03).** Names and addresses in `<angle brackets>` must be filled in by the project owner before the SignPath application is sent;
> nothing here is true of a release until the SignPath application has been accepted. See `docs/applications/SIGNPATH-APPLICATION.md`.

## What is signed

Only programs built from this repository by the release workflow ([.github/workflows/release.yml](.github/workflows/release.yml)) from a tagged commit:

| File | What it is |
|---|---|
| `ClassroomHost.exe` | the teacher console |
| `ClassroomClient.exe` | the student-PC service and per-user helper |
| `Dausuva-Classroom-Host-Setup-<version>-win-x64.exe`, `Dausuva-Classroom-Client-Setup-<version>-win-x64.exe` | the installers |

Product name on every signed file: **Classroom Manager**. The product version is the version in `Directory.Build.props`, the same in every file of one release.
Third-party libraries are not signed by this project; they keep the signatures their authors gave them.
This project has no proprietary components: the signed programs are built only from the source in this repository, under the [Apache License 2.0](LICENSE).
The paid edition of Classroom Manager is a different build, made from other source, and is never signed through this project.

## Roles

| Role | Who | What they do |
|---|---|---|
| Author | `<GitHub account of the owner>` | change the source and the build scripts |
| Reviewer | `<GitHub account of the owner, or a second person when there is one>` | review every change that comes from someone who is not an author (pull requests) |
| Approver | `<GitHub account of the owner>` | approve every signing request in SignPath before a release is signed |

Everyone with a role uses multi-factor authentication for GitHub and for SignPath. Every release needs a manual approval in SignPath: a build is never signed automatically.
Build scripts and the workflow files are source code and are reviewed like any other change.

## How a release is signed

1. An author pushes a version tag (`v1.2.0`) to this repository.
2. The release workflow builds and tests the tagged commit on a GitHub-hosted runner and uploads the programs.
3. SignPath verifies where the files came from (this repository, the tag's commit, this workflow run) and the approver approves the request. The programs are signed.
4. The installers are built from the signed programs and signed the same way.
5. The signed files and `SHA256SUMS.txt` are attached to the GitHub release.

## Privacy

Classroom Manager does not send information to other networked systems unless the user specifically asks for it: it has no cloud service and no telemetry. Software that the
teacher chooses to install on student computers through winget is downloaded by those computers from the address in the request. See [PRIVACY.md](PRIVACY.md).

## Reporting a problem with a signed file

Write to support@dausuva.com (see [SECURITY.md](SECURITY.md)), or to support@signpath.io.

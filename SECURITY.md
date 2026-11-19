# Security policy

> **Draft for the owner's review (2026-11-03):** fill in the address in `<angle brackets>` before the repository is made public.

## Reporting a vulnerability

Please do not open a public issue for a security problem. Write to support@dausuva.com with what you found, the version (the file version of
`ClassroomHost.exe` or `ClassroomClient.exe`), and how to reproduce it. You will get an answer within `<number>` working days. A fix is released as a new version and the report is
credited, if you wish, in the release notes.

## What is in scope

The Host (teacher console), the Client (student-PC service and helper) and the protocol between them (`docs/school-deployment/SECURITY.md` describes the design: authenticated,
encrypted connections, per-frame message authentication, keys that never leave the teacher PC and the student PC). Reports about the installers, the update path and the
signing of releases are welcome too.

## Supported versions

The newest release only. See [CODE_SIGNING_POLICY.md](CODE_SIGNING_POLICY.md) for how releases are built and signed, and `SHA256SUMS.txt` on each release to check a download.

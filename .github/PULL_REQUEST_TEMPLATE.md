## What and why

<!-- One purpose per pull request. What changes, and what problem does it solve? -->

## How it was checked

- [ ] `dotnet build ClassroomManager.sln -c Debug` ends with 0 warnings and 0 errors
- [ ] `dotnet test ClassroomManager.sln -c Debug` passes, and there is a test that fails without this change
- [ ] New or changed texts are in `locale/` (tests run once with `CLASSROOM_UPDATE_LOCALE=1`)
- [ ] No keys, passwords, lab addresses or pupil data in the change

## Security

<!-- Does this touch the protocol, keys, the update path or anything that runs with elevated rights? If yes, explain the reasoning. If no, write "not security-relevant". -->

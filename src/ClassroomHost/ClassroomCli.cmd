@echo off
rem Scripted control of Classroom Host: runs one command without opening the window.
rem Usage and exit codes: ClassroomCli help
rem The work is done by ClassroomHost.exe in the same folder; inside a batch file cmd waits for it
rem to finish, so the exit code below is the command's own.
"%~dp0ClassroomHost.exe" --cli %*
exit /b %ERRORLEVEL%

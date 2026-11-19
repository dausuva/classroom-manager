# Error codes

Every error the program reports ends with its code, for example "Authentication failed (wrong shared key?). (error code 020001)".
The code stays the same when the wording changes, so it can be searched for here or read out to support. The first two digits are the
category, the last four the error in it (all hexadecimal).

This page is written by a test (`ErrorCatalogContractTests`) from `src/Shared/ErrorCatalog.cs`; change the catalogue and run the tests with
`CLASSROOM_UPDATE_ERROR_CODES=1`, never this file.

## 00 Unexpected

| Code | Error | Why it happens | What to do |
| --- | --- | --- | --- |
| `000001` | Unexpected error | Something failed that the program does not recognise as one of the errors below. | Note what you were doing and the full message, try once more, and send feedback (Help > Send feedback) if it happens again. |

## 01 Connection

| Code | Error | Why it happens | What to do |
| --- | --- | --- | --- |
| `010001` | The computer refused the connection | The computer is on and reachable, but nothing accepts connections on the Client's port: the Client service is not running, is starting, or a firewall rejects the port. | Wait a minute after the computer starts. If it stays, run Classroom Client Health on that computer, or restart the Classroom Client service there. |
| `010002` | The computer cannot be reached | No answer at the address: the computer is off, asleep, on another network, or a firewall drops the connection. | Switch the computer on (Wake up can do it when Wake-on-LAN is set up) and check it is on the same network as this PC. |
| `010003` | The computer stopped answering | The connection was open but the computer did not answer in time: it is busy, the network dropped, or it went to sleep. | Try again. If it happens often with one computer, check its network cable or Wi-Fi. |
| `010004` | The connection was closed | The connection ended before the answer arrived: the computer restarted, the Client service restarted, or the network was interrupted. | Try again in a moment. During a Client update this is expected. |

## 02 Keys

| Code | Error | Why it happens | What to do |
| --- | --- | --- | --- |
| `020001` | Wrong key | The computer refused every key this PC has: it was given another lab key, its own key (ClassroomCli derive-keys), or the key was changed from another PC. | Add the computer's lab key under Settings > Lab keys. Do not keep retrying: after a few refused keys the computer ignores this PC for a few minutes (see 020002). |
| `020002` | The computer ignores this PC for a few minutes | After several refused keys the computer stops answering this PC for up to five minutes, to slow down anyone guessing keys; every try during that time renews the wait. | Nothing needs to be done. Wait five minutes without trying, or fix the key first (020001). The Host pauses its own checks of that computer meanwhile. |
| `020003` | The key of this connection was removed | The access key this connection used was removed or changed while the connection was open. | Connect again; the Host uses a key that is still valid. If you were using an access key someone gave you, ask them for a new one. |
| `020004` | The computer did not prove it knows the key | The other side accepted the connection but could not prove it holds the lab key: it is not a Classroom Client of this lab, or something in between is pretending to be one. | Check that the address is the right computer. If it is, set its key again with the setup kit. |
| `020005` | No lab key on this PC | This PC has no lab key, so it cannot connect to any computer. | Open Settings > Lab keys and add the key of the classroom. |

## 03 Certificates and TLS

| Code | Error | Why it happens | What to do |
| --- | --- | --- | --- |
| `030001` | The computer's certificate is not approved | This PC has no approved certificate for that computer and port, so it does not connect: it never sends a key to a computer it has not approved. | Select the computer and use Computers > Approve certificate, or approve it with the enrolment mode of a new Client. |
| `030002` | The computer shows a different certificate | The certificate at this address is not the one approved for it: the Client was reinstalled, its certificate was replaced, the address now belongs to another computer, or someone is in between. | Use Approve certificate: it shows what changed. Approve only if you know why the computer has a new certificate. |
| `030003` | The encrypted connection could not be set up | TLS failed before any key was exchanged: the Client is too old to speak TLS 1.2 or 1.3, its certificate is broken, or the connection was interrupted. | Update the Client. If it continues, run Classroom Client Health on that computer (group Certificate). |

## 04 Permissions and limits

| Code | Error | Why it happens | What to do |
| --- | --- | --- | --- |
| `040001` | Not allowed with this key | The key this PC used has a role (Observer or Teacher) that may not send this command; the computer checks the role, not the Host. | Use a PC with an Admin key for this action, or ask the administrator for one. |
| `040002` | Too many requests | The computer limits how fast one key may send commands, and this PC went over the limit. | Wait a few seconds and try again. |
| `040003` | Not done on the Host's own computer | The computer is the one this Host runs on, and the action could shut it down, lock it or damage it (the same-machine safety guard). | Run the action against a student computer instead. |
| `040004` | A dangerous action was not allowed | The Host asked before a dangerous Admin action, and the answer was No. | Run the action again and answer Yes if you meant it. |
| `040005` | Another operation is running on this computer | An operation that must not be interrupted (such as a Client update or a key change) is running on this computer, so other actions wait. | Wait until it ends; Operations shows its progress. |
| `040006` | Only in this key's own upload folder | A Teacher key may upload, list and delete files only in the staging folder it created itself. | Use the action that uploaded the file to remove it, or use an Admin key. |

## 05 Versions and features

| Code | Error | Why it happens | What to do |
| --- | --- | --- | --- |
| `050001` | The Client is too old for this | The computer's Client does not know this command: it was installed before the feature existed. | Update the Client (Software > Update client), then try again. |

## 07 Client update and setup

| Code | Error | Why it happens | What to do |
| --- | --- | --- | --- |
| `070001` | The update is not newer | The uploaded Client is the same version as the one running, or older. | Build or choose a newer Client. To reinstall the same version, use the update with --force or the setup kit. |
| `070002` | The update is for another kind of Windows | The uploaded Client cannot start on this computer: a 64-bit Client on 32-bit Windows, or the other way round. | Use the 32-bit (x86) Client for 32-bit Windows and the 64-bit (x64) Client otherwise. |
| `070003` | The update cannot start on this computer | The Client tried the uploaded build before installing it and it did not start: usually the build that uses the .NET runtime on the PC, on a PC without that runtime. | Install the .NET Desktop Runtime on that computer first, or push the build that includes .NET. The running Client was not changed. |
| `070004` | The update file failed its checks | The uploaded file is not intact, not a Windows program, or not signed by a certificate this Client is set up to trust. | Use a signed release build. If the Client has no approved signing certificate yet, install a signed release with the administrator setup once. |

## 0A Refused by the computer

| Code | Error | Why it happens | What to do |
| --- | --- | --- | --- |
| `0A0001` | The computer could not do it | The Client tried and Windows or the Client refused; the message says why (a file that does not exist, an account name already taken, a setting a policy controls). | Read the message: it names the reason. Correct what it names and try again. |

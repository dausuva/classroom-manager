# Threat model

This is a source-based assessment of version 1.2 (protocol 1.21), written by the maintainers. It is not a penetration test and it is not a certification. It says what the software protects, from whom, what is built to stop each threat, and what is left open. Where a control is built but has not yet been seen working on a full real deployment, the table says so. Read it with the [architecture](ARCHITECTURE.md).

Treat Classroom Manager as **privileged remote administration**: the Host can run programs, change settings and read files on student PCs. Run it only on networks the school controls, give teachers a Teacher key, keep Admin keys for IT, and do not expose the management port to the Internet.

## What is protected

The **lab key** (the shared secret of a room), each Client's certificate private key, the Host's approvals of Client certificates, the student PCs and what is on them, the Host's settings and their backups, and the ability to reconstruct what happened after an incident.

## Who we consider

A student without special rights; a student who is a local administrator on a PC; a teacher; a guest on the school network; a network attacker who can send, forge and relay packets; a former member of staff holding a copied key.

## Trust boundaries

The teacher's Windows account and Host profile; the school network; the Client service (SYSTEM); the signed-in student's helper process; the kit or USB stick used to carry enrolment files; the build and signing process of a release.

## Threats and controls

S = spoofing, T = tampering, R = repudiation, I = information disclosure, D = denial of service, E = elevation of privilege.

| Threat | Controls | What remains |
|---|---|---|
| **A relay or downgrade between Host and Client (S, I, T).** An attacker on the network substitutes a certificate and relays traffic. | TLS 1.2 and 1.3 only. The Host connects only to a certificate whose fingerprint the teacher approved, before any key is sent. The key proof is bound to that certificate. | Older Hosts without these checks can still be fooled until they are updated. Windows 10 reaches TLS 1.2 only. The cipher suites are the operating system's defaults. |
| **Replay, reflection or alteration of messages (T).** | Fresh random values per connection; every frame carries a MAC that covers its direction and sequence number. | None known in the design. Fuzzing and a formal model are planned work. |
| **A stolen or copied key (S, E).** Someone reads `client.json` or the Host profile, or a former staff member keeps a key. | Roles: a Teacher key cannot make system changes. Key quality rules. A removed key is refused at the next request. Client secret files are readable by SYSTEM and administrators only, set on every start. | The lab key is shared by a room: whoever holds it controls the room. Per-computer keys exist but are not applied to every computer by default. A local administrator can read the Client's files (accepted). Named keys can carry an expiry and limits; the shared lab key stays valid until it is rotated. |
| **A forged or swapped approval (S).** One computer poses as another after an address change. | Approvals are tied to the network card; the first approval needs a person to read the certificate fingerprint on the student PC; a tool finds clashes. | The first approval depends on a person. A network card's address is an identifier, not a secret, and a local administrator can fake it. |
| **A forged enrolment file or announcement (S, T).** | The enrolment file is authenticated with the lab key, valid 30 days, and never replaces an existing approval or adds a computer. Announcements are signed, bound to a fresh question, accepted only while no Host has connected and at most 50 per scan. | Anyone holding the lab key can make a valid file. A file left on a USB stick is valid for 30 days. |
| **Flooding before authentication (D).** | Limits on connections in setup, per source and in total, a growing wait after failed attempts, and a short setup deadline. | Not measured on a real PC. Many source addresses are bounded by the total cap, not stopped. |
| **Abusing discovery (D, I).** A forged question makes every Client reply to a victim; anyone can learn names and addresses. | Rate limits per source and in total; optional restriction to the local subnet; authenticated minimal discovery in current Hosts and Clients. | While the old compatibility question is allowed (the default for one release) a Client still answers it, rationed. The names and the MAC address of a PC are visible on the local network. |
| **A tampered update (T, E).** | The update must be signed by the approved signer, its content hash must match the bytes uploaded, and a probe starts the new program before it replaces the old. | If the approved signing key were stolen, the attacker could sign updates. A successful replacement that then fails later does not restore itself automatically. |
| **Another local user poses as the helper (S, E).** | The pipe is access-controlled and the Windows session must match. | Checks are covered by source tests; live checks on real service and session combinations are still to be done. |
| **File access outside the permitted folders (T, I, E).** | Permitted roots are normalised; uploads sit in per-Host staging with ownership checks. | Containment is by path, not by the final file the path resolves to, so links in the file system are not fully excluded. The default root (`C:\Users`) is broad; narrow it. |
| **Over-broad teacher power (I, E).** A Teacher can read permitted folders and launch programs. | Roles and permitted roots. | There is no per-pupil purpose check. Decide at the school what a teacher may do, and tell students. |
| **Arbitrary execution by an Admin (T, E).** | An Admin can run any command by design; some dangerous commands are refused on the teacher's own PC. | The refusal is not complete. Admin keys are the most sensitive thing in the system. |
| **A key removed while a PC is offline (E).** | The Client refuses a removed key at the next request; the Host can finish changes later. | An offline PC keeps the old key until it is reached. Older Clients keep an existing connection open. |
| **An incident cannot be reconstructed (R).** | Each Client keeps a hash-chained security-event log (key use, approvals, refusals) with counters, checked by Client Health. | The last lines can be removed without a trace from the file alone; a local administrator can rewrite the whole chain. The Host does not yet record which named key did what. |
| **Logs or reports leak data (I).** | Redaction before a report is shared; secrets are not logged. | Error text and older logs can contain sensitive content. Review a report before sending it. |
| **A lost or tampered deployment kit (I, E).** | Key quality checks; checksums and signature checks when the kit has them. | A kit with a key in it is a secret and must be stored like one. A writable kit share can be replaced with something malicious. |

## Accepted risks

- A local administrator on a Client controls that Client.
- The first approval of a computer depends on a person comparing a fingerprint.
- The lab key is shared by a room until per-computer and scoped keys are used.
- Discovery answers anyone on the local network.
- The Host's profile holds the keys of the labs it manages; compromise of that Windows account is compromise of those labs.

## What this model does not claim

The software is not a network filter or an application-control product. Blocking websites edits the hosts file; blocking programs polls process names. They support classroom routine and do not stop a determined student. Use the school's filtering and Windows application control for enforcement.

## How this stays true

A security requirement is added with its test in the same change. Tests fail when a test that a requirement names is renamed or removed. Controls marked as not yet verified on a real deployment are moved to "verified" only after they have been checked there. Report a weakness you find to the address in [SECURITY.md](../SECURITY.md); if the model above is wrong or out of date, that is a bug too.

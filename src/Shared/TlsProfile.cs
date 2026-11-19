// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace ClassroomManager.Shared;

/// <summary>
/// The TLS settings every part of Classroom Manager uses (T-186): TLS 1.2 and 1.3 only, named explicitly instead of "let the
/// operating system choose" so that a computer with older protocols still switched on cannot be talked down to them (RFC 8996:
/// TLS 1.0 and 1.1 are retired). Windows 11 and Server 2022 reach 1.3; Windows 10 reaches 1.2. The cipher suites stay the
/// operating system's own: Windows offers no per-connection suite list, and its defaults for these two protocols are AEAD with
/// forward secrecy.
/// </summary>
public static class TlsProfile
{
    public const SslProtocols Protocols = SslProtocols.Tls12 | SslProtocols.Tls13;

    public static SslServerAuthenticationOptions Server(X509Certificate2 certificate) => new()
    {
        ServerCertificate = certificate,
        ClientCertificateRequired = false,
        EnabledSslProtocols = Protocols,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
    };

    public static SslClientAuthenticationOptions Client(string targetHost) => new()
    {
        TargetHost = targetHost,
        EnabledSslProtocols = Protocols,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
    };
}

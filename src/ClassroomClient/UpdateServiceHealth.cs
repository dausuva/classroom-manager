// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>Private, authenticated readiness probe used while a signed update is being applied.</summary>
static class UpdateServiceHealth
{
    public const string Switch = "--verify-update-service";

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length != 3 || !Version.TryParse(args[1], out var expectedVersion) ||
                args[2].Length != 64 || !args[2].All(Uri.IsHexDigit)) return 2;

            var target = Path.GetFullPath(args[0]);
            if (!File.Exists(target) || !Version.TryParse(FileVersionInfo.GetVersionInfo(target).FileVersion, out var fileVersion) ||
                fileVersion != expectedVersion)
                return 3;
            await using (var executable = File.OpenRead(target))
                if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(executable)), args[2], StringComparison.OrdinalIgnoreCase)) return 3;

            var serviceProcesses = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(target));
            var serviceImageFound = false;
            foreach (var process in serviceProcesses)
            {
                using (process)
                {
                    try
                    {
                        if (process.SessionId == 0 && process.MainModule?.FileName is { } image &&
                            Path.GetFullPath(image).Equals(target, StringComparison.OrdinalIgnoreCase))
                            serviceImageFound = true;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or UnauthorizedAccessException) { }
                }
            }
            if (!serviceImageFound) return 4;

            var configText = await File.ReadAllTextAsync(ClientConfig.ConfigPath);
            var config = ClientConfig.Parse(configText);
            if (config is null || string.IsNullOrWhiteSpace(config.Psk) || config.TcpPort is < 1 or > 65535) return 5;

            var certificatePath = Path.Combine(ClientConfig.DataDir, "tls-cert.pfx");
            if (!File.Exists(certificatePath)) return 6;
            using var certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, null,
                X509KeyStorageFlags.EphemeralKeySet);
            var expectedCertificate = SHA256.HashData(certificate.RawData);

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync("127.0.0.1", config.TcpPort, deadline.Token);
            using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false,
                (_, remote, _, _) => MatchesCertificate(remote, expectedCertificate));
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = TlsProfile.Protocols,
            }, deadline.Token);

            using var channel = await SecureChannel.ConnectAsync(ssl, Encoding.UTF8.GetBytes(config.Psk), deadline.Token,
                SecureChannel.BindingOf(ssl));
            await channel.SendJsonAsync(new Request { Cmd = Cmd.Ping, Level = Levels.System, Args = new JsonObject() }, deadline.Token);
            var response = await channel.ReceiveJsonAsync<Response>(deadline.Token);
            return Accepts(response, Environment.MachineName, config.ClientId, expectedVersion.ToString()) ? 0 : 7;
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or CryptographicException or
                                   System.ComponentModel.Win32Exception or InvalidOperationException or System.Text.Json.JsonException or
                                   ArgumentException or TimeoutException or OperationCanceledException)
        {
            return 1;
        }
    }

    internal static bool Accepts(Response response, string machineName, string clientId, string version) =>
        response.Ok &&
        response.Data["machineName"]?.GetValue<string>() == machineName &&
        response.Data["clientId"]?.GetValue<string>() == clientId &&
        response.Data["appVersion"]?.GetValue<string>() == version;

    internal static bool MatchesCertificate(X509Certificate? remote, ReadOnlySpan<byte> expectedSha256) =>
        remote is not null && expectedSha256.Length == 32 &&
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(remote.GetRawCertData()), expectedSha256);
}

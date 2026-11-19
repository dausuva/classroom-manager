// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace ClassroomManager.Shared;

/// <summary>
/// Which processor a Windows program was built for, read from its PE header (the "machine" field), without running it. The Client uses it to
/// refuse an update built for another architecture: an x64 build can never start on 32-bit Windows, and an x86 build on 64-bit Windows would
/// see the registry and System32 through the 32-bit redirection and list the wrong programs.
/// </summary>
public static class PeArchitecture
{
    public const string X86 = "x86";
    public const string X64 = "x64";
    public const string Arm64 = "arm64";
    public const string Unknown = "unknown";

    public static string Read(Stream stream)
    {
        Span<byte> dos = stackalloc byte[64];
        if (stream.Read(dos) != 64 || dos[0] != 'M' || dos[1] != 'Z') return Unknown;
        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(dos[0x3C..]);
        if (peOffset < 64 || peOffset > 4096) return Unknown;
        stream.Seek(peOffset, SeekOrigin.Begin);
        Span<byte> header = stackalloc byte[6];
        if (stream.Read(header) != 6 || header[0] != 'P' || header[1] != 'E' || header[2] != 0 || header[3] != 0) return Unknown;
        return BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) switch
        {
            0x014C => X86,
            0x8664 => X64,
            0xAA64 => Arm64,
            _ => Unknown,
        };
    }

    public static string Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    /// <summary>The architecture of the running process.</summary>
    public static string Current => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X86 => X86,
        Architecture.X64 => X64,
        Architecture.Arm64 => Arm64,
        _ => Unknown,
    };

    /// <summary>Null when the update may replace the running program; otherwise why it may not.</summary>
    public static string? RefuseUpdate(string running, string uploaded)
    {
        if (uploaded == Unknown) return null;            // not a build we can tell; the start-up check decides
        if (running == uploaded || running == Unknown) return null;
        return uploaded switch
        {
            X64 when running == X86 => "The uploaded build is for 64-bit Windows (x64) but this computer runs the 32-bit (x86) client. Push the win-x86 build to this computer.",
            X86 when running == X64 => "The uploaded build is for 32-bit Windows (x86) but this computer runs the 64-bit (x64) client. Push the win-x64 build to this computer.",
            _ => $"The uploaded build is for {uploaded} but this computer runs the {running} client. Push the build for {running}.",
        };
    }
}

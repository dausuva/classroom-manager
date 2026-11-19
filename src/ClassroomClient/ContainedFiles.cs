// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ClassroomManager.Client;

/// <summary>File operations anchored to no-follow directory handles, never reopened by checked path.</summary>
sealed class ContainedFiles(IEnumerable<string> roots)
{
    readonly string[] _roots = roots.Select(Canonical).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public IReadOnlyList<string> Roots => _roots;

    public string Resolve(string path)
    {
        var full = Canonical(path);
        var root = _roots.Where(root => Within(full, root)).OrderByDescending(root => root.Length).FirstOrDefault();
        if (root is null) throw new UnauthorizedAccessException("Path is outside the allowed folders.");
        // Use configured ancestor spelling even on case-sensitive Windows directories.
        var prefix = root.TrimEnd('\\');
        return full.Equals(root, StringComparison.OrdinalIgnoreCase) ? root : prefix + full[prefix.Length..];
    }

    static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    static string Canonical(string path)
    {
        path = ClassroomManager.Shared.PathText.Clean(path).Replace('/', '\\');
        var drive = path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';
        var unc = path.StartsWith(@"\\") && !path.StartsWith(@"\\?\") && !path.StartsWith(@"\\.\") &&
            path[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries).Length >= 2;
        if ((!drive && !unc) || path[(drive ? 2 : 0)..].Contains(':'))
            throw new UnauthorizedAccessException("File operations require a drive or UNC path without alternate streams or device prefixes.");
        foreach (var name in path[(drive ? 3 : 2)..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (name is "." or "..") continue; // GetFullPath resolves these before the root check.
            var stem = name.Split('.')[0].ToUpperInvariant();
            if (name.EndsWith(' ') || name.EndsWith('.') || name.IndexOfAny(['<', '>', '|', '"', '*', '?', '\0']) >= 0 ||
                stem is "CON" or "PRN" or "AUX" or "NUL" ||
                stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9')
                throw new UnauthorizedAccessException("Unsupported filesystem name.");
        }
        var full = Path.GetFullPath(path);
        return full.Equals(Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase) ? full : full.TrimEnd('\\');
    }

    public Lease Directory(string path, bool create = false)
    {
        var full = Resolve(path);
        var lease = new Lease(full);
        try
        {
            var root = Path.GetPathRoot(full)!;
            lease.Add(OpenDrive(root));
            var current = root;
            foreach (var part in full[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                var mayCreate = create && _roots.Any(root => Within(current, root));
                lease.Add(OpenChild(lease.Handle, part, ReadAttributes | ListDirectory | Synchronize,
                    directory: true, mayCreate ? 3u : 1u));
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    public Lease File(string path, bool write, bool create)
    {
        var full = Resolve(path);
        if (_roots.Contains(full, StringComparer.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Refusing to open a root as a file.");
        var lease = Directory(Path.GetDirectoryName(full)!, create);
        try
        {
            lease.Add(OpenChild(lease.Handle, Path.GetFileName(full), ReadAttributes | Synchronize | (write ? GenericWrite : GenericRead),
                directory: false, create ? 3u : 1u, share: write ? 0u : 3u));
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    public void Remove(string path)
    {
        var full = Resolve(path);
        RefuseRoot(full);
        using var parent = Directory(Path.GetDirectoryName(full)!);
        using var leaf = OpenChild(parent.Handle, Path.GetFileName(full), ReadAttributes | ListDirectory | DeleteAccess | Synchronize, null, 1);
        Remove(leaf);
    }

    static void Remove(SafeFileHandle handle)
    {
        if ((Info(handle).Attributes & 0x10) != 0)
            foreach (var entry in Entries(handle))
            {
                using var child = OpenChild(handle, entry.Name, ReadAttributes | ListDirectory | DeleteAccess | Synchronize, null, 1);
                Remove(child);
            }
        var info = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(info, 1);
            if (!SetFileInformationByHandle(handle, 4, info, 1)) ThrowWin32(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(info); }
    }

    public void Rename(string from, string to)
    {
        from = Resolve(from); to = Resolve(to);
        RefuseRoot(from); RefuseRoot(to);
        using var source = Directory(Path.GetDirectoryName(from)!);
        using var destination = Directory(Path.GetDirectoryName(to)!);
        using var leaf = OpenChild(source.Handle, Path.GetFileName(from), ReadAttributes | DeleteAccess | Synchronize, null, 1);
        var name = Encoding.Unicode.GetBytes(Path.GetFileName(to));
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + 4;
        var buffer = Marshal.AllocHGlobal(nameOffset + name.Length + 2);
        try
        {
            Marshal.Copy(new byte[nameOffset + name.Length + 2], 0, buffer, nameOffset + name.Length + 2);
            Marshal.WriteIntPtr(buffer, rootOffset, destination.Handle.DangerousGetHandle());
            Marshal.WriteInt32(buffer, lengthOffset, name.Length);
            Marshal.Copy(name, 0, buffer + nameOffset, name.Length);
            var status = NtSetInformationFile(leaf, out _, buffer, (uint)(nameOffset + name.Length), 10);
            if (status < 0) ThrowWin32((int)RtlNtStatusToDosError(status));
            GC.KeepAlive(destination);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    void RefuseRoot(string path)
    {
        if (_roots.Contains(path, StringComparer.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Refusing to delete or rename a root folder.");
    }

    public sealed record Entry(string Name, bool IsDirectory, long Size, DateTime Modified);
    public static IReadOnlyList<Entry> Entries(SafeFileHandle directory)
    {
        const int capacity = 65536;
        var buffer = Marshal.AllocHGlobal(capacity);
        var entries = new List<Entry>();
        try
        {
            var first = true;
            while (true)
            {
                var status = NtQueryDirectoryFile(directory, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var io,
                    buffer, capacity, 1, false, IntPtr.Zero, first);
                first = false;
                if (status == unchecked((int)0x80000006)) return entries; // STATUS_NO_MORE_FILES
                if (status < 0) ThrowWin32((int)RtlNtStatusToDosError(status));
                var used = checked((int)io.Information);
                var offset = 0;
                while (true)
                {
                    if (used - offset < 64) throw new IOException("Invalid directory response.");
                    var next = Marshal.ReadInt32(buffer, offset);
                    var nameBytes = Marshal.ReadInt32(buffer, offset + 60);
                    if (nameBytes < 0 || (nameBytes & 1) != 0 || nameBytes > used - offset - 64) throw new IOException("Invalid directory entry.");
                    var name = Marshal.PtrToStringUni(buffer + offset + 64, nameBytes / 2)!;
                    if (name is not "." and not "..") entries.Add(new(name, (Marshal.ReadInt32(buffer, offset + 56) & 0x10) != 0,
                        Marshal.ReadInt64(buffer, offset + 40), DateTime.FromFileTime(Marshal.ReadInt64(buffer, offset + 24))));
                    if (next == 0) break;
                    if (next < 64 || next > used - offset - 64) throw new IOException("Invalid directory offset.");
                    offset += next;
                }
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public sealed class Lease(string path) : IDisposable
    {
        readonly List<SafeFileHandle> _handles = [];
        public string Path { get; } = path;
        public SafeFileHandle Handle => _handles[^1];
        internal void Add(SafeFileHandle handle) => _handles.Add(handle);
        public void Dispose() { for (var i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose(); _handles.Clear(); }
    }

    const uint ReadAttributes = 0x80, ListDirectory = 1, Synchronize = 0x100000, DeleteAccess = 0x10000;
    const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;

    static SafeFileHandle OpenDrive(string path)
    {
        var handle = CreateFileW(path, ReadAttributes | ListDirectory | Synchronize, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); ThrowWin32(error); }
        try { Check(handle, true); return handle; }
        catch { handle.Dispose(); throw; }
    }

    static SafeFileHandle OpenChild(SafeFileHandle parent, string name, uint access, bool? directory, uint disposition, uint share = 3)
    {
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(['\\', '/', ':', '\0']) >= 0)
            throw new UnauthorizedAccessException("Expected a single filesystem component.");
        var nameBuffer = Marshal.StringToHGlobalUni(name);
        var unicodeBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            Marshal.StructureToPtr(new UnicodeString { Length = checked((ushort)(name.Length * 2)),
                MaximumLength = checked((ushort)((name.Length + 1) * 2)), Buffer = nameBuffer }, unicodeBuffer, false);
            var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>(), Root = parent.DangerousGetHandle(),
                Name = unicodeBuffer, Attributes = 0x40 };
            var options = 0x00200020u | (directory == true ? 1u : directory == false ? 0x40u : 0u);
            var status = NtCreateFile(out var handle, access, ref attributes, out _, IntPtr.Zero, 0x80, share, disposition, options, IntPtr.Zero, 0);
            GC.KeepAlive(parent);
            if (status < 0) { handle?.Dispose(); ThrowWin32((int)RtlNtStatusToDosError(status)); }
            try { Check(handle!, directory); return handle!; }
            catch { handle!.Dispose(); throw; }
        }
        finally { Marshal.FreeHGlobal(unicodeBuffer); Marshal.FreeHGlobal(nameBuffer); }
    }

    static void Check(SafeFileHandle handle, bool? directory)
    {
        var info = Info(handle);
        var isDirectory = (info.Attributes & 0x10) != 0;
        if ((info.Attributes & 0x400) != 0 || !isDirectory && info.Links != 1)
            throw new UnauthorizedAccessException("Reparse points and multiply linked files are not permitted.");
        if (directory is { } expected && isDirectory != expected) throw new IOException("Unexpected filesystem object type.");
    }

    static FileInformation Info(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) ThrowWin32(Marshal.GetLastWin32Error());
        return info;
    }

    static void ThrowWin32(int error)
    {
        var message = new Win32Exception(error).Message;
        throw error switch { 2 => new FileNotFoundException(message), 3 => new DirectoryNotFoundException(message),
            5 => new UnauthorizedAccessException(message), _ => new IOException(message, unchecked((int)0x80070000) | error) };
    }

    [StructLayout(LayoutKind.Sequential)] struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] struct ObjectAttributes { public int Length; public IntPtr Root, Name; public uint Attributes; public IntPtr Security, Quality; }
    [StructLayout(LayoutKind.Sequential)] struct IoStatus { public IntPtr Status; public nuint Information; }
    [StructLayout(LayoutKind.Sequential)] struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetFileInformationByHandle(SafeFileHandle handle, int type, IntPtr info, uint size);
    [DllImport("ntdll.dll")] static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes, out IoStatus status, IntPtr allocation, uint fileAttributes, uint share, uint disposition, uint options, IntPtr ea, uint eaLength);
    [DllImport("ntdll.dll")] static extern uint RtlNtStatusToDosError(int status);
    [DllImport("ntdll.dll")] static extern int NtSetInformationFile(SafeFileHandle handle, out IoStatus status, IntPtr buffer, uint length, int type);
    [DllImport("ntdll.dll")] static extern int NtQueryDirectoryFile(SafeFileHandle handle, IntPtr waitEvent, IntPtr apc, IntPtr context, out IoStatus status, IntPtr buffer, int length, int type, [MarshalAs(UnmanagedType.U1)] bool single, IntPtr name, [MarshalAs(UnmanagedType.U1)] bool restart);
}

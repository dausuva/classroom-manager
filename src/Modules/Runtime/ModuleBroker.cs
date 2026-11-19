// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.RegularExpressions;

namespace ClassroomManager.Modules;

/// <summary>Builds the ports a module may use: exactly the ones in its descriptor, each limited by this Host's rules.</summary>
public static class ModuleBroker
{
    /// <param name="descriptor">What the module asked for.</param>
    /// <param name="dataRoot">Gives the Host's data folder (asked each time, so a test can point it elsewhere); a module's private folder is <c>modules\&lt;id&gt;\data</c> below it.</param>
    /// <param name="writeLog">Receives each accepted log line, already marked with the module id.</param>
    /// <param name="time">The clock a module gets.</param>
    public static ModuleServices Grant(ModuleDescriptor descriptor, Func<string> dataRoot, Action<string> writeLog, TimeProvider time)
    {
        var wanted = descriptor.Ports.Select(port => port.Name).ToHashSet(StringComparer.Ordinal);
        return new ModuleServices(
            wanted.Contains(ModulePorts.Log) ? new ModuleLog(descriptor.Id, writeLog, time) : null,
            wanted.Contains(ModulePorts.Time) ? time : null,
            wanted.Contains(ModulePorts.StoragePrivate) ? new PrivateStorage(() => Path.Combine(dataRoot(), "modules", descriptor.Id, "data")) : null);
    }

    sealed class ModuleLog(string moduleId, Action<string> sink, TimeProvider time) : IModuleLog
    {
        readonly object _gate = new();
        readonly Queue<DateTimeOffset> _recent = new();

        public void Write(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            var now = time.GetUtcNow();
            lock (_gate)
            {
                while (_recent.Count > 0 && now - _recent.Peek() >= TimeSpan.FromMinutes(1)) _recent.Dequeue();
                if (_recent.Count >= ModulePorts.MaxLogLinesPerMinute) return;
                _recent.Enqueue(now);
            }
            var line = new string(message.Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Trim();
            if (line.Length > ModulePorts.MaxLogLineLength) line = line[..ModulePorts.MaxLogLineLength];
            sink($"[{moduleId}] {line}");
        }
    }

    sealed class PrivateStorage(Func<string> folderOf) : IModulePrivateStorage
    {
        string folder => folderOf();
        static readonly Regex NamePattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        const string TemporaryMark = ".tmp-";
        readonly object _gate = new();

        public long UsedBytes { get { lock (_gate) return Files().Sum(file => file.Length); } }

        public IReadOnlyList<string> List() { lock (_gate) return Files().Select(file => file.Name).Order(StringComparer.OrdinalIgnoreCase).ToArray(); }

        public byte[]? Read(string name)
        {
            lock (_gate)
            {
                var path = PathOf(name);
                if (!File.Exists(path)) return null;
                if (IsLink(path)) throw new ModulePortException("The file is a link.");
                return File.ReadAllBytes(path);
            }
        }

        public void Write(string name, byte[] content)
        {
            ArgumentNullException.ThrowIfNull(content);
            lock (_gate)
            {
                var path = PathOf(name);
                if (content.LongLength > ModulePorts.StorageMaxFileBytes) throw new ModulePortException($"A file may hold at most {ModulePorts.StorageMaxFileBytes} bytes.");
                var existing = File.Exists(path) ? new FileInfo(path) : null;
                if (existing is not null && IsLink(path)) throw new ModulePortException("The file is a link.");
                var files = Files().ToList();
                if (existing is null && files.Count >= ModulePorts.StorageMaxFiles) throw new ModulePortException($"At most {ModulePorts.StorageMaxFiles} files may be kept.");
                if (files.Sum(file => file.Length) - (existing?.Length ?? 0) + content.LongLength > ModulePorts.StorageQuotaBytes)
                    throw new ModulePortException($"The private storage is limited to {ModulePorts.StorageQuotaBytes} bytes.");
                var temporary = path + TemporaryMark + Guid.NewGuid().ToString("N");
                File.WriteAllBytes(temporary, content);
                File.Move(temporary, path, overwrite: true);
            }
        }

        public bool Delete(string name)
        {
            lock (_gate)
            {
                var path = PathOf(name);
                if (!File.Exists(path)) return false;
                File.Delete(path);
                return true;
            }
        }

        string PathOf(string name)
        {
            if (name is null || !NamePattern.IsMatch(name) || name.EndsWith('.') || name.Contains(TemporaryMark, StringComparison.Ordinal))
                throw new ModulePortException("A file name is letters, digits, dot, hyphen and underscore, at most 64 characters; it is never a path.");
            Directory.CreateDirectory(folder);
            if (IsLink(folder)) throw new ModulePortException("The module's folder is a link.");
            return Path.Combine(folder, name);
        }

        IEnumerable<FileInfo> Files() => Directory.Exists(folder)
            ? new DirectoryInfo(folder).EnumerateFiles().Where(file => !file.Name.Contains(TemporaryMark, StringComparison.Ordinal)) : [];

        static bool IsLink(string path) => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
    }
}

// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

public enum EnrolmentAction { Enrol, AlreadyApproved, DifferentApproval, NoSavedComputer, Rejected }

/// <summary>What would happen to one enrolment file.</summary>
public sealed record EnrolmentLine(string Path, EnrolmentFile? File, SavedComputer? Computer, string Card, EnrolmentAction Action, string Detail);

/// <summary>
/// Approves certificates from enrolment files written on the student computers (Classroom Client Health,
/// <c>--export-enrolment</c>). The fingerprint was read from the computer's own protected file and the file carries
/// a tag made with the lab key, so nothing is fetched from the network and nobody has to type digits. A file never
/// replaces an existing approval, never adds a computer and is refused when it is old or its tag does not verify.
/// </summary>
public static class EnrolmentImport
{
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(30);

    public static IEnumerable<string> Keys(HostSettings settings) =>
        settings.CurrentHostKeys().Select(key => key.Psk).Concat(settings.PreviousPsks).Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.Ordinal);

    /// <summary>Reads every <c>*.json</c> file of a folder (or one file) and decides what each one means for the saved computers.</summary>
    public static List<EnrolmentLine> Plan(HostSettings settings, string folderOrFile, DateTime nowUtc, TimeSpan? maxAge = null)
    {
        var files = Directory.Exists(folderOrFile)
            ? Directory.EnumerateFiles(folderOrFile, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase).ToList()
            : File.Exists(folderOrFile) ? [folderOrFile] : throw new CliUsageException($"Not found: {folderOrFile}");
        var keys = Keys(settings).ToList();
        var lines = new List<EnrolmentLine>();
        foreach (var path in files)
        {
            string json;
            try { json = File.ReadAllText(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { lines.Add(Refused(path, "Could not be read: " + ex.Message)); continue; }
            if (!EnrolmentFile.TryParse(json, out var file, out var error)) { lines.Add(Refused(path, error)); continue; }
            lines.Add(Decide(settings, path, file, keys, nowUtc, maxAge ?? DefaultMaxAge));
        }
        // Two files for one computer: the newer one decides; the older is shown as superseded.
        foreach (var group in lines.Where(line => line.Action == EnrolmentAction.Enrol && line.Computer is not null).GroupBy(line => line.Computer).Where(group => group.Count() > 1).ToList())
            foreach (var older in group.OrderByDescending(line => line.File!.Created).Skip(1))
                lines[lines.IndexOf(older)] = older with { Action = EnrolmentAction.Rejected, Detail = "Superseded by a newer file for the same computer." };
        return lines;
    }

    static EnrolmentLine Refused(string path, string detail) => new(path, null, null, "", EnrolmentAction.Rejected, detail);

    static EnrolmentLine Decide(HostSettings settings, string path, EnrolmentFile file, List<string> keys, DateTime nowUtc, TimeSpan maxAge)
    {
        if (!file.Verify(keys))
            return new(path, file, null, "", EnrolmentAction.Rejected, "The tag does not match any key of this lab: the file was not made with this lab's key, or was changed.");
        if (nowUtc - file.Created > maxAge)
            return new(path, file, null, "", EnrolmentAction.Rejected, $"Made {file.Created:yyyy-MM-dd}, more than {maxAge.TotalDays:0} days ago. Make a new one on the computer.");
        if (file.Created - nowUtc > TimeSpan.FromHours(1))
            return new(path, file, null, "", EnrolmentAction.Rejected, "Dated in the future: the computer's clock is wrong.");

        // The computer is recognised by its card; the client id is used only for a saved computer whose card is not known yet.
        var byCard = settings.Computers.Where(c => ComputerIdentity.NormalizeMac(c.Mac) is { Length: > 0 } card && file.Cards.Contains(card)).ToList();
        var matches = byCard.Count > 0 ? byCard
            : settings.Computers.Where(c => ComputerIdentity.NormalizeMac(c.Mac).Length == 0 && c.ClientId.Length > 0 && c.ClientId.Equals(file.ClientId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
            return new(path, file, null, "", EnrolmentAction.NoSavedComputer,
                $"{file.MachineName}: no saved computer has its card address ({string.Join(", ", file.Cards.Select(ComputerIdentity.FormatMac))}) or client id. Add or scan it first.");
        if (matches.Count > 1)
            return new(path, file, null, "", EnrolmentAction.Rejected,
                $"{file.MachineName}: more than one saved computer matches ({string.Join(", ", matches.Select(c => c.Name))}); nothing is guessed.");

        var computer = matches[0];
        var card = ComputerIdentity.NormalizeMac(computer.Mac);
        var port = computer.Port > 0 ? computer.Port : settings.TcpPort;
        if (settings.TlsPins(computer.Address, port, card).Contains(file.CertificateSha256, StringComparer.OrdinalIgnoreCase))
            return new(path, file, computer, card, EnrolmentAction.AlreadyApproved, $"{computer.Name}: already approved with this certificate.");
        if (settings.HasTrustEntry(computer.Address, port, card))
            return new(path, file, computer, card, EnrolmentAction.DifferentApproval,
                $"{computer.Name}: a different certificate is already approved. An enrolment file never replaces an approval; if the computer's certificate was replaced on purpose, " +
                $"clear the old one with ClassroomCli trust --computer \"{computer.Name}\" --clear --yes first.");
        return new(path, file, computer, card, EnrolmentAction.Enrol,
            $"{computer.Name} ({computer.Address}): approve {file.CertificateSha256[..16]}... for card {(card.Length > 0 ? ComputerIdentity.FormatMac(card) : "(not known yet)")}.");
    }

    /// <summary>Makes the approvals of the lines that say <see cref="EnrolmentAction.Enrol"/>. Returns how many.</summary>
    public static int Apply(HostSettings settings, IEnumerable<EnrolmentLine> lines)
    {
        var done = 0;
        foreach (var line in lines.Where(line => line.Action == EnrolmentAction.Enrol && line.Computer is not null && line.File is not null))
        {
            var port = line.Computer!.Port > 0 ? line.Computer.Port : settings.TcpPort;
            settings.EnrollTrust(line.Computer.Address, port, line.Card, [line.File!.CertificateSha256]);
            done++;
        }
        return done;
    }

    public static string Text(IReadOnlyList<EnrolmentLine> lines)
    {
        var text = new StringBuilder();
        foreach (var line in lines)
            text.AppendLine($"[{Label(line.Action),-8}] {Path.GetFileName(line.Path)}: {line.Detail}");
        text.AppendLine().Append($"{lines.Count(l => l.Action == EnrolmentAction.Enrol)} to approve, {lines.Count(l => l.Action == EnrolmentAction.AlreadyApproved)} already approved, " +
                                 $"{lines.Count(l => l.Action is EnrolmentAction.DifferentApproval or EnrolmentAction.NoSavedComputer or EnrolmentAction.Rejected)} not used.");
        return text.ToString().TrimEnd();
    }

    static string Label(EnrolmentAction action) => action switch
    {
        EnrolmentAction.Enrol => "approve", EnrolmentAction.AlreadyApproved => "ok", EnrolmentAction.DifferentApproval => "differs",
        EnrolmentAction.NoSavedComputer => "unknown", _ => "refused",
    };
}

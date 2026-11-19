// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>
/// The school's licence on this Host (T-213). It is shown, never enforced: an ended or missing licence changes nothing in what the Host
/// does. It only decides whether the school keeps receiving signed builds, so after it ends new installs show Windows' prompts.
/// </summary>
public static class HostLicence
{
    /// <summary>Where the licence file is kept for this Windows user, with the Host's other files.</summary>
    public static string FilePath => Path.Combine(HostSettings.DataFolder, "licence" + Licences.FileExtension);

    /// <summary>
    /// The folder every Windows user of this PC reads (T-262), so a licence added by one user counts for all of them. Replaced by tests, so
    /// they never touch the real one.
    /// </summary>
    public static string SharedFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Dausuva", "ClassroomManager");

    /// <summary>The PC-wide copy of the licence file.</summary>
    public static string SharedFilePath => Path.Combine(SharedFolder, "licence" + Licences.FileExtension);

    /// <summary>The licence on this Host, checked on <paramref name="today"/> for this computer (<see cref="HostComputerId.Current"/> unless given).</summary>
    public static LicenceCheck Read(DateOnly today, IReadOnlyDictionary<string, byte[]>? keys = null, string? computerId = null)
    {
        keys ??= Licences.TrustedKeys();
        computerId ??= HostComputerId.Current;
        var own = Licences.Check(ReadFile(FilePath), keys, today, computerId);
        if (own.Signed) return own;
        // Nothing usable in this user's folder (no file, or a copy meant for another computer): the PC-wide file may be the right one.
        var shared = Licences.Check(ReadFile(SharedFilePath), keys, today, computerId);
        return shared.State is LicenceState.Missing ? own : own.State is LicenceState.Missing || shared.Signed ? shared : own;
    }

    static string? ReadFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    /// <summary>
    /// Keeps a licence file the school was given, for this Windows user and, when this user may write there, for every user of the PC. A damaged
    /// or changed file is refused, and so is one issued for another computer; one signed with a key this build does not know is
    /// kept, so it counts once the Host is updated. Returns the check of the file, or throws <see cref="InvalidDataException"/> with the reason.
    /// </summary>
    public static LicenceCheck Install(string source, DateOnly today, IReadOnlyDictionary<string, byte[]>? keys = null, string? computerId = null)
    {
        computerId ??= HostComputerId.Current;
        var text = File.ReadAllText(source);
        var check = Licences.Check(text, keys ?? Licences.TrustedKeys(), today, computerId);
        if (check.WrongComputer)
            throw new InvalidDataException(WrongComputerText(check, computerId));
        if (check.State is LicenceState.Missing || check.State is LicenceState.Invalid && !check.KeyUnknown)
            throw new InvalidDataException(check.Problem.Length > 0 ? check.Problem : "The file is empty.");
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        AtomicFile.WriteAllText(FilePath, text);
        try
        {
            Directory.CreateDirectory(SharedFolder);
            AtomicFile.WriteAllText(SharedFilePath, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* only this Windows user has it; the others add it themselves */ }
        return check;
    }

    /// <summary>Which of several licence files (such as the ones in a school's kit) is for this computer: the first that installs, or null when none is.</summary>
    public static (string? Installed, LicenceCheck? Check, string Problem) InstallFirstMatching(IEnumerable<string> sources, DateOnly today,
        IReadOnlyDictionary<string, byte[]>? keys = null, string? computerId = null)
    {
        var problem = "";
        foreach (var source in sources)
        {
            try { return (source, Install(source, today, keys, computerId), ""); }
            catch (InvalidDataException ex) { if (problem.Length == 0) problem = ex.Message; }
        }
        return (null, null, problem.Length > 0 ? problem : "No file was chosen.");
    }

    internal static string WrongComputerText(LicenceCheck check, string computerId)
    {
        var terms = check.Terms!;
        var named = terms.HostName.Length > 0 ? $" ({terms.HostName})" : "";
        return $"This licence was issued for another computer{named}, ID {terms.HostId}. This computer's ID is {(computerId.Length > 0 ? computerId : "unknown")}. " +
               "Ask for a licence for this computer, giving that ID.";
    }

    static string HostText(LicenceTerms terms) =>
        terms.HostId.Length == 0 ? "" : terms.HostName.Length > 0 ? $", for this computer ({terms.HostName})" : ", for this computer";

    /// <summary>A headline, a sentence or two about the licence, and a note when the Host lists more computers than it covers (or "").</summary>
    public static (string Headline, string Detail, string Note) Describe(LicenceCheck check, int computersListed, bool buildHasKeys, bool freeTier = false)
    {
        var terms = check.Terms;
        var (headline, detail) = check.State switch
        {
            LicenceState.Missing when freeTier => ("Free for up to 3 computers",
                "Without a licence file Classroom Manager works with up to 3 computers online at the same time. A school's licence file covers more; add it with Add licence file."),
            LicenceState.Missing => ("No licence file",
                "Classroom Manager works without one. A school's licence file comes with its kit; add it with Add licence file."),
            LicenceState.Invalid when check.KeyUnknown && !buildHasKeys => ("Licence not checked",
                "This version cannot check licences yet. The file is kept and is checked after an update."),
            LicenceState.Invalid when check.KeyUnknown => ("Licence from an unknown issuer",
                "The licence is not signed by Dausuva MB's key known to this version. Update the Host, or ask for a new licence file."),
            LicenceState.Invalid when check.WrongComputer => ("Licence is for another computer", WrongComputerText(check, HostComputerId.Current)),
            LicenceState.Invalid => ("Licence not valid", check.Problem),
            LicenceState.Active => ($"Licensed to {terms!.School}", $"{terms.Computers} computer(s), until {terms.Ends:yyyy-MM-dd} (licence {terms.Id}){HostText(terms)}."),
            LicenceState.EndingSoon => ($"Licensed to {terms!.School}",
                $"The licence ends on {terms.Ends:yyyy-MM-dd} ({check.DaysLeft} day(s) left). Renew it to keep receiving signed updates."),
            _ => ($"The licence ended on {terms!.Ends:yyyy-MM-dd}",
                "The licence does not disable classroom features; Classroom Manager keeps working. Expired signing certificates may cause Windows' untrusted-publisher prompts or rejected installs and updates. Install the renewed trust update and signed packages."),
        };
        var note = check.Signed && terms is not null && computersListed > terms.Computers
            ? $"This Host lists {computersListed} computers; the licence covers {terms.Computers}."
            : "";
        return (headline, detail, note);
    }
}

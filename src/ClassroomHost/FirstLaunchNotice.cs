// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

/// <summary>
/// The notice a new Host shows once, before the setup assistant (T-246): what this program is, that the student computers need the separate Client, where its
/// data is kept, what leaves the computer (nothing, except an optional daily look at GitHub for a newer version), and where the privacy information is.
/// Shown only for a Host that has no key and no computers yet, so an update never interrupts someone who already uses it.
/// </summary>
static class FirstLaunchNotice
{
    public const string PrivacyPage = "https://dausuva.com/school/privacy/";

    /// <summary>True for a Host with nothing in it that has not shown the notice before.</summary>
    public static bool IsDue(HostSettings settings, int computersListed) =>
        !settings.FirstLaunchNoticeShown && !settings.HasHostKeys && computersListed == 0;

    /// <summary>A packaged (Microsoft Store) Host is updated by Windows, so it never looks for a newer version itself.</summary>
    public static bool OffersUpdateCheck(bool packaged) => !packaged;

    public static IReadOnlyList<string> Paragraphs(string dataFolder, bool packaged)
    {
        var paragraphs = new List<string>
        {
            "This is the teacher console (the Host). It manages computers that run the separate Classroom Manager Client, which your school's administrator installs on each student computer.",
            $"Your settings and keys are kept on this computer, for your Windows account only, in {dataFolder}. The keys are protected by your Windows account.",
            "There is no cloud service. Classroom Manager does not look at, capture or record what is on a student's screen, and it sends nothing to its maker.",
            "The Host starts in the light theme. Change the theme and more in Settings > Interface settings, and the language in Settings > Languages.",
        };
        if (packaged) paragraphs.Add("Updates come with the package: Windows installs them.");
        return paragraphs;
    }
}

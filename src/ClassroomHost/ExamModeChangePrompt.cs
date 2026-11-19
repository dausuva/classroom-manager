// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

/// <summary>
/// What the teacher is asked, and told, when Exam mode is switched (T-253 phase 2): whether there is anything to switch on, and the confirmation. Which computers it
/// reaches and how it is applied are <see cref="ExamModeWorkflow"/> and <see cref="ExamModeScope"/>, as before.
/// </summary>
public static class ExamModeChangePrompt
{
    public const string NothingConfigured = "Configure at least one Exam mode website, app, or device control in Exam mode settings first.";
    public const string NoComputers = "No assigned computers are visible in the current room and network filter.";

    /// <summary>Switching on with no website, app or device control set up would change nothing.</summary>
    public static bool NothingToApply(bool enable, int websites, int apps, string deviceControls) => enable && websites == 0 && apps == 0 && deviceControls.Length == 0;

    public static string Message(bool enable, int computers, int websites, int apps, string deviceControls)
    {
        var details = enable
            ? $"This will add {websites} website restriction(s) and {apps} app restriction(s). Existing normal block lists remain separate." +
              (deviceControls.Length == 0 ? "" : $"\n\nDevice controls for the whole computer: {deviceControls}.")
            : "This removes only the Exam mode overlay, including its device controls; normal website and app block lists remain.";
        return (enable ? $"Enable Exam mode on {computers} visible assigned computer(s)?"
                       : $"Disable Exam mode on the {computers} computer(s) it was turned on for?") + $"\n\n{details}";
    }

    public static string Caption(bool enable)
    {
        var action = enable ? "Enable" : "Disable";
        return $"Confirm {action.ToLowerInvariant()} Exam mode";
    }
}

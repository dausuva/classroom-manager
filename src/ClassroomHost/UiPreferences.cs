// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Win32;

namespace ClassroomManager.Host;

public enum ButtonDisplayMode { IconsOnly, NamesOnly, IconsAndNames }
public enum AppTheme { System, Light, Dark }

/// <summary>
/// App-wide UI preferences (button display mode, theme). A singleton bound directly from XAML
/// (see App.xaml's Button style) so changing it live re-renders every button and window at once -
/// no need to touch each window individually. Persisted through HostSettings.
/// </summary>
public sealed class UiPreferences : INotifyPropertyChanged
{
    public static UiPreferences Instance { get; } = new();

    ButtonDisplayMode _display = ButtonDisplayMode.IconsAndNames;
    public ButtonDisplayMode Display
    {
        get => _display;
        set
        {
            if (_display == value) return;
            _display = value;
            OnChanged();
            OnChanged(nameof(IconVisibility));
            OnChanged(nameof(TextVisibility));
            OnChanged(nameof(IconMargin));
        }
    }

    public Visibility IconVisibility => Display == ButtonDisplayMode.NamesOnly ? Visibility.Collapsed : Visibility.Visible;
    public Visibility TextVisibility => Display == ButtonDisplayMode.IconsOnly ? Visibility.Collapsed : Visibility.Visible;
    /// <summary>No gap after the icon when the text next to it is hidden (icons-only mode).</summary>
    public Thickness IconMargin => Display == ButtonDisplayMode.IconsOnly ? new Thickness(0) : new Thickness(0, 0, 6, 0);

    /// <summary>How many times a window has been opened before shows its explanation in full; after that it folds behind an info icon.</summary>
    public const int ExplanationShownTimes = 3;

    /// <summary>Keep every window's explanation open (Interface settings), instead of folding it after the first few times.</summary>
    public bool AlwaysShowExplanations { get; set; }

    /// <summary>
    /// Counts one opening of a window (by its type name) and returns how many there were before. Set by the main window, which keeps the
    /// counts in its settings; null where nothing is counted (tests), so every explanation shows in full.
    /// </summary>
    public Func<string, int>? CountOpening { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Swaps the app's theme resource dictionary at runtime - every open window using
/// DynamicResource brushes re-paints immediately, no restart needed.</summary>
public static class ThemeManager
{
    static AppTheme _requestedTheme = AppTheme.Light;
    static bool _initialized;
    public static AppTheme EffectiveTheme { get; private set; } = AppTheme.Light;

    public static void Initialize(AppTheme theme)
    {
        if (!_initialized)
        {
            SystemParameters.StaticPropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SystemParameters.HighContrast)) ReapplyOnUiThread();
            };
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category is UserPreferenceCategory.Color or UserPreferenceCategory.General)
                    ReapplyOnUiThread();
            };
            _initialized = true;
        }
        Apply(theme);
    }

    public static void Apply(AppTheme theme)
    {
        _requestedTheme = theme;
        var effectiveTheme = theme == AppTheme.System ? WindowsTheme() : theme;
        EffectiveTheme = effectiveTheme;
        // Absolute pack URIs: a relative Source on a dictionary created in code has no base URI to
        // resolve against, so spell out the assembly explicitly.
        var themeFile = SystemParameters.HighContrast
            ? "HighContrast.xaml"
            : effectiveTheme == AppTheme.Dark ? "Dark.xaml" : "Light.xaml";
        var uri = new Uri($"pack://application:,,,/ClassroomHost;component/Themes/{themeFile}", UriKind.Absolute);
        var dict = new ResourceDictionary { Source = uri };

        var app = Application.Current.Resources.MergedDictionaries;
        for (int i = app.Count - 1; i >= 0; i--)
        {
            // Only swap out the color-scheme dictionaries (Light.xaml / Dark.xaml) - never
            // Controls.xaml, which holds the shared styles/templates and stays merged always.
            if (app[i].Source is { } src && (src.OriginalString.EndsWith("Light.xaml") ||
                                             src.OriginalString.EndsWith("Dark.xaml") ||
                                             src.OriginalString.EndsWith("HighContrast.xaml")))
                app.RemoveAt(i);
        }
        app.Add(dict);

        foreach (Window window in Application.Current.Windows)
            WindowsTitleBar.Apply(window, effectiveTheme == AppTheme.Dark, SystemParameters.HighContrast);
    }

    static void ReapplyOnUiThread()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        _ = dispatcher.BeginInvoke(() => Apply(_requestedTheme));
    }

    static AppTheme WindowsTheme()
    {
        try
        {
            var value = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return value is int light && light == 0 ? AppTheme.Dark : AppTheme.Light;
        }
        catch { return AppTheme.Light; }
    }
}

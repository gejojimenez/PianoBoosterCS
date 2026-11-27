// Not in the C++ code: the profile whose practices are saved (D13 of FEATURES.md) and the
// deletion of a profile used by ProfilesDialog (phase 1.5). GPLv3, like the rest of PianoBoosterCS.

using System;
using System.Globalization;
using PianoBooster.Core;
using PianoBooster.Data;

namespace PianoBooster.App;

/// <summary>
/// The active profile, kept by its id in the settings (<see cref="SettingsKey"/>), so that
/// renaming it does not lose it. When the settings have none, or it no longer exists, the first
/// profile is used; when there are no profiles, one is created with the name of the account
/// (D13). It never throws: without a profile nothing is saved.
/// </summary>
public static class ActiveProfile
{
    public const string SettingsKey = "Profile/active";

    /// <summary>The id of the active profile (made the active one in the settings), or -1 if
    /// there is none and none could be created.</summary>
    public static long Resolve(IProgressStore store, SettingsStore settings, string defaultName)
    {
        if (!store.IsOpen)
            return -1;
        if (long.TryParse(settings.GetString(SettingsKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)
            && store.Profile(id) != null)
            return id;

        var profiles = store.Profiles();
        id = profiles.Count > 0 ? profiles[0].Id : store.CreateProfile(defaultName);
        if (id < 0)
        {
            Log.Warn("No profile: the practices will not be saved");
            return -1;
        }
        Set(settings, id);
        return id;
    }

    /// <summary>Makes a profile the active one.</summary>
    public static void Set(SettingsStore settings, long id) =>
        settings.SetValue(SettingsKey, id.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Deletes a profile with its practices (the profiles dialog, phase 1.5), but never the last
    /// one, so that there is always an active profile. If it was the active one, the first profile
    /// that is left (by name, as <see cref="Resolve"/>) becomes the active one. False if it was not
    /// deleted. active: the active profile afterwards.
    /// </summary>
    public static bool Delete(IProgressStore store, SettingsStore settings, long id, long activeId, out long active)
    {
        active = activeId;
        var profiles = store.Profiles();
        if (profiles.Count < 2 || !profiles.Exists(p => p.Id == id))
            return false;
        store.DeleteProfile(id);
        if (store.Profile(id) != null)
            return false; // the store could not delete it (logged)
        if (id == activeId)
        {
            active = store.Profiles() is { Count: > 0 } left ? left[0].Id : -1;
            if (active >= 0)
                Set(settings, active);
        }
        return true;
    }

    /// <summary>The name of the default profile: the name of the account, or the fallback.</summary>
    public static string DefaultName(string fallback)
    {
        try
        {
            string name = Environment.UserName.Trim();
            return name.Length > 0 ? name : fallback;
        }
        catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException)
        {
            return fallback;
        }
    }
}

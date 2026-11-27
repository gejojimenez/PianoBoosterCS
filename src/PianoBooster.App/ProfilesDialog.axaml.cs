// Not in the C++ code: the user profiles (phase 1.5 of FEATURES.md): create, rename, delete (with
// its practices, never the last one) and choose the active one. GPLv3, like the rest of PianoBoosterCS.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using PianoBooster.Core;
using PianoBooster.Data;

namespace PianoBooster.App;

public partial class ProfilesDialog : Window
{
    private IProgressStore _store = null!;
    private SettingsStore _settings = null!;
    private Action<long> _activeChanged = _ => { };
    private List<ProgressProfile> _profiles = new();
    private long _activeId = -1;

    public ProfilesDialog()
    {
        InitializeComponent();
        Strings.TranslateTree(this);
        Title = Strings.Tr("Profiles");
    }

    /// <summary>The active profile, when the dialog is closed.</summary>
    public long ActiveId => _activeId;

    /// <summary>activeChanged is called at once each time another profile becomes the active
    /// one (chosen, or because the active one was deleted), so that the practice that is being
    /// played is not saved for it.</summary>
    public void Init(IProgressStore store, SettingsStore settings, long activeId, Action<long> activeChanged)
    {
        _store = store;
        _settings = settings;
        _activeId = activeId;
        _activeChanged = activeChanged;
        Reload(activeId);
    }

    // The list again, with the profile of this id chosen
    private void Reload(long selectId)
    {
        _profiles = _store.Profiles();
        profileList.Items.Clear();
        int selected = -1;
        for (int i = 0; i < _profiles.Count; i++)
        {
            ProgressProfile profile = _profiles[i];
            bool active = profile.Id == _activeId;
            profileList.Items.Add(new ListBoxItem
            {
                Content = new TextBlock
                {
                    Text = active ? Strings.Tr("%1 (active)", profile.Name) : profile.Name,
                    FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal,
                },
            });
            if (profile.Id == selectId)
                selected = i;
        }
        profileList.SelectedIndex = selected >= 0 ? selected : (_profiles.Count > 0 ? 0 : -1);
        UpdateButtons();
    }

    private ProgressProfile? Chosen =>
        profileList.SelectedIndex is int i && i >= 0 && i < _profiles.Count ? _profiles[i] : null;

    private void UpdateButtons()
    {
        ProgressProfile? chosen = Chosen;
        newButton.IsEnabled = _store.IsOpen;
        useButton.IsEnabled = chosen != null && chosen.Id != _activeId;
        renameButton.IsEnabled = chosen != null;
        deleteButton.IsEnabled = chosen != null && _profiles.Count > 1; // never the last one
        ToolTip.SetTip(deleteButton, _profiles.Count > 1
            ? Strings.Tr("Delete the chosen profile and all its practices")
            : Strings.Tr("The last profile cannot be deleted"));
    }

    private void MakeActive(long id)
    {
        if (id == _activeId)
            return;
        _activeId = id;
        ActiveProfile.Set(_settings, id);
        _activeChanged(id);
    }

    private void OnProfileListSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void OnProfileListDoubleTapped(object? sender, TappedEventArgs e) => OnUseButtonClicked(sender, e);

    private void OnUseButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (Chosen is not ProgressProfile chosen)
            return;
        MakeActive(chosen.Id);
        Reload(chosen.Id);
    }

    // The name of a profile, asked again while it is empty or used; apply gives the id of the
    // profile, or -1 if the name was refused. -1 if cancelled.
    private async Task<long> AskName(string title, string name, Func<string, long> apply)
    {
        while (true)
        {
            string? text = await MessageDialog.GetText(this, title, Strings.Tr("Name of the profile:"), name);
            if (text == null)
                return -1;
            long id = apply(text);
            if (id >= 0)
                return id;
            await MessageDialog.Warning(this, title, text.Trim().Length == 0
                ? Strings.Tr("The name of the profile cannot be empty.")
                : Strings.Tr("There is already a profile called \"%1\".", text.Trim()));
            name = text;
        }
    }

    private async void OnNewButtonClicked(object? sender, RoutedEventArgs e)
    {
        long id = await AskName(Strings.Tr("New Profile"), "", _store.CreateProfile);
        if (id < 0)
            return;
        Log.Info($"Profile {id} created");
        Reload(id);
    }

    private async void OnRenameButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (Chosen is not ProgressProfile chosen)
            return;
        long id = await AskName(Strings.Tr("Rename Profile"), chosen.Name,
            name => name.Trim() == chosen.Name || _store.RenameProfile(chosen.Id, name) ? chosen.Id : -1);
        if (id >= 0)
            Reload(id);
    }

    private async void OnDeleteButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (Chosen is not ProgressProfile chosen || _profiles.Count < 2)
            return;
        int practices = _store.PracticeCount(chosen.Id);
        string text = Strings.Tr("Delete the profile \"%1\"?", chosen.Name) + "\n\n" +
            (practices > 0
                ? Strings.TrPlural("Its %n practice(s) will be deleted too.", practices)
                : Strings.Tr("It has no practices.")) +
            " " + Strings.Tr("This cannot be undone.");
        if (!await MessageDialog.Question(this, Strings.Tr("Delete Profile"), text))
            return;

        long oldActive = _activeId;
        if (!ActiveProfile.Delete(_store, _settings, chosen.Id, _activeId, out long active))
        {
            await MessageDialog.Warning(this, Strings.Tr("Delete Profile"),
                Strings.Tr("The profile \"%1\" could not be deleted.", chosen.Name));
            return;
        }
        Log.Info($"Profile {chosen.Id} deleted with its {practices} practices");
        _activeId = active;
        if (active != oldActive)
            _activeChanged(active);
        Reload(active);
    }

    private void OnCloseButtonClicked(object? sender, RoutedEventArgs e) => Close();
}

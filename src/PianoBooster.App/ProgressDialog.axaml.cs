// Not in the C++ code: the history of the practices of the active profile (phase 1.6 of
// FEATURES.md): each song, mode and hand it has practised, with its practices, and the result of
// each one with its errors. Only to look at: nothing is changed. GPLv3, like the rest of PianoBoosterCS.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PianoBooster.Core;

namespace PianoBooster.App;

public partial class ProgressDialog : Window
{
    private static readonly double[] SummaryWidths = { 250, 90, 120, 80, 130, 120 };
    private static readonly double[] PracticeWidths = { 150, 80, 210, 70, 80 };

    private IProgressStore _store = null!;
    private long _profileId = -1;
    private long _loadedSongId = -1;
    private List<PracticeSummary> _summaries = new();
    private List<PracticeRecord> _practices = new();

    public ProgressDialog()
    {
        InitializeComponent();
        Strings.TranslateTree(this);
        Title = Strings.Tr("Practice History");
    }

    /// <summary>The history of this profile. loadedSongId: the song that is loaded (its key
    /// signature names the notes of its errors).</summary>
    public void Init(IProgressStore store, long profileId, long loadedSongId)
    {
        _store = store;
        _profileId = profileId;
        _loadedSongId = loadedSongId;
        string name = store.Profile(profileId)?.Name ?? "";
        profileText.Text = Strings.Tr("The practices of %1", name);

        _summaries = store.PracticeSummaries(profileId);
        emptyText.IsVisible = _summaries.Count == 0;
        tables.IsVisible = _summaries.Count > 0;
        SimpleTable.Fill(summaryHeader, summaryList, SummaryWidths,
            new[] { Strings.Tr("Song"), Strings.Tr("Mode"), Strings.Tr("Hand"), Strings.Tr("Practices"),
                Strings.Tr("Best"), Strings.Tr("Last practice") },
            _summaries.Select(s => (IReadOnlyList<string>)new[]
            {
                SongTitle(s.Song), PracticeText.Mode(s.Mode), PracticeText.Hand(s.Hand),
                s.Count.ToString(System.Globalization.CultureInfo.CurrentCulture),
                PracticeText.Score(s.Best), PracticeText.Date(s.LastEnded),
            }));
        summaryList.SelectedIndex = _summaries.Count > 0 ? 0 : -1;
        ShowPractices();
    }

    private static string SongTitle(ProgressSong song) =>
        song.Title.Length > 0 ? song.Title : Path.GetFileNameWithoutExtension(song.Path);

    private PracticeSummary? ChosenSummary =>
        summaryList.SelectedIndex is int i && i >= 0 && i < _summaries.Count ? _summaries[i] : null;

    private PracticeRecord? ChosenPractice =>
        practiceList.SelectedIndex is int i && i >= 0 && i < _practices.Count ? _practices[i] : null;

    // The practices of the chosen song, mode and hand, the newest first
    private void ShowPractices()
    {
        PracticeSummary? summary = ChosenSummary;
        _practices = summary == null ? new List<PracticeRecord>()
            : _store.Practices(_profileId, summary.Song.Id, summary.Mode, summary.Hand);
        long bestId = summary?.Best.Id ?? -1;
        practicesText.Text = summary == null ? "" : Strings.Tr("Practices of %1 (%2, %3)", SongTitle(summary.Song),
            PracticeText.Mode(summary.Mode), PracticeText.Hand(summary.Hand));
        SimpleTable.Fill(practiceHeader, practiceList, PracticeWidths,
            new[] { Strings.Tr("Date"), Strings.Tr("Speed"), Strings.Tr("Score"), Strings.Tr("Errors"),
                Strings.Tr("Duration") },
            _practices.Select(p => (IReadOnlyList<string>)new[]
            {
                PracticeText.Date(p.Started), PracticeText.Speed(p),
                p.Id == bestId ? Strings.Tr("%1 (best)", PracticeText.Score(p)) : PracticeText.Score(p),
                p.ErrorCount.ToString(System.Globalization.CultureInfo.CurrentCulture), PracticeText.Duration(p),
            }),
            index => _practices[index].Id == bestId);
        practiceList.SelectedIndex = _practices.Count > 0 ? 0 : -1;
        errorsButton.IsEnabled = ChosenPractice != null;
    }

    private void OnSummaryListSelectionChanged(object? sender, SelectionChangedEventArgs e) => ShowPractices();

    private void OnPracticeListSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        errorsButton.IsEnabled = ChosenPractice != null;

    private void OnPracticeListDoubleTapped(object? sender, TappedEventArgs e) => OnErrorsButtonClicked(sender, e);

    private async void OnErrorsButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (ChosenPractice is not PracticeRecord chosen || ChosenSummary is not PracticeSummary summary)
            return;
        try
        {
            // with its errors; null if it could not be read (logged by the store)
            if (_store.Practice(chosen.Id) is not PracticeRecord practice)
                return;
            var resultDialog = new PracticeResultDialog();
            resultDialog.Init(practice, SongTitle(summary.Song), summary.Song.Id == _loadedSongId);
            await resultDialog.ShowDialog<bool?>(this);
        }
        catch (System.Exception ex)
        {
            Log.Error($"The result of the practice {chosen.Id} could not be shown: {ex.Message}");
        }
    }

    private void OnCloseButtonClicked(object? sender, RoutedEventArgs e) => Close();
}

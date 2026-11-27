// Not in the C++ code: the result of a whole practice (phase 1.6 of FEATURES.md): the score of its
// real maximum, the errors of each kind, the list of the errors and, when it has just ended, how it
// compares with the best practice before it. GPLv3, like the rest of PianoBoosterCS.

using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PianoBooster.Core;

namespace PianoBooster.App;

public partial class PracticeResultDialog : Window
{
    private static readonly double[] Widths = { 70, 60, 140, 90, 90, 70 };

    public PracticeResultDialog()
    {
        InitializeComponent();
        Strings.TranslateTree(this);
        Title = Strings.Tr("Practice Result");
    }

    /// <summary>A practice that has just been saved, compared with the best one before it.</summary>
    public void Init(SavedPractice saved, string songTitle) =>
        Show(saved.Practice, songTitle, loadedSong: true, compare: true, saved.PreviousBest, saved.IsNewBest);

    /// <summary>A practice of the history (with its errors), without a comparison. loadedSong:
    /// whether its song is the loaded one, whose key signature names the notes.</summary>
    public void Init(PracticeRecord practice, string songTitle, bool loadedSong) =>
        Show(practice, songTitle, loadedSong, compare: false, null, false);

    private void Show(PracticeRecord practice, string songTitle, bool loadedSong, bool compare,
        PracticeRecord? previousBest, bool isNewBest)
    {
        songText.Text = songTitle;
        howText.Text = string.Join(" · ", PracticeText.Mode(practice.Mode), PracticeText.Hand(practice.Hand),
            Strings.Tr("Speed %1", PracticeText.Speed(practice)),
            PracticeText.Date(practice.Started), Strings.Tr("Duration %1", PracticeText.Duration(practice)));
        scoreText.Text = PracticeText.Score(practice);

        bestText.IsVisible = comparisonText.IsVisible = compare;
        if (compare)
        {
            if (previousBest == null)
            {
                bestText.Text = Strings.Tr("This is your first practice of this song in this mode and with this hand.");
                comparisonText.IsVisible = false;
            }
            else
            {
                bestText.Text = Strings.Tr("Best before: %1, on %2", PracticeText.Score(previousBest),
                    PracticeText.Date(previousBest.Started));
                int difference = PracticeText.WholePercent(practice) - PracticeText.WholePercent(previousBest);
                comparisonText.Text = isNewBest
                    ? (difference > 0
                        ? Strings.TrPlural("New best! %n percentage point(s) better.", difference)
                        : Strings.Tr("New best!"))
                    : (difference < 0
                        ? Strings.TrPlural("%n percentage point(s) below your best.", -difference)
                        : Strings.Tr("As good as your best."));
            }
        }

        IReadOnlyList<PracticeError> errors = practice.Errors;
        countsText.Text = Strings.TrPlural("%n error(s)", errors.Count) + ":   " + PracticeText.Counts(errors);
        noErrorsText.IsVisible = errors.Count == 0;
        errorTable.IsVisible = errors.Count > 0;

        bool hands = practice.Hand == WhichPart.Both; // which hand, only when both play
        var widths = hands ? Widths : Widths.Take(Widths.Length - 1).ToArray();
        var titles = new List<string> { Strings.Tr("Bar"), Strings.Tr("Beat"), Strings.Tr("Error"),
            Strings.Tr("Expected"), Strings.Tr("Played") };
        if (hands)
            titles.Add(Strings.Tr("Hand"));
        bool keyOfC = !loadedSong;
        IEnumerable<IReadOnlyList<string>> rows = errors
            .OrderBy(e => e.Bar).ThenBy(e => e.Beat).ThenBy(e => e.Tick)
            .Select(e =>
            {
                var cells = new List<string> { PracticeText.Bar(e), PracticeText.Beat(e), PracticeText.Kind(e.Kind),
                    NoteText.Name(e.ExpectedNote, keyOfC), NoteText.Name(e.PlayedNote, keyOfC) };
                if (hands)
                    cells.Add(PracticeText.ShortHand(e.Hand));
                return (IReadOnlyList<string>)cells;
            });
        SimpleTable.Fill(errorHeader, errorList, widths, titles, rows);
    }

    private void OnCloseButtonClicked(object? sender, RoutedEventArgs e) => Close();
}

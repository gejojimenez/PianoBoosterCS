// Not in the C++ code: the texts and tables of the results and the history of the practices
// (phase 1.6 of FEATURES.md). GPLv3, like the rest of PianoBoosterCS.

using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PianoBooster.Core;

namespace PianoBooster.App;

/// <summary>How the practices are shown to the user.</summary>
internal static class PracticeText
{
    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    public static string Mode(PlayMode mode) => mode switch
    {
        PlayMode.FollowYou => Strings.Tr("Follow You"),
        PlayMode.PlayAlong => Strings.Tr("Play Along"),
        _ => mode.ToString(),
    };

    public static string Hand(WhichPart hand) => hand switch
    {
        WhichPart.Right => Strings.Tr("Right hand"),
        WhichPart.Left => Strings.Tr("Left hand"),
        WhichPart.Both => Strings.Tr("Both hands"),
        _ => "",
    };

    /// <summary>The hand of an error, in short.</summary>
    public static string ShortHand(WhichPart hand) => hand switch
    {
        WhichPart.Right => Strings.Tr("Right"),
        WhichPart.Left => Strings.Tr("Left"),
        _ => "",
    };

    public static string Kind(PracticeErrorKind kind) => kind switch
    {
        PracticeErrorKind.Early => Strings.Tr("Early"),
        PracticeErrorKind.Late => Strings.Tr("Late"),
        PracticeErrorKind.Wrong => Strings.Tr("Wrong note"),
        PracticeErrorKind.Missed => Strings.Tr("Missed"),
        _ => kind.ToString(),
    };

    /// <summary>The percentage rounded down, so that 100 % is only a perfect practice.</summary>
    public static int WholePercent(PracticeRecord practice) => (int)Math.Floor(practice.Percent + 1e-9);

    public static string Percent(PracticeRecord practice) => Strings.Tr("%1 %", WholePercent(practice));

    /// <summary>"140 / 170 (82 %)".</summary>
    public static string Score(PracticeRecord practice) =>
        Strings.Tr("%1 / %2 (%3 %)", practice.Score, practice.MaxScore, WholePercent(practice));

    public static string Date(DateTime date) => date.ToString("g", Culture);

    /// <summary>"2:05", or "1:02:05".</summary>
    public static string Duration(PracticeRecord practice)
    {
        TimeSpan time = practice.Ended - practice.Started;
        if (time < TimeSpan.Zero)
            time = TimeSpan.Zero;
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    public static string Speed(PracticeRecord practice) => Strings.Tr("%1 %", practice.Speed);

    /// <summary>The bar as the score numbers it.</summary>
    public static string Bar(PracticeError error) =>
        (error.Bar + Core.Bar.FirstBarNumber).ToString(CultureInfo.InvariantCulture);

    public static string Beat(PracticeError error) => (error.Beat + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>How many errors of each kind there are, for the ones that there are: "Early: 2 · Wrong note: 3".</summary>
    public static string Counts(IReadOnlyList<PracticeError> errors)
    {
        var parts = new List<string>();
        foreach (PracticeErrorKind kind in new[] { PracticeErrorKind.Early, PracticeErrorKind.Late,
            PracticeErrorKind.Wrong, PracticeErrorKind.Missed })
        {
            int count = 0;
            foreach (PracticeError error in errors)
                if (error.Kind == kind)
                    count++;
            parts.Add(Strings.Tr("%1: %2", Kind(kind), count));
        }
        return string.Join("   ", parts);
    }
}

/// <summary>A table of text: a header above a list of rows with the same columns.</summary>
internal static class SimpleTable
{
    /// <summary>A row of cells in columns of these widths (the last one takes the rest).</summary>
    public static Grid Row(IReadOnlyList<double> widths, IReadOnlyList<string> cells, bool bold = false)
    {
        var grid = new Grid();
        for (int i = 0; i < widths.Count; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(i == widths.Count - 1
                ? new GridLength(Math.Max(widths[i], 1), GridUnitType.Star)
                : new GridLength(widths[i])));
        for (int i = 0; i < cells.Count && i < widths.Count; i++)
        {
            var text = new TextBlock
            {
                Text = cells[i],
                Margin = new Avalonia.Thickness(4, 2),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
            };
            ToolTip.SetTip(text, cells[i]);
            Grid.SetColumn(text, i);
            grid.Children.Add(text);
        }
        return grid;
    }

    /// <summary>Fills a header and a list box with these rows; the row of index bold is in bold.</summary>
    public static void Fill(Decorator header, ListBox list, IReadOnlyList<double> widths, IReadOnlyList<string> titles,
        IEnumerable<IReadOnlyList<string>> rows, Func<int, bool>? bold = null)
    {
        header.Child = Row(widths, titles, bold: true);
        list.Items.Clear();
        int index = 0;
        foreach (IReadOnlyList<string> row in rows)
        {
            list.Items.Add(new ListBoxItem { Content = Row(widths, row, bold?.Invoke(index) == true) });
            index++;
        }
    }
}

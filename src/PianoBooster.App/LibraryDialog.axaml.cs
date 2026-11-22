// Ported from GuiLibraryDialog.h and GuiLibraryDialog.cpp of PianoBooster (C++): the song library
// window: favourites, recent songs and the MIDI files in the locations (folders) chosen by the
// user. GPLv3.
//
// The two QTreeWidget of the C++ are a TreeView (the sources) and a list of rows with a header of
// buttons (the songs: Avalonia has no tree with columns). What the C++ keeps in the item roles
// (KIND_ROLE, PATH_ROLE, SORT_ROLE) is in Source and SongRow.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PianoBooster.Core;
using PianoBooster.Data;

namespace PianoBooster.App;

public partial class LibraryDialog : Window
{
    private const int MaxSongsListed = 5000;

    // enum SongColumn
    private const int ColFavourite = 0, ColName = 1, ColFolder = 2, ColDuration = 3, ColBars = 4,
        ColTimeSig = 5, ColParts = 6, ColLastOpened = 7, ColTimesOpened = 8, ColCount = 9;

    private const string Star = "★";
    private const string EmptyStar = "☆";

    private enum SourceKind { Favourites, Recent, Location, Folder }

    /// <summary>KIND_ROLE and PATH_ROLE of an item of the sources; the heading "Locations" has none.</summary>
    private sealed class Source
    {
        public SourceKind Kind;
        public string? Path;
        public bool SubfoldersAdded;
    }

    private readonly ISongLibrary _library = null!;
    private readonly string _currentSong = "";

    private readonly List<SongRow> _rows = new();       // all the songs shown (m_songs), sorted
    private SongRow? _current;                          // the current item of m_songs, even if hidden by the filter
    private int _sortColumn = ColName;
    private bool _sortDescending;
    private readonly double[] _widths = new double[ColCount];
    private readonly Button[] _headerButtons = new Button[ColCount];
    private readonly string[] _headerLabels = new string[ColCount];
    private int _updating;                              // QSignalBlocker
    private bool _sourcePointerPressed;
    private TreeViewItem? _favouritesItem, _recentItem, _locationsItem;

    /// <summary>The song to open, after the dialog has been accepted (selectedSong()).</summary>
    public string SelectedSong { get; private set; } = "";

    /// <summary>For the designer of Avalonia only.</summary>
    public LibraryDialog()
    {
        InitializeComponent();
    }

    public LibraryDialog(ISongLibrary library, string currentSong)
    {
        _library = library;
        _currentSong = Library.NormalisePath(currentSong);
        InitializeComponent();

        Title = Strings.Tr("Library");
        addButton.Content = Strings.Tr("Add Location...");
        ToolTip.SetTip(addButton, Strings.Tr("Add a folder with MIDI files to the library"));
        removeLocationButton.Content = Strings.Tr("Remove");
        ToolTip.SetTip(removeLocationButton, Strings.Tr("Remove the selected location from the library (the files are not deleted)"));
        search.PlaceholderText = Strings.Tr("Search...");
        openButton.Content = Strings.Tr("Open");
        closeButton.Content = Strings.Tr("Close");

        CreateSongsHeader();
        songs.ItemTemplate = new FuncDataTemplate<SongRow>((_, _) => new SongRowView(this), supportsRecycling: true);

        sources.SelectionChanged += (_, e) => OnSourcesSelectionChanged(e);
        sources.AddHandler(TreeViewItem.ExpandedEvent, (_, e) =>
        {
            if (e.Source is TreeViewItem item)
                SourceExpanded(item);
        });
        // to tell a click from the keyboard on the heading, which can't be selected
        sources.AddHandler(PointerPressedEvent, (_, _) => _sourcePointerPressed = true, RoutingStrategies.Tunnel);
        sources.AddHandler(PointerReleasedEvent, (_, _) => _sourcePointerPressed = false, RoutingStrategies.Tunnel);
        songs.SelectionChanged += (_, _) =>
        {
            if (_updating == 0)
                _current = songs.SelectedItem as SongRow;
            UpdateButtons();
        };
        search.TextChanged += (_, _) => FilterSongs();

        // start in the location of the current song, if it is in one
        string startLocation = "";
        foreach (string location in _library.Locations())
            if (_currentSong.StartsWith(location + "/", StringComparison.Ordinal))
                startLocation = location;
        FillSources(startLocation);
    }

    // ---- the sources

    private static Source? SourceOf(TreeViewItem? item) => item?.Tag as Source;

    private static TreeViewItem NewSourceItem(string text, Source? source) =>
        new() { Header = text, Tag = source };

    private void FillSources(string locationToSelect = "")
    {
        TreeViewItem selected;
        _updating++;
        try
        {
            sources.Items.Clear();

            _favouritesItem = NewSourceItem(Star + " " + Strings.Tr("Favourites"), new Source { Kind = SourceKind.Favourites });
            _recentItem = NewSourceItem("⏲ " + Strings.Tr("Recent"), new Source { Kind = SourceKind.Recent });
            sources.Items.Add(_favouritesItem);
            sources.Items.Add(_recentItem);

            _locationsItem = NewSourceItem(Strings.Tr("Locations"), null); // a heading, not selectable
            _locationsItem.FontWeight = FontWeight.Bold;
            sources.Items.Add(_locationsItem);

            selected = _recentItem;
            foreach (string path in _library.Locations())
            {
                var item = NewSourceItem(DirName(path), new Source { Kind = SourceKind.Location, Path = path });
                // the heading is bold, its children are not
                item.FontWeight = FontWeight.Normal;
                ToolTip.SetTip(item, NativeSeparators(path));
                if (!Directory.Exists(path))
                {
                    item.Foreground = Brushes.Gray;
                    ToolTip.SetTip(item, Strings.Tr("Not found: %1", NativeSeparators(path)));
                }
                else if (HasSubfolders(path))
                    AddChildIndicator(item);
                _locationsItem.Items.Add(item);
                if (path == locationToSelect)
                    selected = item;
            }
            _locationsItem.IsExpanded = true;
        }
        finally
        {
            _updating--;
        }
        sources.SelectedItem = selected; // setCurrentItem(): calls sourceChanged()
    }

    // setChildIndicatorPolicy(ShowIndicator): an invisible child until the subfolders are added
    private static void AddChildIndicator(TreeViewItem item) =>
        item.Items.Add(new TreeViewItem { IsVisible = false });

    private static void AddSubfolders(TreeViewItem item)
    {
        if (SourceOf(item) is not { Path: string path } source || source.SubfoldersAdded)
            return;
        source.SubfoldersAdded = true;
        item.Items.Clear();
        foreach (string name in Subfolders(path))
        {
            string folderPath = path + "/" + name;
            var child = NewSourceItem(name, new Source { Kind = SourceKind.Folder, Path = folderPath });
            child.FontWeight = FontWeight.Normal;
            ToolTip.SetTip(child, NativeSeparators(folderPath));
            if (HasSubfolders(folderPath))
                AddChildIndicator(child);
            item.Items.Add(child);
        }
        // with no subfolders, the item has no children: DontShowIndicator
    }

    private static void SourceExpanded(TreeViewItem item)
    {
        if (SourceOf(item) is { Path: not null })
            AddSubfolders(item);
    }

    private void OnSourcesSelectionChanged(SelectionChangedEventArgs e)
    {
        if (_updating > 0)
            return;
        if (sources.SelectedItem is TreeViewItem item && item == _locationsItem)
        {
            // the heading can't be selected (Qt::ItemIsEnabled only): a click does nothing and
            // the keyboard goes past it
            TreeViewItem? previous = e.RemovedItems.Count > 0 ? e.RemovedItems[0] as TreeViewItem : null;
            TreeViewItem? next = previous;
            if (!_sourcePointerPressed)
                next = previous == _recentItem
                    ? (_locationsItem.Items.Count > 0 ? _locationsItem.Items[0] as TreeViewItem : previous)
                    : _recentItem;
            Dispatcher.UIThread.Post(() => sources.SelectedItem = next ?? _recentItem);
            return;
        }
        SourceChanged();
    }

    private void SourceChanged()
    {
        TreeViewItem? item = sources.SelectedItem as TreeViewItem;
        UpdateButtons();
        if (SourceOf(item) is not Source source)
        {
            ShowSongs(new List<LibrarySong>(), "");
            return;
        }

        switch (source.Kind)
        {
            case SourceKind.Favourites:
                ShowSongs(_library.Favourites(), "");
                break;
            case SourceKind.Recent:
                ShowSongs(_library.Recent(50), "");
                break;
            default:
            {
                // the MIDI files in this folder and its subfolders
                string folder = source.Path!;
                var paths = new List<string>();
                foreach (string path in MidiFiles(folder))
                {
                    if (paths.Count >= MaxSongsListed)
                        break;
                    paths.Add(path);
                }

                // read the information of the songs that have not been seen before (it is quick),
                // and keep it in the library for the next time
                List<LibrarySong> songList = _library.Songs(paths);
                Cursor? cursor = Cursor;
                Cursor = new Cursor(StandardCursorType.Wait);
                _library.BeginBatch();
                try
                {
                    foreach (LibrarySong song in songList)
                        if (!song.Info.Valid)
                        {
                            song.Info = MidiInfo.Read(song.Path);
                            _library.StoreInfo(song.Path, song.Info);
                        }
                }
                finally
                {
                    _library.EndBatch();
                    Cursor = cursor;
                }
                ShowSongs(songList, folder);
                break;
            }
        }
    }

    // ---- the songs

    private void ShowSongs(List<LibrarySong> songList, string baseFolder)
    {
        _rows.Clear();
        _current = null;
        CultureInfo locale = CultureInfo.CurrentCulture;

        foreach (LibrarySong song in songList)
        {
            if (!File.Exists(song.Path))
                continue; // deleted or moved since it was opened

            string absolutePath = Path.GetDirectoryName(song.Path)?.Replace('\\', '/') ?? "";
            var row = new SongRow { Path = song.Path };
            row.Text[ColFavourite] = song.Favourite ? Star : EmptyStar;
            row.SortKey[ColFavourite] = song.Favourite ? 0 : 1;
            row.Text[ColName] = Path.GetFileNameWithoutExtension(song.Path); // completeBaseName()

            // the folder, relative to the selected one
            string folder = absolutePath;
            if (baseFolder.Length > 0)
                folder = Path.GetRelativePath(baseFolder, folder);
            if (folder == ".")
                folder = "";
            row.Text[ColFolder] = NativeSeparators(folder);
            row.FolderTip = NativeSeparators(absolutePath);

            if (song.Info.Valid)
            {
                row.Text[ColDuration] = song.Info.DurationText();
                row.SortKey[ColDuration] = song.Info.DurationMs;
                row.Text[ColBars] = song.Info.Bars.ToString(CultureInfo.InvariantCulture);
                row.SortKey[ColBars] = song.Info.Bars;
                row.Text[ColTimeSig] = song.Info.TimeSigText();
            }
            if (song.LastOpened is DateTime lastOpened)
            {
                row.Text[ColLastOpened] = lastOpened.ToString("g", locale); // QLocale::ShortFormat
                row.SortKey[ColLastOpened] = lastOpened;
            }
            else
                row.SortKey[ColLastOpened] = DateTime.UnixEpoch.ToLocalTime();
            row.Text[ColParts] = song.PartCount > 0 ? song.PartCount.ToString(CultureInfo.InvariantCulture) : "";
            row.SortKey[ColParts] = song.PartCount;
            row.Text[ColTimesOpened] = song.TimesOpened > 0 ? song.TimesOpened.ToString(CultureInfo.InvariantCulture) : "";
            row.SortKey[ColTimesOpened] = song.TimesOpened;

            if (song.Path == _currentSong)
            {
                row.IsCurrentSong = true;
                _current = row;
            }
            _rows.Add(row);
        }

        // recent songs in the order they were opened, the others by name
        if (SourceOf(sources.SelectedItem as TreeViewItem)?.Kind == SourceKind.Recent)
            SortByColumn(ColLastOpened, descending: true, refresh: false);
        else
            SortByColumn(ColName, descending: false, refresh: false);
        foreach (int column in new[] { ColDuration, ColBars, ColTimeSig, ColParts, ColLastOpened, ColTimesOpened })
            ResizeColumnToContents(column);

        FilterSongs(); // fills the list
        if (_current != null)
            Dispatcher.UIThread.Post(() =>
            {
                if (_current != null && songs.SelectedItem == _current)
                    songs.ScrollIntoView(_current);
            }, DispatcherPriority.Loaded);
        UpdateButtons();
    }

    private void SongClicked(SongRow row, SongRowView view)
    {
        bool favourite = row.Text[ColFavourite] != Star;
        _library.SetFavourite(row.Path, favourite);
        row.Text[ColFavourite] = favourite ? Star : EmptyStar;
        row.SortKey[ColFavourite] = favourite ? 0 : 1;
        view.Update();
    }

    private void FilterSongs()
    {
        string text = (search.Text ?? "").Trim();
        List<SongRow> visible = _rows.Where(row => text.Length == 0 ||
                row.Text[ColName].Contains(text, StringComparison.OrdinalIgnoreCase) ||
                row.Text[ColFolder].Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        _updating++;
        try
        {
            songs.ItemsSource = visible;
            songs.SelectedItem = _current != null && visible.Contains(_current) ? _current : null;
        }
        finally
        {
            _updating--;
        }
        UpdateButtons();
    }

    private void OpenSelected()
    {
        if (songs.SelectedItem is not SongRow row) // no current item, or hidden by the filter
            return;
        SelectedSong = row.Path;
        Close(true); // accept()
    }

    private async void AddLocation()
    {
        IStorageFolder? home = await StorageProvider.TryGetFolderFromPathAsync(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.Tr("Add a Folder with MIDI Files"),
            AllowMultiple = false,
            SuggestedStartLocation = home,
        });
        string? folder = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(folder))
            return;
        _library.AddLocation(folder);
        FillSources(Library.NormalisePath(folder));
    }

    private async void RemoveLocation()
    {
        if (SourceOf(sources.SelectedItem as TreeViewItem) is not { Kind: SourceKind.Location, Path: string path })
            return;
        if (!await MessageDialog.Question(this, Strings.Tr("Remove Location"),
                Strings.Tr("Remove \"%1\" from the library?\n\nThe files are not deleted, and the songs keep " +
                           "their favourites and statistics.", NativeSeparators(path))))
            return;
        _library.RemoveLocation(path);
        FillSources();
    }

    private void UpdateButtons()
    {
        removeLocationButton.IsEnabled = SourceOf(sources.SelectedItem as TreeViewItem)?.Kind == SourceKind.Location;
        openButton.IsEnabled = songs.SelectedItem is SongRow;
    }

    private void OnOpenButtonClicked(object? sender, RoutedEventArgs e) => OpenSelected();

    private void OnCloseButtonClicked(object? sender, RoutedEventArgs e) => Close(false);

    private void OnAddButtonClicked(object? sender, RoutedEventArgs e) => AddLocation();

    private void OnRemoveLocationButtonClicked(object? sender, RoutedEventArgs e) => RemoveLocation();

    // ---- the columns of the songs (QHeaderView)

    private void CreateSongsHeader()
    {
        string[] labels =
        {
            Star, Strings.Tr("Name"), Strings.Tr("Folder"), Strings.Tr("Duration"), Strings.Tr("Bars"),
            Strings.Tr("Time Sig."), Strings.Tr("Sections"), Strings.Tr("Last Opened"), Strings.Tr("Times"),
        };
        _widths[ColFavourite] = 30;
        _widths[ColFolder] = 200;
        for (int column = 0; column < ColCount; column++)
        {
            _headerLabels[column] = labels[column];
            songsHeader.ColumnDefinitions.Add(new ColumnDefinition());
            int c = column;
            var button = new Button { Content = labels[column] };
            if (column == ColFavourite)
            {
                button.HorizontalContentAlignment = HorizontalAlignment.Center;
                ToolTip.SetTip(button, Strings.Tr("Favourite (click a star to change it)"));
            }
            button.Click += (_, _) => SortByColumn(c, c == _sortColumn && !_sortDescending, refresh: true);
            Grid.SetColumn(button, column);
            songsHeader.Children.Add(button);
            _headerButtons[column] = button;
        }
        UpdateColumnWidths();
    }

    private void UpdateColumnWidths()
    {
        for (int column = 0; column < ColCount; column++)
            songsHeader.ColumnDefinitions[column].Width = ColumnWidth(column);
    }

    /// <summary>The name stretches (QHeaderView::Stretch); the other columns have a width.</summary>
    internal GridLength ColumnWidth(int column) =>
        column == ColName ? new GridLength(1, GridUnitType.Star) : new GridLength(_widths[column]);

    private void ResizeColumnToContents(int column)
    {
        var typeface = new Typeface(FontFamily);
        double Measure(string text) => text.Length == 0 ? 0 :
            new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, FontSize, null)
                .WidthIncludingTrailingWhitespace;

        // the header with the sort indicator, and the widest text of the column
        double width = Measure(_headerLabels[column] + " ▼");
        foreach (string text in _rows.Select(row => row.Text[column]).Distinct())
            width = Math.Max(width, Measure(text));
        _widths[column] = Math.Ceiling(width) + 2 * SongRowView.CellPadding + 2;
        UpdateColumnWidths();
    }

    /// <summary>sortByColumn(): a stable sort, by the sort key when both items have one (numbers,
    /// dates), else by the text (SongItem::operator&lt;).</summary>
    private void SortByColumn(int column, bool descending, bool refresh)
    {
        _sortColumn = column;
        _sortDescending = descending;
        var comparer = Comparer<SongRow>.Create((a, b) =>
        {
            IComparable? x = a.SortKey[column];
            IComparable? y = b.SortKey[column];
            if (x != null && y != null)
                return x.CompareTo(y);
            return string.Compare(a.Text[column], b.Text[column], StringComparison.CurrentCulture); // localeAwareCompare
        });
        List<SongRow> sorted = descending
            ? _rows.OrderByDescending(row => row, comparer).ToList()
            : _rows.OrderBy(row => row, comparer).ToList();
        _rows.Clear();
        _rows.AddRange(sorted);

        for (int c = 0; c < ColCount; c++)
            _headerButtons[c].Content = c == column && c != ColFavourite
                ? _headerLabels[c] + (descending ? " ▼" : " ▲")
                : _headerLabels[c];
        if (refresh)
            FilterSongs();
    }

    // ---- helpers for the folders (QDir)

    private static string NativeSeparators(string path) => path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    private static string DirName(string path)
    {
        string name = Path.GetFileName(path.TrimEnd('/'));
        return name.Length > 0 ? name : path; // QDir::dirName() of a root
    }

    private static readonly EnumerationOptions FolderOptions = new()
    {
        AttributesToSkip = FileAttributes.Hidden, // without QDir::Hidden
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
    };

    /// <summary>QDir(path).entryList(QDir::Dirs | QDir::NoDotAndDotDot, QDir::Name | QDir::IgnoreCase).</summary>
    private static List<string> Subfolders(string path)
    {
        try
        {
            List<string> names = new DirectoryInfo(path).EnumerateDirectories("*", FolderOptions).Select(d => d.Name).ToList();
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            return new List<string>();
        }
    }

    private static bool HasSubfolders(string path)
    {
        try
        {
            return new DirectoryInfo(path).EnumerateDirectories("*", FolderOptions).Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>QDirIterator(folder, CLibrary::midiFileFilters(), QDir::Files, QDirIterator::Subdirectories):
    /// the files that match, case-insensitively, in the folder and its subfolders, without hidden
    /// files or folders and without following links to folders.</summary>
    private static IEnumerable<string> MidiFiles(string folder)
    {
        string[] extensions = Library.MidiFileFilters().Select(filter => filter.TrimStart('*')).ToArray();
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.Count > 0)
        {
            string dir = pending.Pop();
            List<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", FolderOptions).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
            {
                continue;
            }
            var subfolders = new List<string>();
            foreach (FileSystemInfo entry in entries)
            {
                if (entry is DirectoryInfo)
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
                        subfolders.Add(dir + "/" + entry.Name);
                }
                else if (extensions.Any(ext => entry.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                    yield return dir + "/" + entry.Name;
            }
            for (int i = subfolders.Count - 1; i >= 0; i--)
                pending.Push(subfolders[i]);
        }
    }

    // ---- a row of the songs (a QTreeWidgetItem, SongItem in the C++)

    private sealed class SongRow
    {
        public string Path = "";                                     // PATH_ROLE
        public readonly string[] Text = Enumerable.Repeat("", ColCount).ToArray();
        public readonly IComparable?[] SortKey = new IComparable?[ColCount]; // SORT_ROLE
        public string FolderTip = "";
        public bool IsCurrentSong;                                   // in bold
    }

    private sealed class SongRowView : Grid
    {
        public const double CellPadding = 4;

        private readonly LibraryDialog _dialog;
        private readonly TextBlock[] _cells = new TextBlock[ColCount];

        public SongRowView(LibraryDialog dialog)
        {
            _dialog = dialog;
            Background = Brushes.Transparent; // so that the whole row can be clicked
            for (int column = 0; column < ColCount; column++)
            {
                ColumnDefinitions.Add(new ColumnDefinition());
                var cell = new TextBlock
                {
                    Padding = new Thickness(CellPadding, 3),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = Brushes.Transparent,
                };
                if (column is ColDuration or ColBars or ColParts or ColTimesOpened)
                    cell.TextAlignment = TextAlignment.Right;
                else if (column == ColFavourite)
                    cell.TextAlignment = TextAlignment.Center;
                Grid.SetColumn(cell, column);
                Children.Add(cell);
                _cells[column] = cell;
            }
            // itemClicked: a click on the star changes the favourite
            _cells[ColFavourite].Tapped += (_, _) =>
            {
                if (DataContext is SongRow row)
                    _dialog.SongClicked(row, this);
            };
            // itemDoubleClicked: open the song, except on the star
            DoubleTapped += (_, e) =>
            {
                if (e.Source != _cells[ColFavourite] && DataContext is SongRow)
                    _dialog.OpenSelected();
            };
            DataContextChanged += (_, _) => Update();
        }

        public void Update()
        {
            for (int column = 0; column < ColCount; column++)
                ColumnDefinitions[column].Width = _dialog.ColumnWidth(column);
            if (DataContext is not SongRow row)
            {
                foreach (TextBlock cell in _cells)
                    cell.Text = "";
                return;
            }
            for (int column = 0; column < ColCount; column++)
                _cells[column].Text = row.Text[column];
            _cells[ColName].FontWeight = row.IsCurrentSong ? FontWeight.Bold : FontWeight.Normal;
            ToolTip.SetTip(_cells[ColName], NativeSeparators(row.Path));
            ToolTip.SetTip(_cells[ColFolder], row.FolderTip);
        }
    }
}

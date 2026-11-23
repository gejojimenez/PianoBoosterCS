// The types of Library.h of PianoBooster (C++): what the library knows about a song, its
// settings and its named parts, and the interface of the library that keeps them. The library
// itself (SQLite, the C++ CLibrary) is PianoBooster.Data.Library. GPLv3.

using System;
using System.Collections.Generic;

namespace PianoBooster.Core;

/// <summary>What the library knows about a song (CLibrarySong).</summary>
public sealed class LibrarySong
{
    public string Path = "";          // absolute path, with '/' separators
    public string Hash = "";          // SHA-1 of the file contents, identifies the song if it is moved
    public bool Favourite;
    public DateTime? LastOpened;      // null if never opened (an invalid QDateTime)
    public int TimesOpened;
    public MidiInfo Info = new();     // Info.Valid is false if not known yet
    public int PartCount;             // number of named parts
}

/// <summary>How a song is practised (CSongSettings). The settings belong to the contents of the
/// song: copies of the same song in different folders share them.</summary>
public struct SongSettings
{
    public SongSettings()
    {
    }

    public bool Valid = false;    // false if the song has no settings yet: these are the defaults
    public string Hand = "both";  // "left", "right" or "both"
    public int Speed = 100;       // percentage
    public int LeftChannel = -2;  // MIDI channel of each hand: -1 for none, -2 for not set
    public int RightChannel = -2;
}

/// <summary>A named part of a song, to practise it on its own: played from start to end (CSongPart).</summary>
public struct SongPart
{
    public SongPart()
    {
    }

    public long Id = -1;
    public string Name = "";
    public BarPosition Start;
    public BarPosition End;
}

/// <summary>The library of songs (the C++ CLibrary): the locations (folders) with MIDI files and
/// what is known about each song. Everything that uses it also works without it (as the C++ with
/// m_library == nullptr). The static members of CLibrary (midiFileFilters, normalisePath,
/// fileHash) are in PianoBooster.Data.Library.</summary>
public interface ISongLibrary
{
    bool IsOpen { get; }

    /// <summary>Locations: folders where the MIDI files are.</summary>
    List<string> Locations();
    void AddLocation(string folder);
    void RemoveLocation(string folder);

    /// <summary>Records that a song has been opened, with its MIDI information.
    /// A song moved or renamed since it was last seen keeps its data.</summary>
    void SongOpened(string path, MidiInfo info);

    void SetFavourite(string path, bool favourite);

    /// <summary>Stores the MIDI information of a song, without counting it as opened.</summary>
    void StoreInfo(string path, MidiInfo info);

    /// <summary>The named parts of a song, in the order they were created. They belong to the song,
    /// not to its path, so they are kept if the song is moved or renamed.</summary>
    List<SongPart> Parts(string songPath);
    long AddPart(string songPath, string name, BarPosition start, BarPosition end);
    void UpdatePart(SongPart part);
    void DeletePart(long id);

    /// <summary>The settings of a song, or the defaults if it has none.</summary>
    SongSettings GetSongSettings(string songPath);
    /// <summary>Saves the settings of a song, and of all its copies.</summary>
    void SetSongSettings(string songPath, SongSettings settings);

    /// <summary>The file name of the last song opened in a folder, empty if none.</summary>
    string LastSong(string folder);
    void SetLastSong(string folder, string songName);

    /// <summary>The note range of a MIDI keyboard, by its name (without the index of the port), or
    /// null if it has none yet. Not in the C++ code.</summary>
    (int Lowest, int Highest)? KeyboardRange(string keyboard);
    void SetKeyboardRange(string keyboard, int lowest, int highest);

    /// <summary>Groups many changes in one transaction, which is much faster.</summary>
    void BeginBatch();
    void EndBatch();

    /// <summary>The songs the library knows, by path (only those found in the database).</summary>
    LibrarySong Song(string path);
    List<LibrarySong> Songs(IEnumerable<string> paths);
    List<LibrarySong> Favourites();
    List<LibrarySong> Recent(int maximum);
}

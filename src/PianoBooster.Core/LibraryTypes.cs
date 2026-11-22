// The parts of Library.h of PianoBooster (C++) that the main window and the settings use:
// the settings and the named parts of a song, and the library that keeps them. The library
// itself (SQLite, the C++ CLibrary) comes in phase 5 (PianoBooster.Data). GPLv3.

using System.Collections.Generic;

namespace PianoBooster.Core;

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

/// <summary>The library of songs, as the main window and the settings use it (the C++ CLibrary).
/// Everything that uses it also works without it (as the C++ with m_library == nullptr).</summary>
public interface ISongLibrary
{
    void AddLocation(string folder);

    /// <summary>Records that a song has been opened, with its MIDI information.</summary>
    void SongOpened(string path, MidiInfo info);

    /// <summary>The named parts of a song, in the order they were created.</summary>
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

    /// <summary>Groups many changes in one transaction.</summary>
    void BeginBatch();
    void EndBatch();
}

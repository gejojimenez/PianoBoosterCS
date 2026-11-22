using System;
using System.IO;
using Xunit;

namespace PianoBooster.Data.Tests;

/// <summary>The settings file (SettingsStore, in place of QSettings) and the import of the .ini
/// file of the C++ program.</summary>
public sealed class SettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "PianoBooster.Data.Tests", Guid.NewGuid().ToString("N"));

    public SettingsTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    // An .ini file as PianoBooster 1.3.10 writes it (QSettings::IniFormat), with what Qt escapes
    private const string Ini = """
        [General]
        RecentFileList=C:/Music/Booster Music/06-Greensleeves.mid, C:/Music/Beginner Course/01-StartWithMiddleC.mid
        CurrentSong=C:/Music/a song.mid

        [SidePanel]
        rhythmTapping=0
        skill=2
        repeatSong=true

        [ShortCuts]
        PlayFromStart=space
        Faster="="
        Slower=-
        NextBook={
        Quoted="a, b"

        [Window]
        Pos=@Point(0 23)
        Size=@Size(1280 729)
        Geometry=@ByteArray(\x1\xd9\xd0\xcb\0\x3)
        NormalGeometry=@Rect(74 97 1035 522)

        [Midi]
        Output=Internal Sound (FluidSynth)

        [FluidSynth]
        SoundFont=C:/Users/me/Music/Soundfonts/FluidR3_GM.sf2
        masterGainSpin=80

        [Score]
        Empty=@Invalid()
        """;

    [Fact]
    public void ImportsTheIniFileOfTheCppProgram()
    {
        File.WriteAllText(Path.Combine(_folder, "Piano Booster.ini"), Ini);
        SettingsStore store = SettingsStore.Open(Path.Combine(_folder, "Piano Booster.json"));

        Assert.EndsWith("Piano Booster.ini", store.ImportedFrom);
        Assert.Equal(new[] { "C:/Music/Booster Music/06-Greensleeves.mid", "C:/Music/Beginner Course/01-StartWithMiddleC.mid" },
                     store.GetStringList("RecentFileList"));
        Assert.Equal("C:/Music/a song.mid", store.GetString("CurrentSong"));
        Assert.Equal(new[] { "C:/Music/a song.mid" }, store.GetStringList("CurrentSong")); // one item
        Assert.Equal(2, store.GetInt("SidePanel/skill"));
        Assert.True(store.GetBool("SidePanel/repeatSong"));
        Assert.False(store.GetBool("SidePanel/rhythmTapping"));
        Assert.Equal("space", store.GetString("ShortCuts/PlayFromStart"));
        Assert.Equal("=", store.GetString("ShortCuts/Faster"));
        Assert.Equal("-", store.GetString("ShortCuts/Slower"));
        Assert.Equal("{", store.GetString("ShortCuts/NextBook"));
        Assert.Equal("a, b", store.GetString("ShortCuts/Quoted"));
        Assert.Equal((0, 23), store.GetPoint("Window/Pos", (25, 25)));
        Assert.Equal((1280, 729), store.GetSize("Window/Size", (1200, 800)));
        Assert.Equal(new SettingsRect(74, 97, 1035, 522), store.GetRect("Window/NormalGeometry"));
        Assert.False(store.Contains("Window/Geometry")); // binary data of Qt
        Assert.Equal("Internal Sound (FluidSynth)", store.GetString("Midi/Output"));
        Assert.Equal(80, store.GetInt("FluidSynth/masterGainSpin"));
        Assert.Empty(store.GetStringList("Score/Empty"));
        // nothing is written until something changes
        Assert.False(File.Exists(Path.Combine(_folder, "Piano Booster.json")));
    }

    [Fact]
    public void KeepsTheValuesAndTheirTypes()
    {
        string file = Path.Combine(_folder, "s.json");
        var store = SettingsStore.Open(file);
        Assert.Null(store.ImportedFrom);
        store.SetValue("Score/NoteNames", true);
        store.SetValue("Midi/Latency", 12);
        store.SetValue("FluidSynth/masterGainSpin", 0.4);
        store.SetValue("Midi/Input", "None (PC Keyboard)");
        store.SetValue("FluidSynth/SoundFont", new[] { "C:/a.sf2", "C:/b.sf2" });
        store.SetValue("Window/NormalGeometry", new SettingsRect(1, 2, 300, 400));

        var reopened = SettingsStore.Open(file);
        Assert.True(reopened.GetBool("Score/NoteNames"));
        Assert.Equal("true", reopened.GetString("Score/NoteNames"));
        Assert.Equal(12, reopened.GetInt("Midi/Latency"));
        Assert.Equal("12", reopened.GetString("Midi/Latency"));
        Assert.Equal(0.4, reopened.GetDouble("FluidSynth/masterGainSpin"));
        Assert.Equal(0, reopened.GetInt("FluidSynth/masterGainSpin")); // a decimal is not an int
        Assert.Equal("None (PC Keyboard)", reopened.GetString("Midi/Input"));
        // a list as one text, separated by '|' (as MidiDeviceBase.GetStringList reads it)
        Assert.Equal("C:/a.sf2|C:/b.sf2", reopened.GetString("FluidSynth/SoundFont"));
        Assert.Equal(new SettingsRect(1, 2, 300, 400), reopened.GetRect("Window/NormalGeometry"));
        Assert.Equal("default", reopened.GetString("Missing/Key", "default"));
        Assert.Equal(7, reopened.GetInt("Missing/Key", 7));
        Assert.True(reopened.GetBool("Missing/Key", true));
        Assert.False(reopened.GetRect("Missing/Key").IsValid);

        reopened.Remove("Midi/Latency");
        Assert.False(SettingsStore.Open(file).Contains("Midi/Latency"));
    }

    [Fact]
    public void TextsAreConvertedAsQVariantDoes()
    {
        var store = new SettingsStore(null);
        store.SetValue("a", "false");
        store.SetValue("b", "0");
        store.SetValue("c", "");
        store.SetValue("d", "yes");
        store.SetValue("e", " 42 ");
        store.SetValue("f", "4.5");
        Assert.False(store.GetBool("a"));
        Assert.False(store.GetBool("b"));
        Assert.False(store.GetBool("c"));
        Assert.True(store.GetBool("d"));
        Assert.Equal(42, store.GetInt("e"));
        Assert.Equal(0, store.GetInt("f"));
        Assert.Equal(4.5, store.GetDouble("f"));
    }

    [Fact]
    public void ABrokenFileGivesNoSettings()
    {
        string file = Path.Combine(_folder, "broken.json");
        File.WriteAllText(file, "{ not json");
        var store = SettingsStore.Open(file);
        Assert.NotNull(store.LoadError);
        Assert.Empty(store.AllKeys());
    }
}

# PianoBoosterCS

A port of **PianoBooster** to .NET (C#), that includes other features still not developed
in PianoBooster.

PianoBooster is a MIDI file player that helps you learn to play the piano: it shows the
music on a scrolling score, waits for you to play the right notes on a MIDI keyboard, and
tells you how well you played them.

> **Status: work in progress.** The musical core, MIDI input/output with sound (FluidSynth),
> the scrolling score and the main window (side panel, top bar, menus, settings) are ported.
> The song library, the section editor and most dialogs are not yet. See [Roadmap](#roadmap).

## Where it comes from

| Project | What it is |
|---|---|
| [pianobooster/PianoBooster](https://github.com/pianobooster/PianoBooster) | The original PianoBooster, in C++ with Qt and OpenGL (version 1.0.0 at the time of the fork) |
| [gejojimenez/PianoBooster](https://github.com/gejojimenez/PianoBooster), branch [`msys2`](https://github.com/gejojimenez/PianoBooster/tree/msys2) (its default branch) | A fork of the original: native Windows build with MSYS2, plus new features and fixes (versions 1.0.1 to 1.3.11). See its [Changelog](https://github.com/gejojimenez/PianoBooster/blob/msys2/Changelog.txt) |
| **PianoBoosterCS** (this repository) | A port to C# of **version 1.3.11 of the fork**, so it includes the fork's changes |

The fork's changes since 1.0.0 include exact start and end positions of the practised
section, correct bar lines and accidentals with time signature changes, a song library,
named sections of a song, a section editor with a piano roll, note duration trails and
smoother scrolling. The port keeps all of them.

## How the port is done

- **The same behaviour as the C++ code.** The C++ classes are translated one by one, keeping
  their names and structure (`CSong` → `Song`, `CConductor` → `Conductor` ...), so that the
  two can be compared file by file. Improvements come later, separately.
- **Checked against the original.** `tools/RefDump` builds the original C++ core of version
  1.3.11 (without its interface) and writes what it does with a MIDI file: the events read,
  the notes of the score, and a whole song played by a simulated pianist (every MIDI event
  sent, every note judged right, wrong or late). The C# tests run the same scenarios with the
  port and require **exactly the same output**, line by line.
- **Technology:** .NET, [Avalonia](https://avaloniaui.net) for the interface (cross-platform,
  like the original) and [SkiaSharp](https://github.com/mono/SkiaSharp) for the scrolling
  score, instead of Qt and OpenGL 1.1.

The full plan, the decisions and the state of each phase are in [PORTING.md](PORTING.md)
(in Spanish).

## Roadmap

| Phase | | |
|---|---|---|
| 1 | Musical core and reference tests | done |
| 2 | MIDI input/output and sound (FluidSynth), real-time thread, console player | done |
| 3 | Practice window: the scrolling score drawn with Skia | done |
| 4 | Main window: side panel, top bar, settings | done |
| 5 | Song library, sections, section editor, dialogs | |
| 6 | Translations and Windows package | |

## Building

Needs the [.NET SDK](https://dotnet.microsoft.com/download) (9 or later).

```
dotnet build PianoBooster.sln
dotnet test PianoBooster.sln
dotnet run --project src/PianoBooster.App
```

The reference outputs of the C++ code are already in `tests/PianoBooster.Core.Tests/TestData/reference`.
To make them again (only needed if the scenarios or the dump tools change), see
[tools/RefDump/README.md](tools/RefDump/README.md); it needs MSYS2 and the sources of the fork.

## Repository layout

```
src/PianoBooster.Core         the musical core (no interface)
src/PianoBooster.Midi         MIDI devices (DryWetMIDI, FluidSynth) and the conductor thread
src/PianoBooster.Rendering    the scrolling score drawn with SkiaSharp
src/PianoBooster.Data         the settings file (JSON); the song library comes in phase 5
src/PianoBooster.App          the application (Avalonia)
tests/PianoBooster.*.Tests    the tests; Core.Tests has the C# twin of RefDump and the test MIDI files
tools/RefDump                 the original C++ core writing the reference outputs
tools/MakeSyntheticMidi       writes the synthetic test MIDI files
tools/PlaySong, ScoreWindow   a console player and a bare score window (measures the smoothness)
```

## License

PianoBoosterCS is free software, released under the **GNU General Public License, version 3
or (at your option) any later version**, like PianoBooster itself, of which it is a derived
work. See [LICENSE](LICENSE).

The songs of PianoBooster used as test data are under the Creative Commons Attribution
license (CC-BY); see [their README](tests/PianoBooster.Core.Tests/TestData/midi/README.md).
The copyright notices of the original project and of the libraries used are in [NOTICE](NOTICE).

## Credits

PianoBooster was created by Louis James Barman, with Fabien Givors, Marius Kittler and many
other contributors (see [NOTICE](NOTICE)). All the musical logic of this port is theirs.

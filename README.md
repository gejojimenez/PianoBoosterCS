# PianoBoosterCS

A port of **PianoBooster** to .NET (C#), that includes other features still not developed
in PianoBooster.

PianoBooster is a MIDI file player that helps you learn to play the piano: it shows the
music on a scrolling score, waits for you to play the right notes on a MIDI keyboard, and
tells you how well you played them.

> **Status:** the port is complete (the musical core, MIDI and sound, the scrolling score, the
> main window, the song library, the section editor, the dialogs, the translations and the
> Windows package), and the first new feature, [profiles and practice scores](#profiles-and-practice-scores),
> is done. See [Roadmap](#roadmap).

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

## Profiles and practice scores

A feature of PianoBoosterCS that PianoBooster does not have. The plan and the decisions behind
it are in [FEATURES.md](FEATURES.md) (in Spanish).

### Profiles

A profile is a person who practises: each profile keeps its own practices and results. The
first time the program starts, it creates one with the name of the Windows account and makes it
the active one. The button **Profile: &lt;name&gt;** on the right of the top bar, or
**Setup → Profiles...**, opens the list of profiles, where you can create, rename, delete and
choose the active one. The active profile is remembered when the program is closed.

- Deleting a profile also deletes all its practices (it asks first). The last profile cannot be
  deleted, so there is always an active one.
- Changing the profile in the middle of a song discards that practice: it is saved neither for
  the old profile nor for the new one.

### What counts as a whole practice

Only whole songs are saved, not pieces of them. A practice is saved when all of this is true:

- the mode is **Follow You** or **Play Along** (Listen and Rhythm Tap are never saved);
- it starts at the **beginning of the song**: not in a section, not with a loop (start / repeat
  bars), not "play from bar N", and not resuming after a pause in the middle;
- nothing changes while playing: not the **mode**, the **hand**, the **speed**, the
  **instrument** (the part you play) or the **profile**;
- it gets to the **end of the song**. Stopping it, changing the song or closing the program
  saves nothing.

The speed is saved with the practice, but it does not change the score.

### How it is scored

Each note you have to play is worth **10 points**, and errors are counted **per note**, not per
chord: in a chord of three notes, one wrong note is one error and three wrong notes are three.

A note is **on time** if it is played inside the play zone of the program (about a quarter of a
beat before or after the note, scaled with the speed). Just outside the zone there is a window
of the same width on each side: a right note played there is **early** (before) or **late**
(after). A note played further away, or a note that is not in the chord, is a **wrong note**. A
note never played is **missed**.

| | Follow You | Play Along |
|---|---|---|
| Starts at | the maximum | 0 |
| On time | (keeps its 10 points) | +10 |
| Early or late | −5 | +5 (an early note played again on time gets +10) |
| Wrong note | −10 | +0 (saved as an error, but not taken off) |
| Missed | −10 (only with "Follow Through Errors" of the preferences, which skips chords) | +0 (the music does not wait: a beat not played misses all its notes) |
| Lowest | 0 | 0 |

The **maximum** is 10 × the notes of the hand you play (right, left, or both: the two added
together). The song keeps its nominal maximum, with all its notes; each practice keeps its
**real maximum**, the notes that were really asked for (a small keyboard or a transposition can
leave some notes out), and the percentage is the score over that real maximum, rounded down
(100 % only for a perfect practice).

### The result

When a whole practice ends, a window shows its result: the song, mode, hand, speed, date and
duration; the score over the real maximum and the percentage; the comparison with the best
practice before of the same song, mode and hand (a new best, the same, or how far below it); the
number of errors of each kind; and every error with its bar, beat, kind, the note expected and the
note played, named as in the score. With **Repeat song** checked the window is not shown (the song
starts again at once), but the practice is saved all the same and is in the history.

### The practice history

**Song → Practice History...** shows the practices of the active profile: one row per song, mode
and hand (how many practices, the best and the last one), and below it the practices of the
chosen row, the newest first and the best in bold. **Show Errors...** (or a double click) opens
the result of a practice with its errors.

### Where it is saved

In `progress.db` (SQLite), next to `library.db` in the settings folder: `%APPDATA%\PianoBooster`,
or the folder of the settings file given with `--settings=<file>`. `library.db` does not change,
so it is still shared with the C++ program. Deleting `progress.db` deletes all the profiles and
practices; a new one is made the next time the program starts.

## Roadmap

| Phase | | |
|---|---|---|
| 1 | Musical core and reference tests | done |
| 2 | MIDI input/output and sound (FluidSynth), real-time thread, console player | done |
| 3 | Practice window: the scrolling score drawn with Skia | done |
| 4 | Main window: side panel, top bar, settings | done |
| 5 | Song library, sections, section editor, dialogs | done |
| 6 | Translations and Windows package | done |
| 7 | Profiles and practice scores ([FEATURES.md](FEATURES.md), in Spanish) | done |

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

A portable zip for Windows (the program with the .NET runtime, FluidSynth and a SoundFont), in
`artifacts/package`; it needs FluidSynth from MSYS2 (`pacman -S mingw-w64-ucrt-x86_64-fluidsynth`) and
`FluidR3_GM.sf2` (in `Music\Soundfonts` of the user, or given with `-SoundFont`; `-NoSoundFont` for none):

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools/package-windows.ps1
```

The translations come from the `.ts` files of the C++ code: `tools/ts-to-resx.ps1` makes them again.

## Repository layout

```
src/PianoBooster.Core         the musical core (no interface)
src/PianoBooster.Midi         MIDI devices (DryWetMIDI, FluidSynth) and the conductor thread
src/PianoBooster.Rendering    the scrolling score drawn with SkiaSharp
src/PianoBooster.Data         the settings file (JSON), the song library and the practices (SQLite)
src/PianoBooster.App          the application (Avalonia)
tests/PianoBooster.*.Tests    the tests; Core.Tests has the C# twin of RefDump and the test MIDI files
tools/RefDump                 the original C++ core writing the reference outputs
tools/MakeSyntheticMidi       writes the synthetic test MIDI files
tools/PlaySong, ScoreWindow   a console player and a bare score window (measures the smoothness)
tools/ts-to-resx.ps1          the translations of the C++ code (.ts) to .resx
tools/package-windows.ps1     the portable zip for Windows
music/BoosterMusicBooks.zip   the music course of the C++ code, installed next to the program
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

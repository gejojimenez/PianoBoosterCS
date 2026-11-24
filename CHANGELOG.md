# Changelog

## 1.4.0

- The score fills the width of the window: it is no longer stopped at 1024 pixels wide (now up
  to 4096), so on a wide screen more of the song is shown ahead. The play zone stays where it was,
  and the score follows the window when it is maximized, restored or resized, also while playing.
- The piano keyboard (View > Show Piano Keyboard) follows the width of the score when the window
  changes size (in C++ its size was fixed the first time it was drawn).
- The version is set only in `Directory.Build.props`.

## 1.3.11

The port to C# of version 1.3.11 of [gejojimenez/PianoBooster](https://github.com/gejojimenez/PianoBooster)
(see [PORTING.md](PORTING.md)).

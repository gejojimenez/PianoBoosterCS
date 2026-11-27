# Changelog

## 1.5.0

- User profiles: each profile keeps its own practices. The first one is created with the name of the
  Windows account; the "Profile:" button of the top bar and Setup > Profiles... create, rename, delete
  and choose them.
- Practice scores: a whole song practised in Follow You or Play Along (from the start, without a
  section, loop or "play from bar", without changing mode, hand, speed, instrument or profile, and up
  to the end) is saved with its score and every error (bar, beat, early, late, wrong or missed note,
  the note expected and the note played). Each note is worth 10 points; Follow You starts at the
  maximum and takes points off, Play Along starts at 0 and adds them (half for early or late notes).
- At the end of a whole song, the result: score, percentage of the real maximum, the errors by kind
  and one by one, and the comparison with the best one before (not shown with "Repeat song").
- Song > Practice History...: the practices of the active profile by song, mode and hand, with their
  errors.
- The progress is saved in progress.db, next to library.db in the settings folder; library.db does not
  change.

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

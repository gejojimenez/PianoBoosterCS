# Port de PianoBooster a .NET (C#)

Documento de referencia del port de PianoBooster 1.3.11 (C++ / Qt 6 / OpenGL) a .NET.
Está pensado para poder retomar el trabajo en una sesión nueva, sin el contexto de las
anteriores: cada fase indica qué se hizo y dónde está. **Actualiza la sección "Estado" al
terminar cada fase.**

- Original (C++): `C:\Git\PianoBosterMigration\PianoBooster` (fuentes en `src\`). Es la versión
  1.3.11 del fork https://github.com/gejojimenez/PianoBooster (rama `msys2`), que parte de la
  1.0.0 de https://github.com/pianobooster/PianoBooster.
- Port (C#): `C:\Git\PianoBosterMigration\PianoBoosterCS`
- Licencia: el port es una obra derivada y sigue siendo **GPLv3**.

---

## 1. Análisis del original

Unas 21.000 líneas de C++ propio (sin contar `3rdparty/rtmidi`).

| Bloque | Archivos | Qué hace | Port |
|---|---|---|---|
| Lógica musical | `MidiFile`, `MidiTrack`, `MidiEvent`, `MidiByteReader`, `Merge`, `Song`, `Conductor`, `Chord`, `Rating`, `Tempo`, `Bar`, `TrackList`, `SongMap`, `StavePosition`, `Notation`, `Symbol`, `Queue`, `Cfg`, `Util` | Lee el MIDI, sigue al pianista, puntúa | Traducción casi literal; apenas usa Qt (QString, QStringList) |
| Ventana de práctica | `GlView`, `Draw`, `Score`, `Scroll`, `Piano` | Partitura que se desplaza | OpenGL 1.1 inmediato (`glBegin/glEnd`, display lists, `glRectf`), FTGL para letras, QPainter para texto |
| Interfaz Qt | `QtWindow`, `QtMain`, `Gui*` (8 `.ui`), `GuiPianoRoll` (QPainter), `GuiSectionEditor`, `GuiLibraryDialog`, `GuiSidePanel`, `GuiTopBar` | Ventanas y paneles | Reescritura en XAML |
| MIDI y sonido | `MidiDevice`, `MidiDeviceBase`, `MidiDeviceRt` (RtMidi), `MidiDeviceFluidSynth` | Entrada/salida MIDI, sintetizador | Interfaz + implementaciones |
| Datos | `Library` (SQLite `library.db`), `Settings` (QSettings), `SectionPlayer`, `MidiInfo`, `KeepDisplayOn`, `translations/*.ts` | Biblioteca, secciones, ajustes, idiomas | Microsoft.Data.Sqlite (mismo esquema), JSON, `.resx` |

**Temporización actual:** todo corre en el hilo de la interfaz. Un `QTimer` preciso de 4 ms
(`Cfg::tickRate`) llama a `CSong::task(ticks)`; el dibujo se encadena con `frameSwapped`
(un fotograma por refresco). Los ticks se calculan en ms enteros conservando el resto en
ns (corrección de la 1.3.8). La entrada MIDI se sondea dentro de `task()`.

## 2. Decisión tecnológica

- **.NET**. Se centraliza en `Directory.Build.props` (`TargetFramework`). Ahora es `net9.0`
  porque es el SDK instalado en este equipo; al instalar el SDK de .NET 10 (LTS) basta con
  cambiarlo a `net10.0` en ese fichero.
- **Interfaz: Avalonia** (XAML, multiplataforma, dibuja con Skia en la GPU).
- **Ventana de práctica: SkiaSharp** mediante `ICustomDrawOperation`, con
  `TopLevel.RequestAnimationFrame` (sincronizado con el refresco, equivalente a `frameSwapped`).
  - `glRectf` → `DrawRect`; `GL_LINES/GL_LINE_STRIP` → `DrawLine`/`SKPath`
  - display lists → `SKPicture` (grabar una vez, repetir trasladado)
  - FTGL + QPainter → texto de Skia con `DejaVuSans.ttf`, posición subpíxel, suavizado
- **Descartados:** WPF (ritmo de fotogramas irregular, problemas de "airspace" con GL);
  WinForms + OpenTK (port rápido pero hereda OpenGL 1.1/FTGL); WinUI 3 (solo Windows).

Librerías previstas (todas compatibles con GPLv3):

| Necesidad | Librería |
|---|---|
| Interfaz | Avalonia (MIT) |
| Dibujo | SkiaSharp (MIT) |
| Dispositivos MIDI | DryWetMIDI (MIT) en Windows/macOS; RtMidi (API C) en Linux más adelante |
| Lectura de archivos MIDI | **Port propio** de `MidiFile`/`MidiTrack` (tiene correcciones que afectan a la puntuación) |
| Sintetizador | FluidSynth por P/Invoke (LGPL, DLL); alternativa MeltySynth (MIT, C# puro) + NAudio |
| Base de datos | Microsoft.Data.Sqlite (reutiliza `library.db`) |

## 3. Arquitectura

```
PianoBoosterCS/
├─ PianoBooster.sln
├─ Directory.Build.props        TargetFramework, Nullable, LangVersion
├─ src/
│  ├─ PianoBooster.Core         Sin interfaz: MIDI, Song, Conductor, Rating, SongMap...
│  ├─ PianoBooster.Midi         IMidiInput/IMidiOutput + implementaciones          (fase 2)
│  ├─ PianoBooster.Rendering    Draw/Score/Scroll/Piano sobre SKCanvas              (fase 3)
│  ├─ PianoBooster.Data         Biblioteca SQLite, ajustes JSON                     (fase 5)
│  └─ PianoBooster.App          Avalonia                                            (fase 4)
├─ tests/
│  └─ PianoBooster.Core.Tests   xUnit: pruebas de referencia contra el C++
└─ tools/
   └─ RefDump/                  Volcador C++ (compila los .cpp originales sin interfaz)
```

**Hilos (a partir de la fase 2):**
1. Hilo del `Conductor`, alta prioridad, `Stopwatch` + temporizador de alta resolución,
   ms enteros conservando el resto (igual que `CGLView::updateMidiTask`).
2. Entrada MIDI por cola (como `Queue.h`), consumida por el hilo del Conductor.
3. El dibujo lee una "foto" inmutable del estado en cada fotograma.

Hasta entonces, el Core es de un solo hilo, como el original, y se maneja llamando a
`Song.Task(ticks)`.

## 4. Fases

1. **Core y pruebas de referencia.** Port de la lógica musical. Volcador C++ que, para un
   conjunto de MIDI, escribe eventos leídos, cola de acordes, eventos de salida y
   puntuación con una secuencia de notas simulada; el C# debe producir exactamente lo mismo.
2. **MIDI y sonido.** `IMidiInput`/`IMidiOutput`, DryWetMIDI, FluidSynth. Reproducir un
   archivo sin interfaz (programa de consola) y hilo del Conductor.
3. **Ventana de práctica con Skia.** Partitura, desplazamiento, barra de precisión,
   teclado. Medir la fluidez como en la 1.3.8 (desviación de un movimiento uniforme).
4. **Ventana principal.** Panel lateral, barra superior, ajustes, menús.
5. **Resto de la interfaz.** Biblioteca, secciones, editor de secciones y piano roll
   (QPainter → Skia), diálogos de MIDI, teclado, preferencias, detalles de canción.
6. **Cierre.** Traducciones (`.ts` → `.resx`, script), `dotnet publish` autocontenido
   (sustituye a `tools/package-windows.sh`).

## 5. Reglas del port

- Mantener nombres y estructura del original (clase `CSong` → `Song`, método `task` →
  `Task`) para poder comparar archivo a archivo. Cada archivo C# indica su origen.
- No "arreglar" comportamientos durante el port: primero igual que el C++ (las pruebas de
  referencia lo verifican); las mejoras, después y anotadas.
- Aritmética: respetar `int`/`float`/`double` del original (afecta a posiciones y tiempos).
- Qt → .NET: `QString` → `string`, `QStringList` → `List<string>`, `qint64` → `long`,
  `QElapsedTimer` → `Stopwatch`.

## 6. Estado

| Fase | Estado | Notas |
|---|---|---|
| 1 | **hecha** (22 nov 2026) | 79 pruebas en verde: 72 escenarios idénticos al C++ y 7 unitarias. Ver §7 |
| 2 | pendiente | Ver §8 |
| 3 | pendiente | |
| 4 | pendiente | |
| 5 | pendiente | |
| 6 | pendiente | |

## 7. Fase 1: lo hecho

### Comandos

```powershell
dotnet build PianoBooster.sln
dotnet test tests\PianoBooster.Core.Tests
```

Las referencias del C++ ya están en `tests/PianoBooster.Core.Tests/TestData/reference`; solo se
regeneran si cambia el C++, el volcador o los escenarios (ver `tools/RefDump/README.md`: se
compila con MSYS2 y se ejecuta `tools/RefDump/generate-references.ps1`).

### Proyecto `src/PianoBooster.Core`

| Fichero C# | Origen C++ | Notas |
|---|---|---|
| `MidiEvent.cs` | `MidiEvent.h`, constantes de `Util.h` | `struct`; el tipo se guarda XOR `Midi.None` para que `default` sea un evento vacío |
| `CircularQueue.cs` | `Queue.h` | `Push` devuelve el índice del hueco (en C++ un puntero); `IndexRef`/`SlotRef` dan `ref` |
| `MidiTrack.cs` | `MidiTrack.*` | `MidiStream` imita `std::fstream` (fallo pegajoso, EOF = -1). `dword_t` = `uint` (como en Windows) |
| `MidiFile.cs` | `MidiFile.*`, `Merge.*` | Sin `QMessageBox`: el error queda en `MidiError` |
| `Chord.cs` | `Chord.*` | `Note`, `Chord` (`struct` con `[InlineArray]`, copia por valor como en C++), `FindChord` |
| `Tempo.cs`, `Bar.cs`, `Rating.cs` | mismos nombres | `BarPosition` es `struct` comparable |
| `StavePosition.cs`, `Symbol.cs` | mismos nombres | |
| `Notation.cs` | `Notation.*` | `Slot` (`[InlineArray]`), `NoteState` (el puntero `backLink` es un índice) |
| `Piano.cs` | `Piano.*` | Solo el modelo; el dibujo, fase 3 |
| `Scroll.cs`, `Score.cs` | mismos nombres | Solo el modelo (colas, colores, tiempos). Los id de *display list* se emulan con números; `Scroll.Revision` sube cuando el C++ recompilaría una ranura (para el renderizador) |
| `Conductor.cs`, `Song.cs` | mismos nombres | El dispositivo MIDI es la propiedad `MidiDevice` (`IMidiDevice`); en C++ `CConductor` hereda de `CMidiDevice`. Los métodos que `CSong` oculta usan `new`, no `virtual`, como en C++ |
| `TrackList.cs` | `TrackList.*` | Sin `QListWidgetItem` (`changeListWidgetItemView` irá a la interfaz). `TrackList.Translate` para traducir |
| `SongMap.cs` | `SongMap.*`, `MidiByteReader.h` | `std::stable_sort` → `OrderBy` (estable) |
| `Cfg.cs` | `Cfg.*`, `Util.h` | `PbColor` (= `CColor`), `Speed` (`deltaAdjust`), `Log`, `KeepDisplayOn` (acciones que pondrá la app) |
| `Interfaces.cs` | parte de `Settings.h`, `MidiDevice.h`, estáticos de `CDraw` | `IPianoSettings` (+ `MemorySettings`), `IMidiDevice`, `DrawState`, `CoreStatics.Reset()` |

Los estáticos del C++ siguen siendo estáticos. `CoreStatics.Reset()` los devuelve al estado de un
proceso nuevo (lo usan las pruebas, que no se ejecutan en paralelo).

### Pruebas y herramientas

- `tools/RefDump/`: el núcleo C++ original con stubs (`stubs/`) y el driver `RefDump.cpp`.
- `tests/PianoBooster.Core.Tests/RefDriver.cs`: el mismo driver en C#. `ReferenceTests` compara
  línea a línea; si falla, deja la salida C# en `%TEMP%\PianoBooster.Core.Tests\<escenario>.actual.txt`.
- `TestData/midi`: las 15 canciones de `BoosterMusicBooks.zip` (prefijos `bc` = curso de
  principiante, `bm` = Booster Music) y 2 sintéticas (`sy01`: 4/4 → 12/8 → 3/4, bemoles, cambio de
  tempo, running status, nota reatacada, nota sin note off, velocidad 0, dos pistas en un canal,
  batería; `sy02`: 4 sostenidos, ppqn 480, manos en los canales 3 y 4). Las sintéticas las escribe
  `tools/MakeSyntheticMidi` (`dotnet run --project tools/MakeSyntheticMidi -- <carpeta>`).
- `TestData/scenarios.txt`: 72 escenarios (eventos, mapa de la canción, información, notación y
  16 partidas: seguir, acompañar, escuchar, ritmo, una mano, transporte, bucle y desde un
  compás, velocidad, volumen, latencia, estelas, errores que hacen avanzar ...).
- Comprobado que la comparación detecta diferencias: cambiar un `0.01f` de la tabla de precisión
  hace fallar 8 partidas.

### Diferencias deliberadas con el C++ (solo en casos de error)

- Un fragmento que no empieza por `MTrk` (o una pista demasiado larga): el C++ se cuelga
  (puntero nulo); el C# lo trata como error (`CorruptedMidiFile`/`TooManyTracks`).
- `CPiano::addNoteNameItem` lee `m_noteNameList[-1]` cuando la nota es la más baja; el C# no.
- Fichero que no se puede abrir: `MidiError.CannotOpenFile` en lugar del `QMessageBox`.

### Fallos encontrados en el original (no corregidos; anotados para después)

- `Draw.cpp` no compila con `NO_USE_FTGL` (literales `ff`).
- `CMidiDevice::openMidiPort(MIDI_INPUT)` asigna `m_selectedMidiOutputDevice` en lugar del de
  entrada (revisar en la fase 2).
- `CPiano` no inicializa `m_noteNameListLength` ni `m_rhythmTapping` en el constructor (sin efecto
  porque se llama a `clear()` antes de usarlos).

## 8. Para retomar: fase 2

Leer este documento y `tools/RefDump/README.md`. Luego:

1. Proyecto `src/PianoBooster.Midi` con implementaciones de `IMidiDevice` (de `Interfaces.cs`):
   - Entrada/salida con DryWetMIDI (`Melanchall.DryWetMidi`), equivalente a `MidiDeviceRt.cpp`
     (lista de puertos, abrir/cerrar por nombre, la entrada en una cola como `Queue.h`).
   - FluidSynth por P/Invoke, equivalente a `MidiDeviceFluidSynth.cpp` (unas 15 funciones; ajustes
     `FluidSynth/*` de `Settings`), y un `MidiDevice` que elige entre ambos como `MidiDevice.cpp`.
2. El hilo del Conductor (§3): llamar a `Song.Task(ms)` con ms enteros conservando el resto, como
   `CGLView::updateMidiTask()`; la entrada MIDI llega en otro hilo a la cola.
3. Un programa de consola que cargue un MIDI y lo reproduzca (`listen`) por el sintetizador, y que
   permita tocar con un teclado MIDI (`follow`), mostrando aciertos y fallos en texto.
4. Las pruebas de la fase 1 deben seguir en verde (el Core no debería cambiar).

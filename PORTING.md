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
- **Interfaz: Avalonia** (XAML, multiplataforma, dibuja con Skia en la GPU). Desde la fase 4,
  **Avalonia 12.1.6** con **SkiaSharp 3.119.4** (la que usa; ver §10).
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
│  ├─ PianoBooster.Rendering    Draw/Score/Scroll/Piano/GlView sobre SKCanvas       (fase 3)
│  ├─ PianoBooster.Data         Ajustes JSON (fase 4); biblioteca SQLite            (fase 5)
│  └─ PianoBooster.App          Avalonia: ventana principal, ajustes de CSettings   (fase 4)
├─ tests/
│  ├─ PianoBooster.Core.Tests   xUnit: pruebas de referencia contra el C++ (+ MidiInfo)
│  ├─ PianoBooster.Midi.Tests   xUnit: mensajes MIDI, FluidSynth sin SoundFont, hilo del Conductor
│  ├─ PianoBooster.Rendering.Tests  xUnit: fotogramas de canciones dibujados en un bitmap (PNG)
│  └─ PianoBooster.Data.Tests   xUnit: ajustes, importación del .ini del C++ y biblioteca SQLite
└─ tools/
   ├─ RefDump/                  Volcador C++ (compila los .cpp originales sin interfaz)
   ├─ MakeSyntheticMidi/        Escribe los MIDI sintéticos de las pruebas
   ├─ PlaySong/                 Consola: reproducir (listen) o tocar (follow...) un MIDI  (fase 2)
   └─ ScoreWindow/              Ventana Avalonia mínima con la partitura; mide la fluidez (fase 3;
                                usa el ScoreView de la app)
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
5. **Resto de la interfaz**, en subfases independientes (una sesión cada una, ver §11):
   5.1 biblioteca SQLite · 5.2 diálogo de la biblioteca · 5.3 `SectionPlayer` y piano roll ·
   5.4 editor de secciones · 5.5 diálogo MIDI · 5.6 teclado · 5.7 preferencias ·
   5.8 detalles de la canción.
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
| 2 | **hecha** (22 nov 2026) | `PianoBooster.Midi` + `tools/PlaySong`; 7 pruebas nuevas en verde y las 79 de la fase 1 sin cambios. Ver §8 |
| 3 | **hecha** (22 nov 2026) | `PianoBooster.Rendering` + `tools/ScoreWindow`; 10 pruebas nuevas en verde y las 86 anteriores sin cambios. Fluidez 0,03 px (C++ 1.3.8: 0,24). Ver §9 |
| 4 | **hecha** (22 nov 2026) | `PianoBooster.App` + `PianoBooster.Data`; Avalonia 12.1.6. 8 pruebas nuevas en verde y las 96 anteriores sin cambios. Ver §10 |
| 5.1 | **hecha** (22 nov 2026) | `PianoBooster.Data.Library` (Microsoft.Data.Sqlite 9.0.11, mismo `library.db`). 10 pruebas nuevas en verde y las 104 anteriores sin cambios. Ver §11.1 |
| 5.2 | pendiente | `GuiLibraryDialog`. Necesita 5.1. Ver §11.2 |
| 5.3 | pendiente | `SectionPlayer` + `GuiPianoRoll`. Ver §11.3 |
| 5.4 | pendiente | `GuiSectionEditor`. Necesita 5.1 y 5.3. Ver §11.4 |
| 5.5 | pendiente | `GuiMidiSetupDialog`. Ver §11.5 |
| 5.6 | pendiente | `GuiKeyboardSetupDialog`. Ver §11.6 |
| 5.7 | pendiente | `GuiPreferencesDialog`. Ver §11.7 |
| 5.8 | pendiente | `GuiSongDetailsDialog`. Ver §11.8 |
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
  entrada (**corregido en la fase 2**, ver §8).
- `CPiano` no inicializa `m_noteNameListLength` ni `m_rhythmTapping` en el constructor (sin efecto
  porque se llama a `clear()` antes de usarlos).

## 8. Fase 2: lo hecho

### Comandos

```powershell
dotnet build PianoBooster.sln
dotnet test PianoBooster.sln
dotnet run --project tools\PlaySong -- --list
dotnet run --project tools\PlaySong -- <fichero.mid> --soundfont <fichero.sf2>
dotnet run --project tools\PlaySong -- <fichero.mid> --mode follow --in "0 - <teclado>" --out "1 - Microsoft GS Wavetable Synth"
```

`PlaySong --help` da todas las opciones (modo, mano, velocidad, nivel, desde/hasta compás, ganancia y
frecuencia de FluidSynth, `--seconds` para parar solo). Sin `--in`, las teclas del PC hacen de
piano como en el original (`Song.PcKeyPress`; la consola no avisa al soltar, así que la nota se
suelta a los 250 ms). Escribe el compás, cada nota que toca el pianista y la puntuación cuando cambia.

### Proyecto `src/PianoBooster.Midi`

| Fichero C# | Origen C++ | Notas |
|---|---|---|
| `MidiDeviceBase.cs` | `MidiDeviceBase.h` | Clase abstracta. `EventFromBytes`/`BytesFromEvent`: la conversión de `MidiDeviceRt.cpp`. `GetStringList`: las listas de `QSettings` (como `FluidSynth/SoundFont`) se guardan en un texto separado por `\|` |
| `MidiDeviceDryWet.cs` | `MidiDeviceRt.*` | DryWetMIDI 8.0.3 (Windows y macOS). Nombres "índice - nombre" como en C++ (los ajustes guardados valen). La entrada llega en el hilo de DryWetMIDI a una `ConcurrentQueue`; se ignoran sysex, reloj y active sensing (lo que RtMidi ignora por defecto) |
| `FluidSynthNative.cs` | — | P/Invoke a libfluidsynth 2.x. Busca la DLL en `PIANOBOOSTER_FLUIDSYNTH_DIR`, junto al programa, en el sistema y en `C:\msys64\{ucrt64,mingw64,clang64}\bin` (con ruta completa Windows carga sus dependencias de esa carpeta) |
| `MidiDeviceFluidSynth.cs` | `MidiDeviceFluidSynth.*` | Mismos ajustes y valores por defecto (`sampleRateCombo` 22050, `bufferSizeCombo` 128, `bufferCountCombo` 4, `masterGainSpin` 80, `audioDriverCombo` fuera de Windows); reverb y chorus apagados (con la API anterior a 2.2 si falta la nueva). Solo aparece si hay SoundFont **y** se encuentra la biblioteca. `Translate` traduce "Internal Sound" |
| `MidiDevice.cs` | `MidiDevice.*` | Implementa `IMidiDevice` del Core. `Init(settings)` = `init()` + `setQSettings()` |
| `ConductorThread.cs` | `CGLView::updateMidiTask()`, `timerEvent()` | Ver abajo |

**Hilo del Conductor.** `ConductorThread` llama a `Song.Task(ms)` cada `Cfg.TickRate` (4 ms) en un hilo
de prioridad alta, con ms enteros y el resto en ns, como el C++. En Windows espera con un
temporizador de alta resolución (`CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`) y si no hay, con
`Sleep` y `timeBeginPeriod(1)`. Medido en este equipo: mediana 4,02 ms entre llamadas, 99 % por
debajo de 5 ms, máximo 7,5 ms, sin perder tiempo (la suma de ms coincide con el reloj).
- El `Song` no es seguro entre hilos: todo lo demás (interfaz, dibujo, abrir puertos) usa
  `ConductorThread.Invoke(...)` o `lock (SyncRoot)`. El dibujo solo toma el lock para hacer una
  foto del estado (fase 3, §9).
- `TakeEventBits()` devuelve y borra los bits acumulados (el `m_eventBits` de `CGLView`, que el
  dibujo lee una vez por fotograma). `EventBitsRaised` avisa desde el hilo del Conductor.
- `UpdateMidiTask()` se puede llamar también desde el dibujo, como hace `paintGL()` en C++.
- **Importante:** alguien tiene que llamar a `Score.DrawScrollingSymbols(true)` regularmente (en C++
  lo hace el dibujo). Si no, la cola de la partitura se llena y `Song.Task` deja de leer el
  fichero. `PlaySong` lo hace cada 16 ms.

### Diferencias deliberadas con el C++

- `CMidiDevice::openMidiPort()`: el C++ cierra el puerto (de entrada o de salida) del dispositivo
  de *salida* y, al abrir la entrada, lo elige como dispositivo de salida. No se notaba porque
  siempre se abre la entrada y luego la salida. En C# la entrada y la salida van por separado.
- `CMidiDeviceRt` tiene un único `m_validConnection` para la entrada y la salida (cerrar la entrada
  invalidaba la salida) y no marca el puerto como cerrado en `closeMidiPort()`. En C# la validez es
  solo de la salida y cerrar deja el puerto a -1.
- Si no se encuentra libfluidsynth, el sintetizador interno no se ofrece (en C++ es una opción de
  compilación).

### Sin probar

- Con un teclado MIDI real (no hay ninguno conectado en este equipo; solo salidas:
  `VirtualMIDISynth #1` y `Microsoft GS Wavetable Synth`). Probado `listen` con FluidSynth
  (`FluidR3_GM.sf2`) y con GS Wavetable, y `follow` sin tocar (se para en el primer acorde).
- Las teclas del PC en `PlaySong` (necesitan una consola interactiva).
- macOS y Linux. En Linux DryWetMIDI no tiene dispositivos: queda pendiente RtMidi (API C) por
  P/Invoke detrás de `MidiDeviceBase`.

## 9. Fase 3: lo hecho

### Comandos

```powershell
dotnet build PianoBooster.sln
dotnet test PianoBooster.sln
dotnet run --project tools\ScoreWindow -- <fichero.mid> --names --trail --keyboard
dotnet run --project tools\ScoreWindow -- <fichero.mid> --mode follow --measure 15
```

`ScoreWindow --help` da todas las opciones (modo, mano, salida y entrada MIDI, SoundFont, velocidad,
nombres, estelas, notas de colores, teclado, tamaño, `--direct` para no usar `SKPicture`). Las teclas
del PC hacen de piano (con su tecla soltada de verdad, no como en `PlaySong`), el tabulador toca el
acorde esperado, espacio para/sigue, Esc sale. Con `--measure <s>` mide durante ese tiempo (tras
`--skip`, 2 s por defecto), escribe los resultados en la consola y se cierra.

Las pruebas de dibujo guardan las imágenes en `%TEMP%\PianoBooster.Rendering.Tests\*.png`.

### Proyecto `src/PianoBooster.Rendering` (SkiaSharp 2.88.9, la versión de Avalonia 11.3; sin Avalonia)

| Fichero C# | Origen C++ | Notas |
|---|---|---|
| `GlCanvas.cs` | llamadas de OpenGL 1.1 | Estado como el GL (color actual, grosor, punteado): el sostenido, el bemol y el becuadro se dibujan con el color que haya. `GL_LINES`/`LINE_STRIP`/`LINE_LOOP`/`POLYGON`/`glRectf`/matriz. Coordenadas del C++ (y hacia arriba); el texto se endereza al dibujarlo |
| `Draw.cs` | `Draw.*` | Todos los símbolos con los mismos vértices. Texto de FTGL → `DejaVuSans.ttf` con Skia (subpíxel, mismo tamaño 16 × 1,3 / escala). Lo que lee de estáticos y ajustes (mano mostrada, nombres, colores, estelas) lo pone cada fotograma el `ViewRenderer` |
| `ScrollRenderer.cs` | `Scroll.cpp` (`compileSlot`, `drawScrollingSymbols`) | Cada `CompiledSlot` se graba una vez en un `SKPicture` (= display list) y se dibujan uno tras otro trasladados; recorte de las estelas como los `glClipPlane` |
| `PianoRenderer.cs` | `Piano.cpp` (`drawPianoInput`) | Líneas (punteadas si hay alteración) y nombres de las notas del pianista |
| `ScoreRenderer.cs` | `Score.cpp` (`drawScore`, `drawScroll`, `drawPianoKeyboard`) | Las display lists de la partitura y de los pentagramas son `SKPicture`, regrabadas con `forceCompileRedraw` |
| `ViewRenderer.cs` | `GlView.cpp` (`paintGL`, `resizeGL`, textos, barra de precisión, compás) | Ver abajo. Fuentes de Qt: "Arial" a 2 y 1,2 veces el tamaño de la ventana (9 pt) |
| `FrameSnapshot.cs` | — | La foto de un fotograma |
| `SmoothnessMeter.cs` | — | Desviación respecto a un movimiento uniforme (recta ajustada por mínimos cuadrados en ventanas de 1 s; no cuentan los tramos parados) |
| `fonts/DejaVuSans.ttf` | `src/fonts` | Se copia junto al programa (ver `NOTICE`) |

**Cambio en el Core** (`Scroll.cs`): `CompileSlot` guarda ahora lo que el C++ compila en la display
list: un `CompiledSlot` inmutable (copia de la ranura ya transportada, id de la lista, id de la
siguiente y su traslación). Se borra al sacar la ranura, en `Reset` y al ocultar el scroll
(`glDeleteLists`). El dibujo sigue la cadena de ids como `glCallList`, así que muestra exactamente lo
que mostraría el C++, también lo que no se recompila (por ejemplo, los colores que cambia
`getKeyboardInfo`). También `Scroll.ScrolledTicks` (para medir la fluidez). Las 79 pruebas de
referencia siguen idénticas.

**Fotograma = foto + dibujo.** `paintGL()` se parte en dos:
- `ViewRenderer.PrepareFrame(song, score, settings, updateMidiTask)`, **con el lock del Song**: lo que
  lee o cambia el modelo, en el orden del C++ (precisión `calculateAccuracy`, compás, compás de la
  canción, `updateMidiTask`, teclado `getKeyboardInfo`, `DrawScrollingSymbols(true)`, notas del
  pianista) y devuelve un `FrameSnapshot`. Los `CompiledSlot` no cambian, así que la foto solo copia
  referencias.
- `ViewRenderer.Render(canvas, frame, escala)`, **sin lock**, en el hilo de render de Avalonia.

`ViewRenderer.Resize(w, h)` (= `resizeGL`) cambia estáticos que lee el Conductor: también con el lock.

**`tools/ScoreWindow`** (Avalonia 11.3.23, sin XAML). `ScoreView` pide cada fotograma con
`TopLevel.RequestAnimationFrame` (uno por refresco, como `frameSwapped` en la 1.3.8): toma los bits
de eventos (`UptoBarReached` → `PlayFromStartBar`), hace la foto con el lock y la dibuja con un
`ICustomDrawOperation` (`ISkiaSharpApiLeaseFeature`). Es provisional: en la fase 4 pasa a la app.

### Medidas (este equipo, 60 Hz, escala 100 %, `bm07-AmazingGrace` escuchando, nombres + estelas + teclado, 15 s)

| | con `SKPicture` | sin `SKPicture` (`--direct`) |
|---|---|---|
| Desviación de un movimiento uniforme (hora del refresco) | RMS 0,032 px, máx. 0,062 | RMS 0,031 px, máx. 0,063 |
| La misma con la hora a la que se dibuja | RMS 0,078 px, máx. 0,73 | RMS 0,057 px, máx. 0,24 |
| Lock para la foto | media 0,10 ms, máx. 0,6 | media 0,10 ms, máx. 1,2 |
| Dibujar un fotograma | media 0,29 ms, máx. 0,7 | media 0,32 ms, máx. 3,0 |
| Entre llamadas a `Song.Task` | máx. 5,0 ms | máx. 5,1 ms |

El C++ 1.3.8 medía 0,24 px (antes 0,71). El dibujo no bloquea al Conductor, así que el paso 3 de la
fase (sustituir el lock por una foto) ya no es necesario: el lock solo dura lo que la foto. Grabar
`SKPicture` apenas se nota con Skia; se mantiene porque equivale a las display lists.

### Diferencias deliberadas con el C++

- El viewport: el C++ llama a `glViewport` en `resizeGL()` con tamaños lógicos, y es posible que
  `QOpenGLWidget` lo cambie antes de cada `paintGL()` (no comprobado en el C++). Aquí la partitura
  ocupa el rectángulo que calcula `resizeGL()` (hasta 1024 de ancho, 5 px bajo el borde superior)
  en píxeles lógicos, que es la intención del código. Comparar con el C++ en una ventana de más de
  1024 de ancho.
- Los grosores de línea de OpenGL son píxeles físicos: se multiplican por 1 / escala (a 150 % las
  líneas son tan finas como en el C++). El punteado (0x0f0f) es de 4 px físicos.
- `glClear` → un rectángulo del color de fondo del tamaño de la ventana.
- Sin `DejaVuSans.ttf` el C++ termina (`exit(0)`); el C# usa la fuente por defecto y lo anota.
- La cache de `SKPicture` se vacía si cambia la escala de la pantalla (en C++ el tamaño del texto
  quedaba grabado en las display lists).

### Fallos encontrados en el original (no corregidos)

- `CScore::drawPianoKeyboard` guarda el tamaño del teclado en una variable estática la primera vez
  que lo dibuja: al cambiar el tamaño de la ventana el teclado no cambia (se reproduce igual).
- `CGLView::drawTimeSignature` usa `sprintf` en búferes de 10 caracteres (sin efecto con compases
  normales).

### Sin probar

- Escala de pantalla distinta de 100 % en la ventana real (las pruebas dibujan a 150 % en un bitmap).
- Monitores de más de 60 Hz, macOS y Linux.
- Con un teclado MIDI real (sigue sin haber uno en este equipo); el modo seguir se ha probado con el
  tabulador en las pruebas.

## 10. Fase 4: lo hecho

### Comandos

```powershell
dotnet build PianoBooster.sln
dotnet test PianoBooster.sln
dotnet run --project src\PianoBooster.App
dotnet run --project src\PianoBooster.App -- <fichero.mid>
dotnet run --project src\PianoBooster.App -- --settings=<carpeta>\pruebas.json --Xquit-after=8 --Xscreenshot=<carpeta>\v.png <fichero.mid>
```

El ejecutable se llama `PianoBooster.exe`. Acepta las mismas opciones que el C++ (`-d`, `--log`,
`--Xtick-rate=`, `--lights`, `-X1`...) y tres nuevas para probar sin tocar los ajustes del usuario:
`--settings=<fichero.json>` (otro fichero de ajustes), `--Xquit-after=<s>` (se cierra sola) y
`--Xscreenshot=<fichero.png>` (guarda una imagen de la ventana antes de cerrarse).

### Avalonia 12

Se ha pasado de Avalonia 11.3.23 a **12.1.6** (la app, `tools/ScoreWindow`) y SkiaSharp de 2.88.9 a
**3.119.4** (la que usa Avalonia 12; también en `PianoBooster.Rendering` y sus pruebas):
- `PianoBooster.Rendering` compila con SkiaSharp 3 sin cambios ni avisos y sus 10 pruebas siguen en
  verde. `ICustomDrawOperation` e `ISkiaSharpApiLeaseFeature` no cambian: `ScoreView` es el de la fase 3.
- Avalonia 12 trae paquetes para `net8.0` y `net10.0`; con `net9.0` se usan los de `net8.0`.
- Fluidez (`ScoreWindow`, mismas condiciones que en §9, 15 s): RMS 0,030 px (antes 0,032); con la hora
  del dibujo 0,052 px (antes 0,078); dibujar un fotograma, media 0,40 ms (antes 0,29); lock para la
  foto, media 0,10 ms (igual).

### Proyecto `src/PianoBooster.App` (Avalonia 12.1.6, XAML + código detrás)

| Fichero C# | Origen C++ | Notas |
|---|---|---|
| `Program.cs` | `QtMain.cpp`, `QtWindow::decodeCommandLine()` | Las opciones se leen antes de crear la app; el fichero MIDI se comprueba en la ventana (como en C++). `Log.Output` → `pb.log` con `--log`, si no, la consola |
| `App.axaml(.cs)` | `QApplication` | Tema Fluent claro y compacto (los colores de la lista de pistas, `Qt::darkBlue` y `Qt::lightGray`, son para fondo claro). Ventana maximizada al abrir |
| `MainWindow.axaml(.cs)` | `QtWindow.*`, constructor e `initializeGL()` de `CGLView` | Menús, atajos, ficheros recientes, ayuda, acerca de, atajos del teclado, página del tutor, tamaño y posición de la ventana. Crea `MidiDevice`, `Song`, `Score`, `ConductorThread` y `ScoreView` |
| `SidePanel.axaml(.cs)` | `GuiSidePanel.*` (+ `.ui`), `CTrackList::changeListWidgetItemView()` | Cabecera de la canción, secciones, habilidad, manos, pistas y volúmenes. Los `QGroupBox` son `Border` con título |
| `TopBar.axaml(.cs)` | `GuiTopBar.*` (+ `.ui`) | El "%" del velocímetro es un texto aparte (el sufijo de `QSpinBox` no existe en `NumericUpDown`) |
| `LoopingPopup.axaml(.cs)` | `GuiLoopingPopup.*` (+ `.ui`) | Es el contenido de un `Popup` de la barra superior (en C++ una ventana `Qt::Popup`) |
| `Settings.cs` | `Settings.*` (`CSettings`) | Implementa `IPianoSettings` sobre `SettingsStore`. Mismas claves y valores por defecto |
| `ScoreView.cs` | `GlView.*` (ritmo de fotogramas) | Movido desde `tools/ScoreWindow` (que lo enlaza). Nuevo: `StopTimerEvent`/`StartTimerEvent` y `WarningMessageSource` (el aviso de `getWarningMessage`, leído cada fotograma) |
| `MessageDialog.cs` | `QMessageBox`, `QInputDialog` | Información, aviso, pregunta Sí/No y texto |
| `HtmlText.cs` | texto enriquecido de Qt | Avalonia no tiene control HTML: entiende lo que usa PianoBooster (`h1`-`h3`, `p`, `blockquote`, `b`, `i`, `br`, `a`, `center`, tablas sencillas). Lo usan la ayuda, "acerca de" y la página del tutor |
| `KeepDisplayOnPlatform.cs` | `KeepDisplayOn.cpp` | Da sus acciones a `Core.KeepDisplayOn`. Ver abajo |
| `MidiSetupDialogProvisional.cs` | parte de `GuiMidiSetupDialog::accept()` | **Provisional**: elegir entrada y salida MIDI hasta portar el diálogo en la fase 5 |
| `Strings.cs` | `tr()` | Devuelve el texto en inglés hasta la fase 6 |
| `images/` | `src/images` | `play`, `stop`, `play-from-start`, `flag`, `pianobooster` (.png/.ico) |

**Proyecto `src/PianoBooster.Data`** (sin interfaz; en la fase 5 tendrá también la biblioteca):

| Fichero C# | Origen | Notas |
|---|---|---|
| `SettingsStore.cs` | `QSettings` | Un objeto JSON plano con las claves de `QSettings` (`"Midi/Output"`...). Guarda el tipo (texto, número, sí/no, lista, rectángulo) y convierte al leer como `QVariant` (`toString`, `toInt`, `toBool`, `toStringList`, `toRect`...). Una lista leída como texto da los elementos separados por `\|` (lo que espera `MidiDeviceBase.GetStringList`). Se guarda en cada cambio (fichero temporal y `Move`) |
| `QtIniReader.cs` | formato `IniFormat` de Qt | Lee el `.ini` del C++: secciones, `[General]`, escapes `%XX` y C, comillas, listas con comas, `@Rect`, `@Point`, `@Size`, `@Invalid()`. `@ByteArray`/`@Variant` (binario de Qt) se ignoran |

El fichero es `%APPDATA%\PianoBooster\Piano Booster.json` (en Linux/macOS `~/.config/PianoBooster/`),
junto al `Piano Booster.ini` y al `library.db` del C++. **La primera vez** (si no existe el `.json`)
se importa el `.ini`; el `.ini` no se toca, así que el C++ y el port pueden convivir. Probado con una
copia del `.ini` real de este equipo: salida FluidSynth, SoundFont, recientes y geometría correctos.

**Cambios en otros proyectos:**
- Core: `MidiInfo.cs` (port de `MidiInfo.*`, usa el `MidiByteReader` de `SongMap.cs`) y
  `LibraryTypes.cs` (`SongSettings`, `SongPart` e `ISongLibrary`, la parte de `Library.h` que usan
  la ventana y los ajustes; la implementación SQLite es de la fase 5). `Conductor.OpenMidiPort` pasa a
  público (en C++ `CConductor` hereda de `CMidiDevice` y `openMidiPort` es público).
- Rendering: SkiaSharp 3.119.4.
- `tools/ScoreWindow`: Avalonia 12.1.6 y `ScoreView.cs` enlazado desde la app.

### Hilos

Todo el código de la interfaz corre en su hilo y toma el lock del `Song`
(`ConductorThread.SyncRoot`, `lock (_sync)`) cada vez que lo usa; el lock es reentrante, así que las
llamadas encadenadas del C++ (`TrackList.SetActiveHandsIndex` → `Settings.SetChannelHands` →
`SidePanel.Refresh` → `TrackList.Refresh`) funcionan igual. El hilo del Conductor arranca en
`Opened` (el `m_timer.start()` de `initializeGL()`). Los bits de eventos llegan por
`ScoreView.SongEventUpdated` → `MainWindow.SongEventUpdated` (= `QtWindow::songEventUpdated`).

`KeepDisplayOn`: el Conductor lo llama desde su hilo, pero `SetThreadExecutionState(ES_CONTINUOUS)`
pertenece al hilo que lo llama (en C++ todo era el hilo de la interfaz), así que la app lo pasa al hilo
de la interfaz con `Dispatcher.UIThread.Post`.

### Eventos de Qt en Avalonia

- `activated` de un `QComboBox` (solo lo que elige el usuario) → `SelectionChanged`, que también salta
  cuando el código cambia la selección: se ignoran con un contador `_updating` (como `QSignalBlocker`).
  `toggled` y `valueChanged` saltan en los dos casos, igual que en Qt.
- Atajos (`QAction` con `setShortcut`): un manejador *tunnel* en la ventana, para que tengan
  prioridad sobre el control con el foco, como en Qt. Los de los ajustes (`ShortCuts/...`) se leen
  cada vez: un carácter (`"="`, `"{"`) se compara con el texto de la tecla (así `{` es Mayús+[ como en
  Qt) y lo demás (`"F2"`, `"space"`) con `KeyGesture`. Como el `ShortcutOverride` de Qt, no se usan los
  de un carácter si el foco está en un cuadro de texto, y ninguno mientras hay un menú abierto (en Qt el
  menú se queda el teclado; sin esto la `p` de un menú hacía "PlayPause").
- `keyPressEvent` (teclas del PC como piano) y el filtro de eventos de la barra superior (↑/↓ = velocidad)
  → manejador *bubble* de la ventana, solo si nadie usó la tecla. Las repeticiones se ignoran con el
  conjunto de teclas pulsadas (Avalonia no tiene `isAutoRepeat`).

### Probado

- Las 104 pruebas en verde: 83 del Core (79 de referencia sin cambios + 4 de `MidiInfo`), 7 de Midi,
  10 de Rendering (con SkiaSharp 3) y 4 de Data.
- La app abierta de verdad, con capturas: carga la canción de la línea de órdenes, panel lateral con
  información (compás, tonalidad, tempo, duración, compases), pistas en azul/negrita, aviso
  "ERROR NO SOUND" sin salida; con el `.ini` importado, sin aviso y con FluidSynth. Con `SendKeys`:
  espacio arranca la música (botón en "stop", compases avanzando), `=` ×3 sube la velocidad a 112 %,
  Ctrl+S abre el diálogo MIDI, Alt+H → P los atajos (tabla), Alt+H → H la ayuda (enlaces); clic en
  "Start / Repeat" abre el popup.

### Diferencias deliberadas con el C++

- `stopTimerEvent()`/`startTimerEvent()` paran y arrancan el hilo del Conductor. Al arrancar, el tiempo
  cuenta desde ese momento: el C++ seguía contando y daba al `Song` de golpe el tiempo que el diálogo
  estuvo abierto.
- Geometría de la ventana: Avalonia no deja fijar el tamaño "restaurado" de una ventana maximizada, así
  que solo se aplica antes de mostrarla (el `readSettings()` de `init()` ya no la cambia). La posición
  de Avalonia es la esquina exterior de la ventana y la de Qt la interior: al importar del `.ini` la
  ventana restaurada queda unos píxeles más arriba. Se guarda en píxeles lógicos, como Qt.
- `unzipBoosterMusicBooks()` fuera de Windows descomprime con `System.IO.Compression` en lugar de
  ejecutar `unzip`. En Windows, como en C++, lo hace el instalador.
- Los botones de las secciones usan símbolos (💾, 🗑) en lugar de los iconos estándar del estilo de Qt.
- El botón "play from start" no es *checkable* (en Qt lo es, pero nunca se ve marcado de forma útil).
- La página del tutor (`QTextBrowser`) se muestra con `HtmlText`, que solo entiende un HTML sencillo.
- `refreshTranslate()` solo actualiza los avisos y la página del tutor (las traducciones, fase 6).

### Pendiente de la fase 5 (en la app, marcado en el código)

- Diálogos que aún muestran "not ported yet (phase 5)": biblioteca (Ctrl+B y botón "Library..."),
  editor de secciones (Ctrl+E), detalles de la canción (Ctrl+D), teclado (Ctrl+K) y preferencias
  (Ctrl+P). El diálogo MIDI (Ctrl+S) es el provisional.

### Sin probar

- Un teclado MIDI real (sigue sin haber uno), macOS y Linux, escalas de pantalla distintas de 100 %.
- Abrir un fichero con el diálogo (`StorageProvider`) y los ficheros recientes: no se han automatizado
  (sí `SetCurrentFile`, que se ve en el JSON).
- Las páginas del tutor (`InfoPages`): las canciones de `TestData` no las tienen.
- Los menús contextuales de la lista de pistas (mano derecha/izquierda) y los atajos de libro/canción
  siguiente (necesitan una carpeta de libros).

## 11. Para retomar: fase 5 (subfases)

La fase 5 se divide en subfases que se pueden hacer **cada una en una sesión nueva**, sin el contexto
de las demás. Orden obligado solo donde se indica (5.2 y 5.4 necesitan 5.1; 5.4 necesita 5.3); el resto
es independiente.

### Cómo empezar cualquier subfase

1. Leer de este documento solo: §2, §3, §5, §6, el apartado de la subfase y, de la fase 4 (§10), las
   tablas de ficheros, "Hilos", "Eventos de Qt en Avalonia" y "Pendiente de la fase 5". No hace falta
   leer §7–§9.
2. Rama nueva desde la rama principal con lo de la subfase anterior (`git switch -c phase-5.N-<nombre>`).
3. Leer solo los ficheros C++ del apartado (en `C:\Git\PianoBosterMigration\PianoBooster\src`, solo
   lectura) y los C# que se citan.
4. Convenciones ya fijadas en la fase 4 (no hay que redescubrirlas):
   - Un diálogo = `NombreDialog.axaml` + `.axaml.cs` en `src/PianoBooster.App`, con los mismos nombres de
     controles que el `.ui` (`x:Name`); los manejadores `on_x_y` → `OnXY`. Abrir con
     `await dialog.ShowDialog(this)` desde `MainWindow`.
   - Textos con `Strings.Tr(...)` (inglés hasta la fase 6). `QMessageBox`/`QInputDialog` →
     `MessageDialog`.
   - Ajustes: `Settings` (`Value`, `SetValue`, `Contains`, `StringListValue`; `Settings.Store` para
     `Remove` y rectángulos). Mismas claves que el C++.
   - El `Song` solo con su lock: `lock (_sync)` (`ConductorThread.SyncRoot`). Mientras el diálogo está
     abierto, `MainWindow` ya hace `StopTimerEvent()`/`StartTimerEvent()` donde el C++ lo hace.
   - `activated` de Qt → `SelectionChanged` con el contador `_updating` para ignorar los cambios del código.
5. Sustituir en `MainWindow.axaml.cs` la llamada `NotPortedYet(...)` del diálogo por el diálogo real.
6. Probar: `dotnet build PianoBooster.sln` (0 avisos nuevos), `dotnet test PianoBooster.sln`, y la app con
   `--settings=<scratchpad>\s.json --Xquit-after=<s>` (nunca los ajustes reales del usuario), capturas con
   `--Xscreenshot=` o con PowerShell (`CopyFromScreen`) y teclas con `WScript.Shell.SendKeys` (ver §10,
   "Probado"). Evitar que suene música (`Midi/Output` vacío en el JSON de prueba).
7. Al terminar: en §6 marcar la subfase como hecha, añadir aquí debajo del apartado un "Lo hecho" corto
   (ficheros, diferencias deliberadas, sin probar) y quitar lo que ya no esté pendiente en §10
   ("Pendiente de la fase 5").

### 11.1 Biblioteca SQLite

- **C++:** `Library.h` (132), `Library.cpp` (504). Usa `MidiInfo` (ya portado: `Core/MidiInfo.cs`).
- **C#:** `src/PianoBooster.Data/Library.cs` con `Microsoft.Data.Sqlite`, implementando
  `Core/LibraryTypes.cs` (`ISongLibrary`). Ampliar `ISongLibrary` y los tipos con lo que falte de `CLibrary`
  (`CLibrarySong`, ubicaciones, favoritos, recientes, `songs`, `song`, `storeInfo`, `normalisePath`,
  `fileHash`, `midiFileFilters`), con los mismos nombres.
- **Mismo esquema** que el `library.db` del C++ (`%APPDATA%\PianoBooster\library.db`, que existe en este
  equipo: probar leyendo **una copia**, no el original).
- En `MainWindow` crearla en `SettingsStore.Folder` y quitar `_library = null` (comentario "phase 5").
  Desde ese momento funcionan, ya portados: mano/velocidad/canales por canción, última canción de cada
  carpeta, importación de `pb.cfg` y las secciones del panel lateral (añadir, actualizar, renombrar, borrar).
- Pruebas en `tests/PianoBooster.Data.Tests` (base temporal): canciones, secciones, ajustes por canción,
  canción movida (mismo hash), copia del `library.db` del C++.

#### Lo hecho (5.1)

| Fichero C# | Origen C++ | Notas |
|---|---|---|
| `Data/Library.cs` | `Library.*` (`CLibrary`) | Microsoft.Data.Sqlite 9.0.11, una conexión abierta mientras vive (sin *pooling*, para que el fichero se cierre con `Dispose`). Mismo esquema, `user_version = 3` y las mismas consultas. `MidiFileFilters`, `NormalisePath` y `FileHash` son estáticos aquí |
| `Core/LibraryTypes.cs` | `Library.h` | Nuevo `LibrarySong` (`CLibrarySong`; `LastOpened` es `DateTime?`, `null` = `QDateTime` inválido). `ISongLibrary` tiene ya todo `CLibrary`: `IsOpen`, ubicaciones, `SetFavourite`, `StoreInfo`, `Song`, `Songs`, `Favourites`, `Recent` |
| `App/MainWindow.axaml.cs` | `QtWindow.cpp` | `new Library(store.Folder)` (la carpeta del fichero de ajustes, como el C++); se cierra en `Shutdown()` (`delete m_library`). Desde ahora funcionan mano/velocidad/canales por canción, última canción de cada carpeta, importación de `pb.cfg` y las secciones del panel lateral |

- Pruebas (`LibraryTests`, 10): rutas como Qt, SHA-1, ubicaciones y canciones, favoritos y recientes,
  secciones, ajustes compartidos entre copias, canción movida, última canción de cada carpeta, lotes
  (transacción) y reabrir, base de la versión 2 (añade las columnas) y **una copia** del `library.db`
  del C++ de este equipo (17 canciones, 2 secciones: todo se lee, las fechas tienen el mismo formato
  `yyyy-MM-ddTHH:mm:ss`, las rutas y los hash coinciden con los calculados aquí). Si no hay `library.db`
  la última prueba no comprueba nada.
- Probado en la app (`--settings=` en el scratchpad, sin sonido): crea su `library.db` junto al JSON,
  guarda la canción abierta (título, compases, veces abierta) y la última canción de la carpeta; el
  botón "+" de las secciones está habilitado.

**Diferencias deliberadas con el C++:**
- Los `?` de Qt (`addBindValue` en orden) pasan a `$1`, `$2`... (Microsoft.Data.Sqlite enlaza por nombre).
- Un título o un hash vacíos se guardan como `NULL` (lo que hace Qt con un `QString` nulo; un `QString`
  vacío pero no nulo sería `''`, lo que no cambia nada al leer).
- `NormalisePath`: `Path.GetFullPath` + separadores `/`, sin `/` final y (en Windows) la letra de unidad
  en mayúscula, como las rutas del `library.db` del C++. Fuera de Windows no se ha comparado con Qt.
- `BeginBatch` con un lote ya abierto no hace nada (Qt: `transaction()` falla); `EndBatch` sin lote, tampoco.
- Si no se puede abrir la base, `IsOpen` es `false` y todo devuelve vacío/por defecto (en C++ las consultas
  fallan igual). Sin fichero de ajustes (solo memoria) no hay biblioteca.

**Sin probar:** importar un `pb.cfg` real, añadir/renombrar/borrar secciones desde el panel (diálogos de
texto, no automatizados) y macOS/Linux.

### 11.2 Diálogo de la biblioteca (necesita 5.1)

- **C++:** `GuiLibraryDialog.h` (74), `GuiLibraryDialog.cpp` (394, sin `.ui`) y `QtWindow::openLibrary()`
  (`Library/Initialised`, `CSettings::boosterMusicBooksFolder()`, ya en `Settings.BoosterMusicBooksFolder()`).
- **C#:** `LibraryDialog.axaml(.cs)`; en `MainWindow.OpenLibrary()` sustituir `NotPortedYet`. El botón
  "Library..." del panel ya llama a `OpenLibrary()`.

### 11.3 `SectionPlayer` y piano roll

- **C++:** `SectionPlayer.h/.cpp` (80 + 256; usa `MidiDevice` y `SongMap`), `GuiPianoRoll.h/.cpp`
  (174 + 860; widget con `QPainter`, usa `Chord`, `Library.h` (solo `CSongPart`) y `SongMap`).
- **C#:** `SectionPlayer` en `src/PianoBooster.Midi` (o la app; sin interfaz) y `PianoRoll` como control
  de Avalonia que dibuja en `Render(DrawingContext)` (o Skia como `ScoreView`). `SongMap` ya está en el Core.
- Se puede probar sin el editor: una ventana de prueba o una prueba que dibuje el piano roll en un bitmap.

### 11.4 Editor de secciones (necesita 5.1 y 5.3)

- **C++:** `GuiSectionEditor.h/.cpp` (146 + 670) y `QtWindow::openSectionEditor()`.
- **C#:** `SectionEditor.axaml(.cs)`. `MainWindow.OpenSectionEditor()` ya hace lo de antes y después
  (parar, `ReloadPartsAfterEditing`, volúmenes); falta crear el editor con los argumentos del C++ y, si se
  acepta, `SelectInstrument` + `PractisePart`/`PractiseSelection` (ya en `SidePanel`).

### 11.5 Diálogo MIDI

- **C++:** `GuiMidiSetupDialog.h/.cpp/.ui` (68 + 336 + 410). Usa `MidiDeviceFluidSynth` (ya en
  `src/PianoBooster.Midi`) y la lista de SoundFonts de `Settings` (`GetFluidSoundFontNames`...).
- **C#:** `MidiSetupDialog.axaml(.cs)`; **borrar** `MidiSetupDialogProvisional.cs` y llamar al nuevo en
  `MainWindow.OnShowMidiSetup`. Ojo a la latencia (`Midi/Latency`, `Keyboard/RightSoundPrevious`).

### 11.6 Teclado

- **C++:** `GuiKeyboardSetupDialog.h/.cpp/.ui` (93 + 166 + 352).
- **C#:** `KeyboardSetupDialog.axaml(.cs)`; en `MainWindow.OnShowKeyboardSetup`.

### 11.7 Preferencias

- **C++:** `GuiPreferencesDialog.h/.cpp/.ui` (61 + 181 + 284). Recibe también el `CGLView` (aquí
  `ScoreView`).
- **C#:** `PreferencesDialog.axaml(.cs)`; en `MainWindow.OnShowPreferencesDialog` (después ya llama a
  `RefreshTranslate()` y `Score.RefreshScroll()`). El idioma (`General/lang`) se guarda pero no se aplica
  hasta la fase 6.

### 11.8 Detalles de la canción

- **C++:** `GuiSongDetailsDialog.h/.cpp/.ui` (62 + 96 + 140).
- **C#:** `SongDetailsDialog.axaml(.cs)`; en `MainWindow.OnShowSongDetailsDialog`.

`GuiHighScoreWindow.ui` no lo usa nadie en el C++: no se porta. Cuando no quede ningún `NotPortedYet`
en `MainWindow`, borrarlo y dar la fase 5 por hecha en §6.


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
│  ├─ PianoBooster.Rendering    Draw/Score/Scroll/Piano/GlView sobre SKCanvas       (fase 3)
│  ├─ PianoBooster.Data         Biblioteca SQLite, ajustes JSON                     (fase 5)
│  └─ PianoBooster.App          Avalonia                                            (fase 4)
├─ tests/
│  ├─ PianoBooster.Core.Tests   xUnit: pruebas de referencia contra el C++
│  ├─ PianoBooster.Midi.Tests   xUnit: mensajes MIDI, FluidSynth sin SoundFont, hilo del Conductor
│  └─ PianoBooster.Rendering.Tests  xUnit: fotogramas de canciones dibujados en un bitmap (PNG)
└─ tools/
   ├─ RefDump/                  Volcador C++ (compila los .cpp originales sin interfaz)
   ├─ MakeSyntheticMidi/        Escribe los MIDI sintéticos de las pruebas
   ├─ PlaySong/                 Consola: reproducir (listen) o tocar (follow...) un MIDI  (fase 2)
   └─ ScoreWindow/              Ventana Avalonia mínima con la partitura; mide la fluidez (fase 3)
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
| 2 | **hecha** (22 nov 2026) | `PianoBooster.Midi` + `tools/PlaySong`; 7 pruebas nuevas en verde y las 79 de la fase 1 sin cambios. Ver §8 |
| 3 | **hecha** (22 nov 2026) | `PianoBooster.Rendering` + `tools/ScoreWindow`; 10 pruebas nuevas en verde y las 86 anteriores sin cambios. Fluidez 0,03 px (C++ 1.3.8: 0,24). Ver §9 |
| 4 | pendiente | Ver §10 |
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

## 10. Para retomar: fase 4

Leer este documento. Luego, la ventana principal en Avalonia (`src/PianoBooster.App`):

1. Leer `QtWindow.*`, `GuiSidePanel.*`, `GuiTopBar.*` (y sus `.ui`), `Settings.*` y `QtMain.cpp` del C++.
2. `src/PianoBooster.App`: ventana principal con la vista de la partitura (mover `ScoreView` de
   `tools/ScoreWindow` a la app; `ScoreWindow` puede quedarse como herramienta de medida usándola),
   panel lateral (pistas, mano, modo, compases...), barra superior (velocidad, transporte, compás,
   desde/hasta), menús y atajos de teclado de `QtWindow`.
3. Ajustes: implementación de `IPianoSettings` que guarde en JSON lo que guarda `CSettings` con
   `QSettings` (mismas claves); el mensaje de aviso (`getWarningMessage`) va a
   `ViewRenderer.WarningMessage`. `songEventUpdated` (bits de eventos) → `ScoreView.SongEventUpdated`.
4. Los textos (`tr()`) quedan en inglés hasta la fase 6: `Draw.NoteNames` y los textos de `ViewRenderer`
   ya se pueden cambiar.
5. Valorar Avalonia 12 (hay 12.1.x en NuGet; comprobar qué SkiaSharp usa y si cambia
   `ICustomDrawOperation`): en la fase 3 se usó 11.3 por estabilidad de la API.

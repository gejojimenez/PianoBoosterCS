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
├─ Directory.Build.props        TargetFramework, Nullable, LangVersion, Version
├─ music/BoosterMusicBooks.zip  El curso de música del C++, junto al ejecutable (fase 6.3)
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
│  ├─ PianoBooster.Data.Tests   xUnit: ajustes, importación del .ini del C++ y biblioteca SQLite
│  └─ PianoBooster.App.Tests    xUnit: traducciones (ts-to-resx.ps1 y Strings)          (fase 6.1)
└─ tools/
   ├─ ts-to-resx.ps1            Traducciones del C++ (.ts) → src/PianoBooster.App/translations/*.resx (fase 6.1)
   ├─ package-windows.ps1       Zip portátil para Windows: dotnet publish autocontenido + FluidSynth + SoundFont (fase 6.3)
   ├─ RefDump/                  Volcador C++ (compila los .cpp originales sin interfaz)
   ├─ MakeSyntheticMidi/        Escribe los MIDI sintéticos de las pruebas
   ├─ PlaySong/                 Consola: reproducir (listen) o tocar (follow...) un MIDI  (fase 2)
   ├─ ScoreWindow/              Ventana Avalonia mínima con la partitura; mide la fluidez (fase 3;
   │                            usa el ScoreView de la app)
   └─ PianoRollWindow/          Ventana de prueba del piano roll y el SectionPlayer (fase 5.3; usa
                                los controles de la app)
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
6. **Cierre**, en subfases (una sesión cada una, ver §12): 6.1 script `.ts` → `.resx` y
   `Strings.Tr` · 6.2 aplicar `General/lang` · 6.3 `dotnet publish` autocontenido
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
| 5.2 | **hecha** (22 nov 2026) | `LibraryDialog` (`GuiLibraryDialog`). Sin pruebas nuevas (es interfaz): las 114 siguen en verde; probado en la app. Ver §11.2 |
| 5.3 | **hecha** (22 nov 2026) | `SectionPlayer` (Midi), piano roll y vista general (Rendering + controles en la app), `tools/PianoRollWindow`. 14 pruebas nuevas en verde y las 114 anteriores sin cambios. Ver §11.3 |
| 5.4 | **hecha** (22 nov 2026) | `SectionEditor` (`GuiSectionEditor`). Sin pruebas nuevas (es interfaz): las 128 siguen en verde; probado en la app. Ver §11.4 |
| 5.5 | **hecha** (22 nov 2026) | `MidiSetupDialog` (`GuiMidiSetupDialog`); se borra el provisional. Sin pruebas nuevas (es interfaz): las 128 siguen en verde; probado en la app. Ver §11.5 |
| 5.6 | **hecha** (22 nov 2026) | `KeyboardSetupDialog` (`GuiKeyboardSetupDialog`). Sin pruebas nuevas (es interfaz): las 128 siguen en verde; probado en la app. Ver §11.6 |
| 5.7 | **hecha** (22 nov 2026) | `PreferencesDialog` (`GuiPreferencesDialog`). Sin pruebas nuevas (es interfaz): las 128 siguen en verde; probado en la app. Ver §11.7 |
| 5.8 | **hecha** (22 nov 2026) | `SongDetailsDialog` (`GuiSongDetailsDialog`); se borra `NotPortedYet`. Sin pruebas nuevas (es interfaz): las 128 siguen en verde; probado en la app. Ver §11.8 |
| **5** | **hecha** (22 nov 2026) | Las 8 subfases (§11). Ya no queda ningún diálogo "not ported yet" en la app |
| 6.1 | **hecha** (22 nov 2026) | `tools/ts-to-resx.ps1`, 18 `Strings.<idioma>.resx` (satélites) y `Strings` con `ResourceManager`. Proyecto nuevo `PianoBooster.App.Tests`: 5 pruebas nuevas en verde y las 128 anteriores sin cambios. Ver §12.1 |
| 6.2 | **hecha** (22 nov 2026) | `General/lang` aplicado en vivo (`RefreshTranslate`, `Strings.TranslateTree`), combo de idiomas con los satélites, textos de Rendering/Core con su contexto. 3 pruebas nuevas en verde y las 133 anteriores sin cambios; probado en la app. Ver §12.2 |
| 6.3 | **hecha** (22 nov 2026) | `tools/package-windows.ps1` (sustituye a `tools/package-windows.sh`): zip de 184 MB con el runtime, FluidSynth y `FluidR3_GM.sf2`. Sin pruebas nuevas; las 136 en verde (arreglada una carrera de `ConductorThreadTests`); probado el zip descomprimido. Ver §12.3 |
| **6** | **hecha** (22 nov 2026) | Las 3 subfases (§12). El port está completo |

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
| `MessageDialog.cs` | `QMessageBox`, `QInputDialog` | Información, aviso, pregunta Sí/No, texto y número (`GetInt`, desde 5.5) |
| `HtmlText.cs` | texto enriquecido de Qt | Avalonia no tiene control HTML: entiende lo que usa PianoBooster (`h1`-`h3`, `p`, `blockquote`, `b`, `i`, `br`, `a`, `center`, tablas sencillas). Lo usan la ayuda, "acerca de" y la página del tutor |
| `KeepDisplayOnPlatform.cs` | `KeepDisplayOn.cpp` | Da sus acciones a `Core.KeepDisplayOn`. Ver abajo |
| `Strings.cs`, `StringsLanguage.cs` | `tr()`, `refreshTranslate()` | Las traducciones de `translations/Strings.<idioma>.resx` (§12.1) y el idioma de `General/lang`, en vivo (§12.2) |
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

- Nada: 5.8 portó el último diálogo (detalles de la canción) y se borró `NotPortedYet`.

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

#### Lo hecho (5.2)

| Fichero C# | Origen C++ | Notas |
|---|---|---|
| `App/LibraryDialog.axaml(.cs)` | `GuiLibraryDialog.*` | Mismos nombres sin `m_` (`sources`, `songs`, `search`, `openButton`, `removeLocationButton`) y métodos (`FillSources`, `SourceChanged`, `ShowSongs`, `FilterSongs`, `OpenSelected`...). Los roles de los items (`KIND_ROLE`, `PATH_ROLE`, `SORT_ROLE`) están en las clases `Source` y `SongRow` |
| `App/MainWindow.axaml.cs` | `QtWindow::openLibrary()` | `Library/Initialised` (añade la carpeta del curso la primera vez), abre el diálogo y, si se acepta, `OpenSongFile` + `SetCurrentFile` |

- **Las canciones** (`QTreeWidget` con columnas, que Avalonia no tiene y los paquetes instalados no traen
  `DataGrid`): una cabecera de botones (clic = ordenar, otra vez = al revés, con ▲/▼) sobre un `ListBox`
  virtualizado de filas (`SongRowView`, una `Grid` con las mismas columnas). El orden es estable y usa la
  clave de orden si las dos filas la tienen, si no el texto (`SongItem::operator<`). `resizeColumnToContents`
  mide los textos con `FormattedText`; el nombre se estira y la carpeta tiene 200. Filas alternas con
  `:nth-child(2n)`.
- **Las fuentes** (`TreeView`): las subcarpetas se añaden al desplegar (`TreeViewItem.ExpandedEvent`); hasta
  entonces un hijo invisible hace de `ShowIndicator`.
- Recorrido de carpetas como `QDir`/`QDirIterator`: sin ocultos, extensiones sin distinguir mayúsculas, sin
  seguir enlaces a carpetas (reparse points), subcarpetas por nombre sin distinguir mayúsculas.
- Probado en la app (`--settings=` en el scratchpad, sin sonido, con una carpeta de canciones de `TestData`
  con subcarpetas y una ubicación inexistente): ubicaciones (la inexistente en gris), la canción actual en
  negrita y seleccionada, favorito con clic en la estrella (se guarda), ordenar por duración, buscar (Open se
  desactiva si la actual queda oculta), recientes (por fecha, descendente), favoritos, desplegar subcarpetas,
  clic en el encabezado "Locations" (no cambia), flecha abajo desde "Recent" (salta a la primera ubicación),
  quitar una ubicación (pregunta) y abrir con doble clic.

**Diferencias deliberadas con el C++:**
- El encabezado "Locations" se puede seleccionar en Avalonia: al hacerlo se vuelve a la fuente anterior (con
  el ratón) o se salta por encima (con el teclado). El marco del foco del teclado se queda en el encabezado.
- Las columnas no se pueden redimensionar a mano (en Qt la carpeta sí). El panel izquierdo empieza con 230
  (su mínimo) y se cambia con un `GridSplitter`.
- El recorrido de carpetas lista primero los ficheros de una carpeta y después sus subcarpetas; solo cambia
  qué canciones entran si hay más de 5000.
- Sin fichero de ajustes (no hay biblioteca) se muestra un aviso en lugar del diálogo.
- El cursor de espera se pone en la ventana del diálogo (Qt: `setOverrideCursor`); como en C++, la lectura es
  en el hilo de la interfaz.

**Sin probar:** "Add Location..." (el selector de carpetas del sistema no se ha automatizado), carpetas con
más de 5000 canciones, macOS/Linux.

### 11.3 `SectionPlayer` y piano roll

- **C++:** `SectionPlayer.h/.cpp` (80 + 256; usa `MidiDevice` y `SongMap`), `GuiPianoRoll.h/.cpp`
  (174 + 860; widget con `QPainter`, usa `Chord`, `Library.h` (solo `CSongPart`) y `SongMap`).
- **C#:** `SectionPlayer` en `src/PianoBooster.Midi` (o la app; sin interfaz) y `PianoRoll` como control
  de Avalonia que dibuja en `Render(DrawingContext)` (o Skia como `ScoreView`). `SongMap` ya está en el Core.
- Se puede probar sin el editor: una ventana de prueba o una prueba que dibuje el piano roll en un bitmap.

#### Lo hecho (5.3)

```powershell
dotnet run --project tools\PianoRollWindow -- <fichero.mid> --select 3-6 --soundfont <fichero.sf2>
dotnet run --project tools\PianoRollWindow -- <fichero.mid> --play --quit-after 3 --screenshot <carpeta>\r.png
```

| Fichero C# | Origen C++ | Notas |
|---|---|---|
| `Midi/SectionPlayer.cs` | `SectionPlayer.*` (`CSectionPlayer`) | Recibe `SongMap` e `IMidiDevice`. El `QTimer` de 2 ms del hilo de la interfaz pasa a un hilo propio con el temporizador de alta resolución de `ConductorThread` (`PeriodicWaiter`, ahora `internal`). `PositionChanged` y `Stopped` se envían al `SynchronizationContext` de quien lo crea (el hilo de la interfaz, como las señales de Qt); `Stop()` avisa en el momento. `Dispose()` = destructor (para sin `Stopped`) |
| `Rendering/PianoRoll.cs` | `GuiPianoRoll.*` | Modelos sin interfaz: `PianoRollView` (`CPianoRollView`: estado, zoom, desplazamiento, redondeo, ratón y dibujo en un `SKCanvas`), `SongOverview` (`CSongOverview`, notas en un `SKPicture` por tamaño, como el `QPixmap`), `PianoRollNotes` (`isPractisedNote`, `pianoRollNoteColor`, `noteRange`, colores de las secciones), `Snap` (`CSnap`), `ScrollBarModel` (el `QScrollBar`: valor dentro del rango) y `PianoRollPalette` (los colores de `QPalette`, claro y oscuro) |
| `App/PianoRoll.cs` | widgets de `GuiPianoRoll` | `PianoRollControl` (`DockPanel` con la vista y una `ScrollBar` siempre visible) y `SongOverviewControl`. Dan al modelo el tamaño, el ratón (con captura), la rueda y el tema; graban el dibujo en un `SKPicture` en el hilo de la interfaz y el hilo de render solo lo reproduce |
| `tools/PianoRollWindow` | — | Conecta las dos vistas y el reproductor como lo hará el editor (centrar desde la vista general, rango visible, cursor siguiendo la reproducción, clic en una sección). Teclas en `--help` |

- Pruebas: `SectionPlayerTests` (6, Midi; reloj propio y un dispositivo que guarda lo enviado): notas exactas de
  `[inicio, fin)`, silencio y pedal al final, controladores anteriores al inicio, repetir, velocidad (también
  cambiada a mitad) y el hilo de verdad. `PianoRollTests` (8, Rendering): redondeo, zoom/desplazamiento/cursor,
  selección con el ratón (nueva, mover un borde, clic sin arrastrar), clic en las secciones del carril (la más
  corta si se solapan), colores de las manos y notas atenuadas fuera de la selección en un bitmap, ocultar los
  otros instrumentos y la vista general (PNG en `%TEMP%\PianoBooster.Rendering.Tests`).
- Probado en `PianoRollWindow` (capturas): tema claro y oscuro, cambios de compás en la regla, reproducción con
  el cursor avanzando, sin sonido.

**Diferencias deliberadas con el C++:**
- El reproductor tiene su propio hilo (ver arriba); las señales pendientes de una reproducción anterior se
  descartan al parar o volver a tocar (en Qt no podían llegar tarde porque todo era el mismo hilo).
- Los widgets de Qt se parten en modelo (Rendering) y control (App) para poder probar el dibujo en un bitmap.
- Rueda: Avalonia da pasos; se multiplican por 120 para usar las fórmulas de `angleDelta` de Qt.
- El texto usa la fuente por defecto de Skia a 8 pt (96 ppp), no la del estilo de Qt; el recorte con "…" se
  calcula a mano (`elidedText`).
- La paleta: la clara imita la de Qt en Windows y la oscura la "Fusion" oscura; se elige por el tema de Avalonia.

**Sin probar:** arrastrar con el ratón real en la ventana (la lógica sí, en las pruebas), con sonido de verdad
(FluidSynth o un puerto) y escalas de pantalla distintas de 100 %.

### 11.4 Editor de secciones (necesita 5.1 y 5.3)

- **C++:** `GuiSectionEditor.h/.cpp` (146 + 670) y `QtWindow::openSectionEditor()`.
- **C#:** `SectionEditor.axaml(.cs)`. `MainWindow.OpenSectionEditor()` ya hace lo de antes y después
  (parar, `ReloadPartsAfterEditing`, volúmenes); falta crear el editor con los argumentos del C++ y, si se
  acepta, `SelectInstrument` + `PractisePart`/`PractiseSelection` (ya en `SidePanel`).
- De 5.3: `PianoRollControl.View`, `SongOverviewControl.Overview` y `SectionPlayer` (crearlo en el hilo de la
  interfaz; `Dispose()` al cerrar). `tools/PianoRollWindow/Program.cs` muestra cómo conectarlos.

#### Lo hecho (5.4)

| Fichero C# | Origen C++ | Notas |
|---|---|---|
| `App/SectionEditor.axaml(.cs)` | `GuiSectionEditor.*` (sin `.ui`) | Mismos nombres sin `m_` (`partList`, `startBarSpin`, `infoLabel`, `playButton`...) y métodos (`LoadParts`, `SetSelection`, `ShowSelectionInFields`, `IsDirty`, `ResolveChanges`, `TogglePlay`, `Practise`, `Reject`...). Usa `PianoRollControl`, `SongOverviewControl` y un `SectionPlayer` creado en el constructor (hilo de la interfaz) y liberado en `Closed` (el destructor) |
| `App/MainWindow.axaml.cs` | `QtWindow::openSectionEditor()` | Crea el editor con los argumentos del C++ (lo leído del `Song` bajo el lock), `SetInitialSelection` y, si se acepta, `SelectInstrument` + `PractisePart`/`PractiseSelection`; si no, `ReloadPartsAfterEditing`. Un indicador evita abrir otro con Ctrl+E mientras está abierto |
| `App/MessageDialog.cs` | `QMessageBox::question` con `Save \| Discard \| Cancel` | Nuevo `SaveDiscardCancel` (Esc = Cancel) |

- Los `QMessageBox`/`QInputDialog` de Qt bloquean; aquí se esperan con `await`, así que los métodos que preguntan son
  `async void`. Mientras hay una pregunta abierta (`_asking`) no se atienden el espacio, Esc ni otra pregunta.
- `reject()`: el botón Close, Esc y la X de la ventana (`Closing` se cancela y llama a `Reject`, que pregunta si hay
  cambios y cierra después), como `QDialog::closeEvent()`.
- El atajo del espacio (`QShortcut`) es un manejador *tunnel*: va antes que el control con el foco, salvo en un cuadro
  de texto (los `NumericUpDown`), como el `ShortcutOverride` de `QLineEdit`.
- Probado en la app (`--settings=` en el scratchpad, sin sonido; ratón con `mouse_event`, teclas solo si la ventana
  de delante es de la app): abrir con Ctrl+E, seleccionar arrastrando, campos e información, vista general, "New..."
  (dos secciones, en la lista, el carril y la vista general), cambiar el final con los campos (Save activo y "Changed"),
  Close → pregunta → Cancel (sigue abierto), Esc → pregunta → Discard (no se guarda), Save, espacio sin salida MIDI
  (aviso), "Practise" con una sección (el panel la elige y el bucle va de su inicio a su final) y volver a abrir con
  la sección del panel elegida y centrada.

**Diferencias deliberadas con el C++:**
- El dispositivo es el `MidiDevice` de la ventana (el C++ pasa `m_song`, que es un `CMidiDevice`; `CConductor` no
  redefine `playMidiEvent`, así que es lo mismo).
- Los iconos del botón Play son una imagen y un texto dentro del botón; el "%" de la velocidad es un texto aparte.
- `QDoubleSpinBox` con dos decimales → `NumericUpDown` con `FormatString="0.00"`; el valor se redondea a dos decimales
  al leerlo. El separador decimal es el de la cultura del sistema (como Qt).
- El tamaño inicial es el 85 % del área de cliente de la ventana principal (`parent->size() * 0.85`).

**Fallo encontrado en el original (no corregido):** en `partRowChanged`, si la sección actual tiene cambios y se
contesta "Save", `savePart()` llama a `loadParts(id antiguo)`, que vuelve a marcar en la lista la sección antigua;
después se elige la nueva (selección, carril), pero la lista sigue mostrando la antigua.

**Sin probar:** escuchar de verdad (FluidSynth o un puerto; sin salida solo sale el aviso), "Rename..." y "Delete"
desde el editor (los mismos diálogos que "New..." y el panel), "Practise" con una selección que no es una sección
(`PractiseSelection`), el clic en el carril de secciones con el ratón real, macOS/Linux y escalas distintas de 100 %.

### 11.5 Diálogo MIDI

- **C++:** `GuiMidiSetupDialog.h/.cpp/.ui` (68 + 336 + 410). Usa `MidiDeviceFluidSynth` (ya en
  `src/PianoBooster.Midi`) y la lista de SoundFonts de `Settings` (`GetFluidSoundFontNames`...).
- **C#:** `MidiSetupDialog.axaml(.cs)`; **borrar** `MidiSetupDialogProvisional.cs` y llamar al nuevo en
  `MainWindow.OnShowMidiSetup`. Ojo a la latencia (`Midi/Latency`, `Keyboard/RightSoundPrevious`).

#### Lo hecho (5.5)

| Fichero C# | Origen C++ | Notas |
|---|---|---|
| `App/MidiSetupDialog.axaml(.cs)` | `GuiMidiSetupDialog.*` (+ `.ui`) | Mismos nombres de controles que el `.ui` (`midiSetupTabWidget`, `midiInputCombo`, `midiInfoText`, `soundFontList`, `fluidSettingsGroupBox`, `masterGainSpin`, `latencyFixButton`...) y métodos (`Init`, `SetComboFromSetting`, `RefreshMidiInputCombo`, `UpdateMidiInfoText`, `UpdateFluidInfoStatus`, `Accept`...). Las listas de puertos, del `MidiDevice` de la ventana; el `Song`, con su lock |
| `App/MainWindow.axaml.cs` | `QtWindow::showMidiSetup()` | `new MidiSetupDialog()` + `Init(...)` + `ShowDialog`; se borra `MidiSetupDialogProvisional.cs` |
| `App/MessageDialog.cs` | `QInputDialog::getInt` | Nuevo `GetInt` (`NumericUpDown` con mínimo, máximo y paso; el texto, con `HtmlText` por los `<br>` y `<b>`) |
| `App/Settings.cs` | `QSettings::remove` | Nuevo `Remove(clave)`: la clave y, si es un grupo (`"FluidSynth"`), todas las suyas |

- `accept()` igual que el C++: ajustes de FluidSynth (o `remove("FluidSynth")` sin SoundFont), `saveSoundFontSettings`,
  entrada y salida, rango del piano, avisos, `Midi/Latency` y, si la latencia cambió, silenciar el piano
  (`Keyboard/RightSoundPrevious` ← `RightSound`, `RightSound` = 0) o recuperarlo. Cancelar no guarda nada (pero, como
  en C++, la lista de SoundFonts en memoria sí cambia con Load/Clear; se guarda en el siguiente OK).
- Probado en la app (`--settings=` en el scratchpad, sin sonido: `Midi/Output` vacío y un `.sf2` **vacío y ficticio**
  en `FluidSynth/SoundFont` que nunca se carga; ratón con `mouse_event` absoluto, teclas solo si la ventana de delante
  es de la app): Ctrl+S abre el diálogo; textos de información (sin teclado MIDI, sin salida en rojo, el aviso naranja
  del "Microsoft GS Wavetable Synth"); la salida "Internal Sound (FluidSynth)" en la lista; pestaña FluidSynth con los
  valores del JSON (ganancia, reverb, frecuencia, búferes); el filtro de dígitos de "Sample Rate"; latencia 150 → OK
  (`Midi/Latency` 150, `RightSoundPrevious` 1, `RightSound` 0); Cancel (no cambia nada); Clear (lista vacía, ajustes
  desactivados) → OK (se borran las claves `FluidSynth/*` y queda `FluidSynth/SoundFont` = `[]`); latencia 0 → OK
  (`RightSound` = `RightSoundPrevious`).

**Diferencias deliberadas con el C++:**
- La pestaña FluidSynth se quita si no está la librería de FluidSynth (en C++, al compilar sin `WITH_INTERNAL_FLUIDSYNTH`).
- `midiInfoText` es un `SelectableTextBlock` con un `Run` de color por línea (no un `QTextEdit`); el `"\n"` del aviso
  del GS Wavetable, que en el HTML de Qt es un espacio, es un espacio.
- `QIntValidator` de los combos editables: solo se aceptan dígitos y nunca un número mayor que el máximo (Qt deja
  además estados intermedios menores que el mínimo; aquí también).
- El selector de SoundFont es el `StorageProvider` de Avalonia (filtro `*.sf2 *.sf3`); la ruta se guarda con `/`.

**Fallos encontrados en el original (no corregidos):**
- Al quitar la latencia, `setPianoSoundPatches(previousRightSound, -2)` recibe el número del ajuste (empieza en 1) sin
  el `- 1` que pone `QtWindow::init()`: hasta reiniciar, suena el instrumento siguiente al elegido.
- Si se cambia la latencia dos veces seguidas a un valor > 0, la segunda guarda en `RightSoundPrevious` el 0 de la
  primera (piano ya silenciado) y, al volver a 0, el piano queda sin sonido (visto en la prueba).
- `on_fluidClearButton_clicked` no llama a `updateMidiInfoText()`: si la salida era FluidSynth, el texto sigue diciendo
  esa salida hasta cambiar algo.

**Sin probar:** cargar un SoundFont de verdad con "Load" (el selector de ficheros no se ha automatizado y no debía
sonar), aceptar con FluidSynth o un puerto como salida, un teclado MIDI real (el texto de "MIDI Through" y el rango
`Keyboard/LowestNote`/`HighestNote`), macOS/Linux (controladores de audio) y escalas distintas de 100 %.

### 11.6 Teclado

- **C++:** `GuiKeyboardSetupDialog.h/.cpp/.ui` (93 + 166 + 352).
- **C#:** `KeyboardSetupDialog.axaml(.cs)`; en `MainWindow.OnShowKeyboardSetup`.

#### Lo hecho (5.6)

| Fichero C# | Origen C++ | Notas |
|---|---|---|
| `App/KeyboardSetupDialog.axaml(.cs)` | `GuiKeyboardSetupDialog.*` (+ `.ui`) | Mismos nombres de controles que el `.ui` (`keyboardInfoText`, `rightSoundCombo`, `wrongSoundCombo`, `rightTestButton`, `lowestNoteEdit`, `resetButton`...) y métodos (`Init`, `UpdateSounds`, `UpdateInfoText`, `Accept`, `Reject`...). Los volúmenes están ocultos, como en C++ ("fixme Hide for now"). El `Song`, con su lock |
| `App/MainWindow.axaml.cs` | `QtWindow::showKeyboardSetup()` | `new KeyboardSetupDialog()` + `Init(...)` + `ShowDialog`. Como en C++, **no** para el hilo del Conductor: los botones "Test" y las teclas del PC tocan a través de él |

- No hizo falta nada nuevo de 5.5: ni `MessageDialog.GetInt` ni `Settings.Remove` (el diálogo no pregunta ni borra claves).
- Sonidos, igual que el C++: al elegir en un combo, `UpdateSounds()` = `SetPianoSoundPatches(derecho - 1, falso - 1, true)`
  (el índice 0 es "(None)" = sin sonido). `accept()` **no** llama a `SetPianoSoundPatches` (ya se hizo al elegir): guarda
  `Keyboard/RightSound`, `WrongSound` y también `RightSoundPrevious` con el índice del combo (sin `- 1`, como lee
  `QtWindow::init()`). `reject()` (Cancel y la X) vuelve a los del ajuste con el `- 1`. El rango (`LowestNote`/`HighestNote`)
  solo se guarda si la entrada no es "None"; con el teclado del PC se muestra 58–75 desactivado.
- `pressed()`/`released()` de los botones "Test" → cambios de `Button.IsPressed` (ratón o espacio). `editingFinished` →
  Enter o perder el foco. La máscara `"000; "` → solo dígitos y `MaxLength` 3. El texto con `<b>` y color, con `Run`.
- `keyPressEvent`/`keyReleaseEvent`: manejador *bubble* de la ventana para las teclas que no usó ningún control (no con el
  foco en un cuadro de texto ni Tab), con el conjunto de teclas pulsadas para las repeticiones.
- Probado en la app (`--settings=` en el scratchpad, sin sonido: `Midi/Output` vacío; ratón con `mouse_event` absoluto,
  clic solo si la ventana bajo el ratón es de la app, teclas solo si la de delante es de la app): Ctrl+K abre el diálogo con
  los sonidos del JSON; con "None (PC Keyboard)", rango 58–75 desactivado y el texto del teclado del PC; la lista de
  instrumentos con "(None)"; elegir otro sonido, mantener "Test" (los dos), tecla `x`, Cancel (no guarda nada); OK con otro
  sonido de las falsas (`WrongSound` 9, sin `LowestNote`/`HighestNote`); con otra entrada, rango 0–127 activo y
  "octaves 10 / semitones 7" en negrita; escribir `2a00` (la `a` no entra) + Tab → 127 y el aviso rojo "0 notes"; Reset;
  21 y 108 con Enter (no cierra) → "octaves 7 / semitones 3" → OK (se guardan); Esc no cierra; la X equivale a Cancel.

**Diferencias deliberadas con el C++:**
- `activated` de Qt salta también al volver a elegir el mismo elemento; `SelectionChanged`, no (daría el mismo
  `SetPianoSoundPatches`).
- Enter y Esc no aceptan ni cancelan, como en C++ (su `keyPressEvent` no llama al de `QDialog`, así que van al piano),
  salvo Enter con el foco en un botón, que lo pulsa (como `autoDefault` en Qt).

**Fallo encontrado en el original (no corregido):** con la latencia del diálogo MIDI activa (`Keyboard/RightSound` = 0, el
piano silenciado; ver §11.5), este diálogo muestra "(None)" y OK guarda `RightSoundPrevious` = 0: al quitar la latencia el
piano queda sin sonido (visto en la prueba). Al revés, elegir aquí un sonido con la latencia activa devuelve el sonido al
piano sin quitar la latencia.

**Sin probar:** escuchar de verdad los botones "Test" y las teclas (sin salida no suena nada; no debía sonar), un teclado
MIDI real con el rango guardado, macOS/Linux y escalas distintas de 100 %.

### 11.7 Preferencias

- **C++:** `GuiPreferencesDialog.h/.cpp/.ui` (61 + 181 + 284). Recibe también el `CGLView` (aquí
  `ScoreView`).
- **C#:** `PreferencesDialog.axaml(.cs)`; en `MainWindow.OnShowPreferencesDialog` (después ya llama a
  `RefreshTranslate()` y `Score.RefreshScroll()`). El idioma (`General/lang`) se guarda pero no se aplica
  hasta la fase 6.

#### Lo hecho (5.7)

| Fichero C# | Origen C++ | Notas |
|---|---|---|
| `App/PreferencesDialog.axaml(.cs)` | `GuiPreferencesDialog.*` (+ `.ui`) | Mismos nombres de controles que el `.ui` (`showNoteNamesCheck`, `courtesyAccidentalsCheck`, `timingMarkersCheck`, `followThroughErrorsCheck`, `showColoredNotesCheck`, `followStopPointCombo`, `noteDisplayCombo`, `showTutorPagesCheck`, `languageCombo`) y métodos (`Init`, `InitLanguageCombo`, `Accept`). Recibe el `ScoreView` y, como el C++, no lo usa. El `Song`, con su lock |
| `App/MainWindow.axaml.cs` | `QtWindow::showPreferencesDialog()` | `new PreferencesDialog()` + `Init(...)` + `ShowDialog`; después, como antes, `RefreshTranslate()` y `Score.RefreshScroll()` |
| `App/translations/langs.json` | `translations/langs.json` | Copiado del C++; va a `translations/` junto al ejecutable (como lo instala el C++). Da el nombre nativo de cada idioma |

- `accept()` igual que el C++: `Score/TimingMarkers` (y `CfgTimingMarkersFlag` del `Song`), `Score/NoteNames`,
  `Score/CourtesyAccidentals`, `Tutor/TutorPages`, `Score/FollowThroughErrors`, `Score/ColoredNotes`,
  `Score/StopPointMode` (número, y `CfgStopPointMode`), `Score/NoteDurationTrail`, `General/lang` (el código del combo;
  `""` = idioma del sistema) y `Song.RefreshScroll()`. Cancelar, Esc o la X no guardan nada. No hizo falta nada nuevo
  en `Settings` ni en `MessageDialog`.
- Idioma: `InitLanguageCombo` es el del C++ (busca `translations/` junto al ejecutable, si no en la carpeta de datos;
  lee `langs.json`; añade `<System Language>`, "English" y un elemento por cada `pianobooster_<código>.qm`, salvo
  `blank`). Hasta la fase 6 no hay ficheros de traducción, así que solo salen esos dos más el guardado (ver abajo). La
  fase 6 tendrá que cambiar el patrón `.qm` por lo que use (`.resx`/ensamblados satélite) y aplicar `General/lang` en
  `RefreshTranslate()`.
- Probado en la app (`--settings=` en el scratchpad, `Midi/Output` vacío, sin sonido; ratón con `mouse_event` absoluto,
  clic solo si la ventana bajo el ratón es de la app, teclas solo si la de delante es de la app): Ctrl+P abre el diálogo
  con los valores del JSON (`TimingMarkers`, `StopPointMode` 2 "After the Beat", la estela, `lang` "es" → "Español");
  marcar casillas + Esc (no guarda nada); cambiar todo con ratón y flechas → OK (las 9 claves guardadas) y la partitura
  se actualiza sola (nombres de nota, colores, sin estela); al volver a abrir, los nuevos valores; `<System
  Language>` → `General/lang` = `""`; Esc con un combo desplegado solo lo cierra; Tab + Enter acepta.

**Diferencias deliberadas con el C++:**
- Un `General/lang` guardado que no está en la lista (desde 6.2, uno que no esté instalado) se añade al combo con su
  nombre y queda elegido: en C++ el combo mostraría `<System Language>` y OK lo borraría.
- `AdjustToContents` de los dos combos: su ancho mínimo se calcula midiendo el texto más largo; el diálogo se ajusta al
  contenido (`SizeToContent`) con 411 de ancho mínimo, como el `.ui`.
- Enter con el foco en un combo cerrado lo despliega (Avalonia); en Qt pulsaría OK. Con el foco en otro control, Enter
  acepta (`IsDefault`) y Esc cancela (`IsCancel`), como en un `QDialog`.
- El nombre de un idioma que no está en `langs.json` sale de `CultureInfo` (en C++, `QLocale::nativeLanguageName`).

**Fallo encontrado en el original (no corregido):** si falta `langs.json` o no es válido, el combo queda vacío y OK
guarda `General/lang` = `""` (se pierde el idioma elegido).

**Sin probar:** con ficheros de traducción (no existen hasta la fase 6), las páginas del tutor al quitarlas (las canciones
de `TestData` no tienen), el efecto en el juego de "Follow Through Errors" y del punto de parada (no hay teclado MIDI),
macOS/Linux (carpeta `translations`) y escalas distintas de 100 %.

### 11.8 Detalles de la canción

- **C++:** `GuiSongDetailsDialog.h/.cpp/.ui` (62 + 96 + 140).
- **C#:** `SongDetailsDialog.axaml(.cs)`; en `MainWindow.OnShowSongDetailsDialog`.

`GuiHighScoreWindow.ui` no lo usa nadie en el C++: no se porta. Al no quedar ningún `NotPortedYet`
en `MainWindow`, se ha borrado y la fase 5 está hecha (§6).


#### Lo hecho (5.8)

| Fichero C# | Origen C++ | Notas |
|---|---|---|
| `App/SongDetailsDialog.axaml(.cs)` | `GuiSongDetailsDialog.*` (+ `.ui`) | Mismos nombres de controles que el `.ui` (`songInfoText`, `groupBox_2`, `rightHandChannelCombo`, `leftHandChannelCombo`) y métodos (`Init`, `UpdateSongInfoText`, `Accept`). El `TrackList` del `Song`, con su lock |
| `App/MainWindow.axaml.cs` | `QtWindow::showSongDetailsDialog()` | `new SongDetailsDialog()` + `Init(...)` + `ShowDialog`. Como en C++, no para el hilo del Conductor. Se borra `NotPortedYet` (ya no lo usa nadie) |

- Igual que el C++: los combos tienen "No channel assigned" y `GetAllChannelProgramNames(true)`; se eligen con
  `GetHandTrackIndex(...) + 1`. Con las dos manos iguales (y no "ninguna") o solo una en "ninguna", el texto en rojo y
  OK desactivado. `accept()` = `TrackList.SetActiveHandsIndex(izquierda - 1, derecha - 1)`, que pasa por
  `Settings.SetChannelHands` (los canales se guardan en la canción de `library.db`, no en el JSON; no hay claves nuevas
  de ajustes), refresca el panel y rebobina. Cancelar, Esc o la X no cambian nada.
- Reutilizado de 5.5–5.7: grupo con `Border`, contador `_updating`, ancho mínimo de los combos según el texto más largo
  (sin bajar del 180/150 del `.ui`), `IsDefault`/`IsCancel`. No hizo falta `MessageDialog` ni nada nuevo en `Settings`.
- Probado en la app (`--settings=` en el scratchpad, `Midi/Output` vacío, sin sonido; ratón con `mouse_event` absoluto,
  clic solo si la ventana bajo el ratón es de la app, teclas solo si la de delante es de la app): Ctrl+D con
  `bm07-AmazingGrace` (derecha 4, izquierda 3); las dos iguales → "must be different" en rojo y OK desactivado; una en
  "ninguna" → "must be none" en rojo; Cancel y la X no guardan; derecha 3 / izquierda 4 → OK: el panel pasa a "3R"/"4L"
  y la partitura cambia; las dos en "ninguna" → OK: sin L/R; Tab + Enter acepta; Esc cancela; al reiniciar la app se
  conservan 3R/4L.

**Diferencias deliberadas con el C++:**
- El diálogo se ajusta al contenido al abrirse (388 de ancho mínimo, 265 de alto como el `.ui`) y después ya no cambia
  de ancho al cambiar el texto (sin eso, `SizeToContent` lo encogía con el texto rojo, más corto).
- `activated` → `SelectionChanged` (no salta al volver a elegir lo mismo; daría el mismo texto). Enter con el foco en un
  combo cerrado lo despliega (Avalonia); con el foco en otro control, Enter acepta (si OK está activo) y Esc cancela.

**Fallo encontrado en el original (no corregido):** el texto siempre dice "using MIDI Channels 1" para las dos manos (es
un texto fijo, no el canal elegido).

**Sin probar:** canciones de una sola pista o sin canción cargada (combos solo con "No channel assigned"), el efecto en el
juego con un teclado MIDI real, macOS/Linux y escalas distintas de 100 %.

## 12. Para retomar: fase 6 (subfases)

La fase 6 se divide en tres subfases, cada una **en una sesión nueva** sin el contexto de las demás. Orden
obligado: 6.2 necesita 6.1. La 6.3 es independiente (si se hace después de 6.1, los ensamblados satélite entran
solos en la publicación).

### Lo que hace el C++ (para no tener que volver a mirarlo)

- **Ficheros.** `translations/pianobooster_<código>.ts` (91 idiomas + `pianobooster_blank.ts`, la plantilla) y
  `langs.json` (nombre y nombre nativo por código). Formato TS 2.1: `<context><name>` (la clase: `QtWindow`,
  `GuiSidePanel`, `CDraw`, `CMidiInfo`, `QMessageBox`, `QObject`...) con `<message>` = `<source>` + `<translation>`;
  `type="unfinished"`/`"obsolete"`/`"vanished"` = sin traducir; `numerus="yes"` con varias `<numerusform>`.
  Solo `es` tiene los textos nuevos del fork (531 mensajes; los demás, los 375 de `blank`). Completos (0 sin
  traducir): `de`, `es`, `fr`, `pt`, `sv`, `uk`; casi: `it`, `cs`, `sk`.
- **Qué idiomas se instalan.** `translations/CMakeLists.txt` compila con `lrelease` solo 18: `ca cs da de es fr gl
  is it ja nb nl pl ru sk sv uk zh` (todos si `INSTALL_ALL_LANGS`; nunca `blank`). `package-windows.sh` copia
  esos `.qm` y `langs.json` a `translations/` junto al ejecutable.
- **El mismo texto cambia según el contexto** en 14 idiomas, sobre todo los nombres de nota: `B` → `H` en
  `de`/`cs` en un contexto y `B` en otro; `C`/`F`/`G` → `Do`/`Fa`/`Sol` en `pt`/`uk` en un contexto y no en otro;
  `None` → `Keine`/`Keines`/`Nicht ausgewählt` en `de`. El resto son variantes sin importancia (`Midi`/`MIDI`).
- **Cargar** (`QtWindow::refreshTranslate()`, `QtWindow.cpp:887`; se llama en el constructor y después del diálogo
  de preferencias): `CSettings::selectedLangauge()` = `General/lang` o, si está vacío,
  `QLocale::system().bcp47Name()` hasta el primer `_`. Quita y vuelve a instalar tres `QTranslator`:
  `pianobooster_<idioma>` (en `translations/` junto al ejecutable; en Linux `Util::dataDir()/translations`, en macOS
  `../Resources/translations`; si no, la carpeta del ejecutable), `music_<idioma>` (no existe en este repositorio) y
  `qt_<idioma>` (botones de `QMessageBox`...). Después traduce **en vivo** la ventana principal: la primera vez
  guarda los textos originales (`toolTip`, `whatsThis`, `windowTitle`, `statusTip` de los widgets y `text` de las
  `QAction`) y les aplica `tr()`; luego `m_sidePanel->updateTranslate()`, `m_topBar->updateTranslate()`
  (`retranslateUi` + rehacer los combos), `updateWarningMessages()` y `updateTutorPage()`. Los diálogos se crean cada
  vez, así que salen ya en el idioma nuevo.
- **Combo de idiomas** (`GuiPreferencesDialog::initLanguageCombo`): `<System Language>`, `English` y uno por cada
  `pianobooster_<código>.qm` de la carpeta (salvo `blank`), con el nombre nativo de `langs.json`.
- **Empaquetar** (`tools/package-windows.sh`, MSYS2): `pianobooster.exe`, `BoosterMusicBooks.zip`,
  `fonts/*.ttf`, `translations/*.qm` + `langs.json`, `README.md`, `license.txt`, `gplv3.txt`, `Changelog.txt`;
  `windeployqt6` y, repitiendo `ldd` hasta que no aparezca nada nuevo, todas las DLL de `ucrt64/bin` (FluidSynth y
  sus dependencias). Zip `PianoBooster-<versión>-win64.zip` con `Compress-Archive`. **No lleva SoundFont**: el C++
  usa la primera `*.sf2` de `soundfont/` junto al ejecutable (`CSettings::setupDefaultSoundFont()`, ya portado en
  `Settings.SetupDefaultSoundFont`).

### Cómo empezar cualquier subfase

1. Leer de este documento solo: §2, §3, §5, §6, este §12 entero y, de §10, las tablas de ficheros, "Hilos" y
   "Eventos de Qt en Avalonia". No hace falta leer §7–§9 ni §11 (salvo §11.7 en la 6.2).
2. Rama nueva desde `main` (`git switch -c phase-6.N-<nombre>`).
3. Leer solo los ficheros C++ del apartado (en `C:\Git\PianoBosterMigration\PianoBooster`, solo lectura) y los C#
   que se citan.
4. Reglas de la fase 5: nombres del C++, textos con `Strings.Tr`, mismas claves de ajustes, 0 avisos nuevos en
   `dotnet build PianoBooster.sln` y `dotnet test PianoBooster.sln` en verde.
5. Probar en la app con `--settings=<scratchpad>\s.json` (nunca los ajustes reales), `Midi/Output` vacío (que no
   suene nada) y `--Xquit-after=` largo (900 s si hay que manejarla); ratón con `mouse_event` absoluto
   (`MOUSEEVENTF_MOVE|ABSOLUTE`), clic solo si la ventana bajo el ratón es de la app, teclas solo si la ventana de
   delante es de la app.
6. Al terminar: marcar la subfase en §6, añadir su "Lo hecho" debajo del apartado, commit
   `Port PianoBooster to .NET: phase 6.N, ...` y merge a `main` con fast-forward.

### 12.1 Script `.ts` → `.resx` y `Strings.Tr` con `ResourceManager` (rama `phase-6.1-resx`)

- **C++:** `translations/*.ts`, `translations/CMakeLists.txt` (la lista de 18), `tools/lupdate.sh`; `QObject::tr`,
  `tr(..., nullptr, n)` (plurales) y `QCoreApplication::translate(contexto, texto)`.
- **C#:** nuevo `tools/ts-to-resx.ps1`; `src/PianoBooster.App/Strings.cs`, `PianoBooster.App.csproj`; nuevos
  `src/PianoBooster.App/translations/Strings.<código>.resx`; pruebas en un proyecto nuevo
  `tests/PianoBooster.App.Tests` (referencia a la app; si da problemas por ser `WinExe` de Avalonia, mover
  `Strings` a `PianoBooster.Data` y probarlo en `Data.Tests`; decidirlo al empezar).
- **Script** (PowerShell 5.1, sin herramientas de Qt): lee los `.ts` de la carpeta del C++ (por defecto los 18 de
  `CMakeLists.txt`; `-All` para todos menos `blank`) y escribe un `.resx` por idioma. Se saltan los mensajes sin
  traducir y las traducciones vacías (en Qt también salen en inglés). Claves:
  - `texto` (el `<source>` tal cual) con la traducción del **primer** contexto en que aparece;
  - además `contexto|texto` solo cuando ese texto tiene traducciones distintas según el contexto (los casos de
    arriba); el script los lista al terminar;
  - plurales: `texto#0`, `texto#1`... (una por `<numerusform>`).
  Los códigos de Qt se pasan a culturas de .NET (`zh_HK` → `zh-HK`, `bs_BA` → `bs-BA`, `sr@latin` → `sr-Latn`;
  los que .NET no conozca se avisan y se saltan). Los `.resx` generados se guardan en el repositorio (no hace
  falta el C++ para compilar) y se regeneran con el script.
- **`Strings`:** un `ResourceManager` sobre los `.resx` y una `Culture` (la elige 6.2; hasta entonces la invariante,
  inglés). `Tr(text)` = `GetString(text, Culture) ?? text`. Nuevo `Translate(context, text)`
  (= `QCoreApplication::translate`): busca `contexto|texto` y, si no, `texto`. `TrPlural` busca `texto#k` con la
  regla de plural del idioma (la de Qt para los idiomas con plurales traducidos; hoy solo `es`: 2 formas,
  `n != 1`); si no hay, el inglés de ahora. No cambia ninguna llamada existente. El `.csproj` añade los
  `EmbeddedResource` (MSBuild genera los satélites `<código>\PianoBooster.resources.dll`).
- **Pruebas:** el script sobre un `.ts` pequeño de prueba (sin traducir, obsoleto, plural, conflicto de contexto);
  `Strings` con `es` y `de`: un texto normal, `Translate("CDraw", "B")` en `de`, el plural en `es` y un texto sin
  traducción (sale en inglés). Contar cuántos textos de `Strings.Tr` del C# no están en el `.ts` de `es` (los nuevos
  del port) y anotarlos en "Lo hecho".
- **Sin cambio visible en la app** (la cultura sigue siendo la invariante): bastan las pruebas y ver que arranca.

#### Lo hecho (6.1)

| Fichero C# | Origen C++ | Notas |
|---|---|---|
| `tools/ts-to-resx.ps1` | `lrelease` (`translations/CMakeLists.txt`) | PowerShell 5.1, solo `System.Xml`. `-TsDir` (por defecto `..\..\PianoBooster\translations`), `-OutDir`, `-All`, `-Languages`. Escribe además `Strings.resx` vacío (los recursos neutros = inglés) y borra los `Strings.*.resx` que ya no salen. Avisa si un `<source>` tiene `\|` (hoy ninguno) |
| `App/translations/Strings.<idioma>.resx` | `translations/pianobooster_<código>.ts` | Generados (no editar): los 18 de `CMakeLists.txt`, de 213 (`ca`) a 496 (`es`) textos. MSBuild los incrusta solo; salen `<idioma>/PianoBooster.resources.dll` |
| `App/Strings.cs` | `QObject::tr`, `QCoreApplication::translate`, `QTranslator` | `ResourceManager` + `Culture` (invariante por defecto). `Tr`, `Translate(context, text)`, `TrPlural(text, n)` y `PluralForm` (reglas de los 18 idiomas) |
| `App/PianoBooster.App.csproj` | | Comentario de los `.resx` e `InternalsVisibleTo` para las pruebas |
| `App/SidePanel.axaml.cs`, `App/SectionEditor.axaml.cs` | `tr("%n bar(s)", nullptr, n)` | `TrPlural("%n bar(s)", n)`: misma clave que el `.ts` |
| `tests/PianoBooster.App.Tests/TranslationTests.cs` | | 5 pruebas: inglés, `es`/`de` (y `es-ES` → `es`), por contexto (`de`: `CDraw\|B` = `H`, `GuiTopBar\|B` = `B`), plurales y el script sobre un `.ts` pequeño (sin traducir, `unfinished`, obsoleto, `vanished`, plural, conflicto, entidades, `blank`, `.resx` sobrante) |

- Conflictos por contexto en los 18 (el script los lista): nombres de nota en `cs`/`de` (`B` → `H` en `CDraw`) y `uk`
  (`C`/`F`/`G`/`A`/`B` en solfeo en `CDraw`), `None` en `cs`/`de`, y variantes (`Midi`/`MIDI`, `Form`, `Song:` en `nb`,
  `Drums` y `Whole song` en `es`/`gl`). En `es`, `Bar` y `Beat` salen dos veces en el mismo contexto (`GuiSectionEditor`)
  con mayúscula y minúscula; se queda la primera.
- 168 textos literales de `Strings.Tr`/`TrPlural` en el C#; 15 no están en `pianobooster_es.ts`: `OK`, `Cancel`, `Yes`,
  `No`, `Discard` (en C++ vienen de `qt_<idioma>.qm`, que el zip de Windows no lleva: `--no-translations`) y textos que
  el C++ tampoco tiene en el `.ts` (la tabla de atajos, `Action`/`Key`, `SoundFont Files`, `Drums+M`, el copyright, la
  página de FluidSynth, `The library is not available.`). Salen en inglés, como en el C++.
- Probado: `dotnet build` sin avisos, 133 pruebas en verde, la app arranca igual que antes (en inglés aunque
  `General/lang` sea `es`, hasta 6.2).

**Diferencias deliberadas con el C++:**
- `Tr(text)` no tiene contexto: un texto traducido solo en otro contexto también se traduce (en Qt saldría en inglés).
  Solo cambia algo en los textos de la lista de conflictos, que en 6.2 usan `Translate` con su contexto.
- Las `unfinished` con texto **sí** se usan (lo que hace `lrelease` sin `-nounfinished`; arriba decía que se saltaban):
  26 en `nb`, 6 en `zh`, 0–2 en los demás.
- El panel lateral decía "40 bars"/"1 bar"; ahora, como el C++ sin traducción, "40 bar(s)".

**Sin probar:** las reglas de plural de los idiomas de 3 formas (`cs`, `sk`, `pl`, `ru`, `uk`) con traducciones reales
(ningún `.ts` de esos idiomas tiene plurales traducidos); compilar la app con los `.resx` de `-All` (el script sí:
87 idiomas en una carpeta aparte, `sr@latin` → `sr-Latn`, `zh_HK` → `zh-HK`; salta con aviso `bs_BA`, que en .NET es
`bs-Latn-BA`, y `wa`); el script en PowerShell 7 o fuera de Windows.

### 12.2 Aplicar `General/lang` (rama `phase-6.2-apply-language`, necesita 6.1)

- **C++:** `QtWindow::refreshTranslate()` (`QtWindow.cpp:887`), `CSettings::selectedLangauge()` (`Settings.h:158`),
  `GuiSidePanel::updateTranslate()`, `GuiTopBar::updateTranslate()`, `GuiPreferencesDialog::initLanguageCombo()`,
  `CDraw` (nombres de nota), `CGLView` (`Accuracy:`, `Song:`, `Bar:`), `CConductor`/`CMidiInfo` (`tr("None")`...).
- **C#:** `MainWindow.axaml.cs` (`RefreshTranslate`), `Settings.cs` (`SelectedLangauge`, mismo nombre con la errata),
  `SidePanel.axaml.cs`, `TopBar.axaml.cs`, `PreferencesDialog.axaml.cs` (`InitLanguageCombo`), los demás diálogos
  (traducir sus textos XAML al crearse), `Strings.cs`; en Rendering/Core solo se les dan los textos desde la app
  (`Draw.NoteNames`, `ViewRenderer.AccuracyText/SongText/BarText`, `Conductor.Translate`, `MidiInfo.Translate`).
- **`RefreshTranslate`** como el C++: `Strings.Culture` = `SelectedLangauge()` (vacío → `CultureInfo.CurrentUICulture`;
  `ResourceManager` ya cae de `es-ES` a `es`); los textos de Rendering/Core con `Strings.Translate` y su contexto del
  C++ (`CDraw`, `CGLView`, `CConductor`, `CMidiInfo`); `SidePanel.UpdateTranslate()`, `TopBar.UpdateTranslate()`,
  `UpdateWarningMessages()`, `UpdateTutorPage()`.
- **Textos de los XAML** (unos 190: `Text`, `Content`, `Header`, `Title`, `ToolTip.Tip`, elementos fijos de combos):
  un ayudante `Strings.TranslateTree(control)` que recorre el árbol lógico, guarda la primera vez el texto original
  de cada propiedad (como `listWidgetsRetranslateUi`) y le pone `Tr(original)`. Lo llaman `MainWindow.RefreshTranslate`
  (en vivo, como el C++) y cada diálogo justo después de `InitializeComponent()`, antes de rellenar datos, para no
  tocar nunca nombres de canciones o ficheros. Los textos que el código ya pone con `Strings.Tr` se rehacen en los
  `UpdateTranslate` donde el C++ los rehace.
- **Combo de idiomas:** en vez del patrón `pianobooster_<código>.qm`, un elemento por cada código de `langs.json` para
  el que exista `ResourceManager.GetResourceSet(cultura, true, false)` (funciona también si algún día se publica en
  un solo fichero). Revisar la diferencia deliberada de 5.7 (añadir al combo un `General/lang` que no esté): dejarla
  solo para un idioma no instalado y anotarlo.
- **Probar en la app:** `General/lang` = `es` en el JSON de prueba → menús, panel, barra, diálogos y nombres de nota en
  español; cambiar a `de` en preferencias → la ventana cambia en vivo y la nota `B` sale como en el C++;
  `<System Language>`; un idioma incompleto (`ca`) mezcla inglés donde falta, como Qt.
- **Fuera de alcance** (anotar): `music_<idioma>` (no existe), los textos propios de Avalonia (menú contextual de los
  cuadros de texto), derecha a izquierda (`ar`, `he`, que no están entre los 18).

#### Lo hecho (6.2)

| Fichero C# | Origen C++ | Notas |
|---|---|---|
| `App/StringsLanguage.cs` (nuevo; `Strings` pasa a `partial`) | `refreshTranslate()`, `listWidgetsRetranslateUi`, `retranslateUi()`, `initLanguageCombo()` | `CultureOfLanguage` (código de Qt → cultura: `zh_HK` → `zh-HK`, `sr@latin` → `sr-Latn`; `en`, `""` o desconocido → invariante = inglés), `LanguageOfCulture`, `InstalledLanguages` (carpetas junto al ejecutable con `PianoBooster.resources.dll`) y `TranslateTree` |
| `App/Strings.cs` | | `Translate(context, text, a)` (= `translate(...).arg(a)`) |
| `App/MainWindow.axaml.cs` | `QtWindow::refreshTranslate()` | `Strings.Culture` = `SelectedLangauge()`; `Conductor`/`MidiInfo`/`TrackList.Translate` con `CConductor`/`CMidiInfo`/`QObject`; `Draw.NoteNames` con `CDraw`; `Accuracy:`/`Song:`/`Bar:` de `ViewRenderer` con `CGLView`; `TranslateTree(this)`; `SidePanel`/`TopBar.UpdateTranslate()`; avisos y tutor. El constructor llama a `TranslateTree(this)` justo después de `InitializeComponent()` para guardar los originales. `Cannot open "%1"` con el contexto `QtWindow` |
| `App/SidePanel.axaml.cs` | `GuiSidePanel::updateTranslate()`, `setPartTexts()` | Combo de ritmo, `setPartTexts()` (ahora también "Whole song", como el C++) y cabecera de la canción |
| `App/TopBar.axaml.cs` | `GuiTopBar::updateTranslate()`, `reloadKeyCombo()` | Combo mayor/menor; las tonalidades con el contexto `GuiTopBar` (`B` no es `H`) |
| `App/PreferencesDialog.axaml.cs` | `initLanguageCombo()` | `Strings.InstalledLanguages()` en vez de `pianobooster_*.qm` |
| Los 6 diálogos | `retranslateUi()` (`setupUi`) | `Strings.TranslateTree(this)` tras `InitializeComponent()`. `None` con el contexto de `GuiKeyboardSetupDialog`/`GuiMidiSetupDialog` |
| `tests/PianoBooster.App.Tests/TranslationTests.cs` | | 3 pruebas nuevas: teclas de acceso, códigos de idioma e idiomas instalados (los 18 satélites), y `TranslateTree` con controles reales (guarda originales, no toca lo añadido ni lo cambiado por el código, vuelve al inglés) |

- `TranslateTree(root)`: la primera vez guarda, por cada elemento del árbol lógico, los textos del XAML (`Title`, `Header`
  de `MenuItem`/`HeaderedContentControl`, `Content` de texto de `ContentControl`, `Text` de `TextBlock`, `PlaceholderText`,
  `ToolTip.Tip`) en una `ConditionalWeakTable` (una lista vacía si no tiene textos, para que lo que se añada después
  —canciones, secciones, pistas— nunca se traduzca). Cada llamada pone `Translate(contexto, original)`, salvo si el
  código cambió el texto desde la última vez. El contexto sale del tipo (`MainWindow` → `QtWindow`, `SidePanel` →
  `GuiSidePanel`, `TopBar` → `GuiTopBar`, `LoopingPopup` → `GuiLoopingPopup`, cada diálogo → su `Gui*`), así que
  `Midi Input Device:` usa el de `GuiMidiSetupDialog` y no el de `GuiMidiSettingsDialog`. En `Header`/`Content`, `_` de
  Avalonia ↔ `&` de Qt (`E_xit` → `E&xit` → `Sa&lir` → `Sa_lir`; `MIDI input & output` ↔ `MIDI input && output`).
- Comprobado sin la app: de los 206 textos de los `.axaml`, 24 no están en `pianobooster_es.ts` (`OK`/`Cancel`, que
  en C++ vienen de `qt_<idioma>.qm`; símbolos `+`, `−`, `■`, `💾`...; `%`; la leyenda del editor y el nombre de las pestañas
  `FluidSynth`, que el código o el C++ ponen igual).
- Probado en la app (`--settings=` en el scratchpad, `Midi/Output` vacío, sin sonido, `--Xquit-after=900`; ratón con
  `mouse_event` absoluto, clic solo con la app bajo el ratón, teclas solo con la app delante), `bm07-AmazingGrace` y
  nombres de nota: con `General/lang` = `es` arranca en español (menús con teclas de acceso, panel, barra, aviso rojo,
  `Compás:`, notas `Do`/`Fa`, pistas `Piano de Cola`/`Baterías`, `40 compases`, `(cambia)`); Preferencias en español con
  los 18 idiomas por su nombre nativo; `Deutsch` + OK → la ventana cambia en vivo y se guarda `de`; al reiniciar arranca
  en alemán (`Schlagzeug`, tonalidad `F`, `Takt:`); en vivo a `Español` ("Canción completa" también); diálogo MIDI en
  español (`Entrada & Salida Midi`, `Ninguno`); `Català` mezcla inglés donde falta; `<Idioma del sistema>` guarda `""`
  y sale en español (Windows en `es-ES`); `English` vuelve al inglés desde los originales.

**Diferencias deliberadas con el C++:**
- Se traducen en vivo todos los textos del XAML de la ventana (en C++, los de los `.ui` del panel y la barra con
  `retranslateUi()`, y de la ventana solo `toolTip`/`whatsThis`/`windowTitle`/`statusTip` y el texto de las `QAction`, que
  es casi lo mismo). Un texto que el código ha cambiado se respeta (en C++ `retranslateUi()` lo devuelve al del `.ui`
  hasta el siguiente cambio); "Start / Repeat" se rehace con `UpdateSectionSummary()`.
- Idioma del sistema: `CultureInfo.CurrentUICulture` hasta el primer `-` (ya estaba así en `SelectedLangauge`), y
  `ResourceManager` cae de `es-MX` a `es`. En Qt, `bcp47Name()` de `es-MX` no tiene `_` y `pianobooster_es-MX` no carga.
- El combo de Avalonia muestra una copia del elemento elegido: al traducir "Whole song" se vuelve a elegir.

**Como en el C++ (no es un fallo del port):** al cambiar de idioma en vivo, el combo de tonalidad y la lista de pistas
siguen en el idioma anterior hasta que se carga otra canción (o se cambia mayor/menor); los botones `OK`/`Cancel` y los
nombres GM de instrumentos que un `.ts` no tenga salen en inglés; la traducción alemana de "Mute your part when playing"
lleva un `<br>` literal; `CConductor` compara con su propio `None` (`Nicht ausgewählt`) y el diálogo MIDI pone el suyo
(`Keines`).

**Fallo encontrado en el original (no corregido):** el "smart resize panel" de `GuiSidePanel::updateTranslate()` cambia
el ancho máximo del panel, pero el `QScrollArea` del fork fija su ancho en el constructor, antes de traducir: los textos
largos de Modo/Manos se cortan (en `ca`, "Ambdues"). Aquí igual (310 fijos).

**Sin probar:** los idiomas de derecha a izquierda y los de 3 plurales en la app, el resto de diálogos en un idioma que no
sea `es`/`de`, macOS/Linux y escalas distintas de 100 %.

### 12.3 `dotnet publish` autocontenido para Windows (rama `phase-6.3-publish`)

- **C++:** `tools/package-windows.sh`, `src/CMakeLists.txt` (qué se instala junto al ejecutable),
  `CSettings::setupDefaultSoundFont()`.
- **C#:** nuevo `tools/package-windows.ps1`; `PianoBooster.App.csproj` (propiedades de publicación,
  `BoosterMusicBooks.zip` como contenido: hoy no se copia a la salida), `Directory.Build.props` (versión),
  `README.md`; se mira `FluidSynthNative.cs` (ya busca la DLL en `AppContext.BaseDirectory`) y
  `Settings.SetupDefaultSoundFont` (ya busca `soundfont/*.sf2`).
- **Script** (PowerShell 5.1; `-OutDir`, `-SoundFont <fichero.sf2>`, `-FluidSynthDir` con `C:\msys64\ucrt64\bin` por
  defecto):
  1. `dotnet publish src\PianoBooster.App -c Release -r win-x64 --self-contained` a una carpeta. No un solo fichero
     (las DLL de FluidSynth tienen que ir sueltas, y así es el zip del C++) ni recortado (Avalonia no lo garantiza);
     `ReadyToRun` si no da avisos.
  2. Junto al ejecutable, lo mismo que el C++: `BoosterMusicBooks.zip` (copiado al repositorio desde `music/` del
     C++, 512 KB, para no depender de él), `fonts/DejaVuSans.ttf` (ya lo copia Rendering), `translations/langs.json`,
     los satélites de 6.1, `README.md`, `LICENSE`/`NOTICE` del port y el `Changelog.txt` del C++.
  3. FluidSynth: `libfluidsynth-3.dll` y, repitiendo `objdump -p` (de `ucrt64/bin`) hasta que no aparezca nada
     nuevo, todas sus dependencias de esa carpeta (`libsndfile-1`, `SDL3`, `libportaudio`, `libgomp-1`,
     `libstdc++-6`, `libreadline8`...), como el bucle de `ldd` del C++.
  4. SoundFont: si se da `-SoundFont`, se copia a `soundfont/` (ver decisión abajo).
  5. Zip `PianoBooster-<versión>-win64.zip` (versión de `Directory.Build.props`) con `Compress-Archive`.
- **Probar:** generar el zip, descomprimirlo en el scratchpad y arrancar **esa copia** con `--settings=` en el
  scratchpad y `Midi/Output` vacío: se ve la partitura, salen los idiomas en preferencias y la salida FluidSynth
  aparece en el diálogo MIDI (sin elegirla, para que no suene). Comprobar con `--log` (o la lista de módulos del
  proceso) que la DLL de FluidSynth se carga de la carpeta descomprimida y no de `C:\msys64`.
- **Decisiones a confirmar antes de 6.3:** (a) si el zip lleva una SoundFont por defecto (p. ej. `FluidR3_GM.sf2`,
  licencia MIT, ~141 MB; la de `C:\Users\...\Music\Soundfonts` de este equipo) o solo con `-SoundFont`; (b) si
  `tools/package-windows.ps1` es lo único o también hace falta un `.sh`.

#### Lo hecho (6.3)

Decisiones: (a) el zip lleva `FluidR3_GM.sf2` por defecto (`-NoSoundFont` para no llevarla); (b) solo `.ps1` (el `.sh` del
C++ era para MSYS2; el port se empaqueta desde PowerShell de Windows).

| Fichero | Origen C++ | Notas |
|---|---|---|
| `tools/package-windows.ps1` | `tools/package-windows.sh` | PowerShell 5.1. `-OutDir` (por defecto `artifacts\package`, ignorado por git), `-SoundFont`, `-NoSoundFont`, `-FluidSynthDir` (`C:\msys64\ucrt64\bin`). Pasos de abajo |
| `music/BoosterMusicBooks.zip` (+ `README.md`) | `music/BoosterMusicBooks.zip` | Copiado (512 KB) para no depender del C++ |
| `App/PianoBooster.App.csproj` | `src/CMakeLists.txt` (`INSTALL ... DESTINATION .`) | `BoosterMusicBooks.zip` junto al ejecutable (también al compilar) |
| `README.md` | | Fases 5 y 6 hechas, cómo hacer el zip, nuevos ficheros |
| `tests/PianoBooster.Midi.Tests/MidiTests.cs` | | `ConductorThreadTests.GivesWholeMillisecondsKeepingTheRest`: `clock.Restart()` antes de `thread.Start()`. Con todas las pruebas a la vez fallaba a veces (el hilo contaba 20–30 ms antes que el reloj de la prueba); 4 ejecuciones seguidas en verde |

- Pasos del script: (1) `dotnet publish src\PianoBooster.App -c Release -r win-x64 --self-contained true
  -p:PublishReadyToRun=true -p:DebugType=None` a una carpeta (no un solo fichero, sin recortar); comprueba que están
  `PianoBooster.exe`, `BoosterMusicBooks.zip`, `fonts\DejaVuSans.ttf`, `translations\langs.json` y los satélites; borra
  los `.pdb` nativos de Skia/HarfBuzz (~105 MB) y el `.dylib` de DryWetMIDI. (2) `README.md`, `LICENSE` (GPLv3) y
  `NOTICE` (el C++ copiaba `license.txt`, `gplv3.txt` y `Changelog.txt`; el port no tiene changelog propio). (3)
  `libfluidsynth-3.dll` y sus dependencias de MSYS2 con `objdump -p` en bucle: 18 DLL (`libsndfile-1`, `SDL3`,
  `libportaudio`, `libgomp-1`, `libstdc++-6`, `libFLAC`, `libvorbis`, `libopus`, `libmpg123-0`...). (4) La SoundFont en
  `soundfont/` con un `README.txt` hecho con el bloque INFO del fichero (nombre, autor, copyright, fecha, comentario);
  si el comentario dice "not redistribute", avisa. (5) `Compress-Archive`.
- Resultado: `PianoBooster-1.3.11-win64.zip`, 184 MB con la SoundFont (59 MB sin ella); 273 ficheros, 18 carpetas de
  idioma.
- Probado: el zip descomprimido en el scratchpad, arrancado con `--settings=` en el scratchpad (`Midi/Output` vacío y
  `LastSoundFontDir` puesto, para que `SetupDefaultSoundFont` no elija FluidSynth: no sonó nada). La lista de módulos del
  proceso: runtime, Skia, SQLite, `libfluidsynth-3.dll`, `SDL3.dll`, `libsndfile-1.dll`, todos de la carpeta
  descomprimida (nada de `C:\msys64`). Partitura, español (satélites) y diálogo MIDI con la pestaña FluidSynth.
  `-NoSoundFont` en otra carpeta. 0 errores; 136 pruebas en verde.

**La SoundFont de este equipo:** su bloque INFO dice `ICOP: Frank Wen 2000-2002` e `ICMT: DO NOT REDISTRIBUTE ANY OF
THESE SAMPLES.` (hay versiones posteriores de FluidR3_GM con licencia MIT, p. ej. la de Debian). Antes de repartir el zip
hay que comprobar que se puede, o usar otra (`-SoundFont`) o ninguna (`-NoSoundFont`). El script lo avisa.

**Como en el C++:** en Windows nadie descomprime `BoosterMusicBooks.zip` (el C++ dice que lo hace el instalador, pero el
zip portátil no tiene instalador): el curso de música no aparece si no se ha instalado antes.

**Aviso de compilación que ya estaba:** `CS0414` en `Conductor._lastSound` (Core, sin cambios desde la fase 2) sale cada
vez que Core se recompila (también en `dotnet publish`). No es nuevo de la fase 6.

**Sin probar:** elegir FluidSynth y la SoundFont incluida con sonido (no debía sonar nada), un Windows sin MSYS2 ni .NET
instalados (este equipo tiene los dos; los módulos sí salen de la carpeta del zip), Windows en ARM y macOS/Linux.

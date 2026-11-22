# RefDump: referencias del C++ original

RefDump compila los `.cpp` del núcleo original de PianoBooster (sin interfaz) y escribe en un
fichero de texto lo que hace con un MIDI. Las pruebas C# (`tests/PianoBooster.Core.Tests`)
ejecutan el mismo recorrido con el port (`RefDriver.cs`) y exigen **exactamente las mismas líneas**.

- `RefDump.cpp` y `RefDriver.cs` son gemelos: si se cambia uno, hay que cambiar el otro igual
  y regenerar las referencias.
- Los ficheros originales se **copian** a `build/pbsrc` (un `#include "..."` busca primero en la
  carpeta del fichero) y se sustituyen `Settings.h` y `MidiDevice.h` por los de `stubs/`.
- Parche al copiar: `Draw.cpp` no compila sin FTGL (dos literales `2.07925ff` y `4.433665ff`);
  ese código solo dibuja nombres de notas y RefDump no lo usa.
- Sin contexto OpenGL, `glGenLists()` devuelve 0 y no se dibuja nada; el modelo de la partitura
  (colas de `CScroll`) no cambia por ello.

## Compilar y regenerar

Desde una consola **MSYS2 UCRT64** (las mismas herramientas que `PianoBooster/BUILD.md`):

```sh
sh tools/RefDump/build.sh
```

Desde PowerShell:

```powershell
.\tools\RefDump\generate-references.ps1
```

Lee `tests/PianoBooster.Core.Tests/TestData/scenarios.txt` y escribe `TestData/reference/<nombre>.txt`.
Solo hace falta regenerar si cambia el C++ original, RefDump o los escenarios.

No usar ficheros MIDI corruptos en los escenarios: el `CMidiFile` original abre un `QMessageBox`
y RefDump se queda esperando.

## Uso

```
refdump <modo> <fichero midi> <fichero de salida> [clave=valor ...]
```

Las rutas de los MIDI se pasan con `/`, porque el título de una canción sin nombre de pista es
el nombre del fichero tras la última `/`.

## Formato

Números enteros en decimal; `float` y `double` como sus bits en hexadecimal (`%08x`, `%016llx`)
para comparar exacto. Un registro por línea; la primera palabra dice qué es.

### `events`: eventos de `CMidiFile::readMidiEvent()`

| Línea | Campos |
|---|---|
| `H` | pistas, ppqn, error (`midiErrors_t`), título |
| `E` | tipo, delta, canal, nota, velocidad, duración, pista (-1 en el EOF) |

### `songmap`: `CSongMap` (editor de secciones)

| Línea | Campos |
|---|---|
| `M` | válido, ppqn, lastTick, maxNoteLength, nota más baja, más alta, compases |
| `N` | nota: tick, duración, nota, canal, pista, velocidad |
| `V` | evento: tick, status, data1, data2 |
| `B` | compás, inicio, pulsos, denominador, ticks por pulso, ms del inicio, `barOfTick(inicio+1)`, `tickOfMs(ms+0.5)` |
| `P` | `positionOfTick(inicio + pulso + 3)`: bar, beat, ticks; `tickOfPosition`, `tickOfBarBeat(bar, 1.5)`, `beatOfTick(inicio+5)` |

### `info`: lo que se averigua al cargar

`T` título, `K` armadura, `L` canales izquierda/derecha, `A` canal activo, `C` canales del
pianista, `RT` pista de la mano derecha por canal, `PN` nombres de las partes, `CH` sus canales,
`TS` compás actual, `BB` pulsos de los compases 0-39, `Q` longitud de las colas de acordes y de
eventos, `WQ` cada acorde pendiente (delta, notas `tono:mano`).

### `notation`: ranuras de `CNotation::nextSlot()`

| Línea | Campos |
|---|---|
| `S` | delta, delta del borde izquierdo, av8Left, símbolos |
| `Y` | tipo, mano, nota, índice en el pentagrama, alteración, color (3 floats), duración MIDI, modificador de alteración, compás, índice, total, pianistTiming, posY |

### `play`: la canción tocada por un pianista simulado

Preparación como `QtWindow`/`CGLView`/`CSettings` (ventana de 1024 × 700), carga de la canción
y luego `playFromStartBar()`. Cada paso: `task(tick)` (4 ms), cada 4 pasos
`drawScrollingSymbols(true)`, y el pianista:

- Cuando cambia el acorde esperado, empieza un intento.
- Toca el acorde cuando `m_chordDeltaTime >= offset × ppqn / 96 × 1000`, con los offsets
  `-10, 0, 5, -20, 2, -3, 8` por turnos, o tras 400 pasos de espera.
- Antes del acorde nº N (`N % wrongEvery == 3`), una nota errónea (la primera + 1).
- Suelta las notas tras `hold` pasos y puede reintentar 20 pasos después.

| Línea | Campos |
|---|---|
| `O` | paso, tipo, canal, nota, velocidad: cada evento enviado al dispositivo MIDI |
| `B` | paso, bits de evento devueltos por `task()` |
| `N` | paso, compás, posición en compases (double) |
| `R` | paso, notas, erróneas, tardías, precisión (float), precisión buena |
| `PI` | paso, notas pulsadas, erróneas pulsadas, nombres de nota en lista |
| `W` | paso, acorde esperado (`n tono:mano ...`) |
| `Z` | cada 50 pasos: estado de seguimiento, chordDeltaTime, playingDeltaTime, colas, pianistTiming, stopPoint, y de la partitura activa: ranuras, deltaHead, deltaTail, wantedIndex, wantedDelta, trailOnlySlots; velocidad |
| `S`/`Y` | cada 500 pasos: las ranuras de la partitura activa |
| `STOP`, `END` | fin de la canción; notas, erróneas, tardías, porcentaje |

Parámetros: `mode` (follow, listen, along, rhythm), `hand` (both, right, left), `steps`, `tick`,
`skill`, `speed`, `transpose`, `mute`, `boost`, `piano`, `latency`, `markers`, `from`, `upto`,
`followErrors`, `trail`, `wrongEvery`, `hold`.

# Características nuevas de PianoBoosterCS

Documento de planificación de lo que se añade a PianoBoosterCS **después del port** (el port está
descrito en `PORTING.md` y está completo). Igual que `PORTING.md`, está pensado para retomar el
trabajo en una sesión nueva sin el contexto de las anteriores: cada fase dice qué hay que hacer, qué
ficheros tocar y cómo probarlo. **Al terminar cada fase, actualiza §3 ("Estado") y añade un "Lo
hecho" corto debajo de la fase.**

- Repositorio: `C:\Git\PianoBosterMigration\PianoBoosterCS` (rama principal `main`).
- El C++ (`C:\Git\PianoBosterMigration\PianoBooster`) ya solo sirve de referencia: estas
  características no existen en él.
- Licencia: GPLv3, como el resto.

---

## 1. Reglas comunes a todas las características

- **No cambiar el comportamiento del port sin querer.** Las 72 pruebas de referencia contra el C++
  (`tests/PianoBooster.Core.Tests/ReferenceTests.cs`) deben seguir idénticas. Lo nuevo se *engancha*
  al `Conductor` (eventos, observadores) sin cambiar lo que ya hace: qué acorde espera, cuándo avanza,
  qué suena y lo que cuenta `Rating` (la barra de precisión del C++ se queda como está).
- Código nuevo en inglés (nombres, comentarios), como el port. Textos de la interfaz con
  `Strings.Tr(...)` y su traducción al español en `translations/Strings.es.resx` (el resto de idiomas
  caen al inglés).
- Una rama por fase: `git switch -c feature-<n>.<m>-<nombre>` desde `main` con lo de la fase anterior.
- Probar siempre: `dotnet build PianoBooster.sln` (0 avisos nuevos; el `CS0414` de
  `Conductor._lastSound` ya estaba), `dotnet test PianoBooster.sln`, y la app con
  `--settings=<scratchpad>\s.json --Xquit-after=<s>` (nunca los ajustes ni la base de datos reales del
  usuario), sin sonido (`Midi/Output` vacío). Ratón y teclado como en `PORTING.md` §10 "Probado":
  movimientos absolutos de ratón, teclas solo con la app delante.

## 2. Características

| # | Característica | Fases |
|---|---|---|
| 1 | Perfiles de usuario y puntuación de las prácticas (§4) | 1.1 – 1.7 |

## 3. Estado

| Fase | Estado | Notas |
|---|---|---|
| 1.0 | **hecha** (27 nov 2026) | Decisiones D1–D13 y D2b aceptadas. Ver §4.3 |
| 1.1 | **hecha** (27 nov 2026) | Eventos de interpretación en el `Conductor`. Rama `feature-1.1-performance-events` |
| 1.2 | **hecha** (27 nov 2026) | `PracticeScorer`, `SongMaxScore`, `ScoringRules`. Rama `feature-1.2-scorer` |
| 1.3 | pendiente | Base de datos de progreso |
| 1.4 | pendiente | Sesión de práctica en la app (canción entera → registro) |
| 1.5 | pendiente | Interfaz de perfiles |
| 1.6 | pendiente | Resultado al terminar y historial |
| 1.7 | pendiente | Cierre: traducciones, README, paquete |

---

## 4. Característica 1: perfiles de usuario y puntuación

### 4.1 Lo que pide el usuario (resumen)

- **Perfiles de usuario.** Cada perfil guarda en base de datos su progreso con las canciones.
- **Solo cuentan las canciones enteras**, no los fragmentos (secciones, bucles, "tocar desde el
  compás N").
- **Puntuación objetiva**, que permita saber cuántas veces se ha fallado. **Distinta por modo**:
  Seguir (`PlayMode.FollowYou`) y Acompañar (`PlayMode.PlayAlong`) tienen cada uno su registro.
- **Puntuación máxima por canción y mano**, según el número de notas:
  - mano derecha: según las notas de la mano derecha;
  - mano izquierda: según las notas de la mano izquierda;
  - ambas manos: máxima derecha + máxima izquierda.
  - Se guarda como información de la canción en la base de datos.
- **Errores**, cada uno descuenta (o deja de sumar) una cantidad fija; no vale lo mismo un error de
  tiempo (adelanto/retraso) que una nota equivocada.
  - Acorde de 3 notas: 2 bien y 1 mal = 1 error; 2 mal = 2 errores; 3 mal = 3 errores. Es decir,
    **los errores se cuentan por nota, no por acorde**.
- **Modo Seguir:** se empieza con la máxima y se descuentan los errores. No se pueden saltar tiempos,
  pero sí cometer varios errores mientras no se pulse la nota correcta. **Nunca baja de 0.**
- **Modo Acompañar:** se empieza en 0 y se suma por cada nota tocada. Adelanto o retraso: **media**
  puntuación. Saltarse un tiempo = saltarse todas las notas de ese tiempo (no suman, y son errores).
- **Cada práctica genera registros:** puntuación, fecha y hora de inicio y de fin, y por cada error:
  compás y tiempo del compás, tipo (adelanto, retraso, nota equivocada; y omitida en Acompañar), la
  nota que tocaba y la nota que se ha tocado en su lugar. Puede haber varios errores en el mismo
  compás y tiempo (acordes).

### 4.2 Lo que ya hay en el código (para no tener que volver a mirarlo)

Todo en `src/PianoBooster.Core/Conductor.cs` salvo donde se indica.

- **Acorde esperado:** `_wantedChord` (`Chord`, `Core/Chord.cs`), sacado de `_wantedChordQueue` en
  `FetchNextChord()`. Solo tiene las notas de la mano activa (`Note.Part` = `WhichPart.Right/Left`) y
  `TrimOutOfRangeNotes` quita las que no caben en el teclado (rango del teclado MIDI, `Piano`) y
  tiene en cuenta el `_transpose`. **El acorde no sabe en qué compás/tiempo está**: hay que añadirlo
  (fase 1.1), p. ej. con la posición absoluta en ticks al meterlo en la cola y `Bar`/`SongMap` para
  pasarla a compás y tiempo.
- **Tiempo del pianista:** `_chordDeltaTime` (<0 = antes de la nota, >0 = después) y
  `_pianistTiming`. Zona de juego: `_cfgPlayZoneEarly` / `_cfgPlayZoneLate`
  (`Cfg.PlayZoneEarly/Late` ajustados por ppqn y velocidad). Una nota antes de `-_cfgPlayZoneEarly`
  no es válida (cuenta como nota equivocada).
- **Nota buena:** `PianistInput` → `ValidatePianistNote` → `PlayGoodNote`. El acorde se da por bueno
  en `ValidatePianistChord`: con `_skill >= 3` hacen falta **todas** las notas; con `_skill < 3`
  **basta una** (las demás no se cuentan hoy como error: ver decisión D4). También falla si hay 2 o más
  notas malas pulsadas (`PianistBadNotesDown`).
- **Nota equivocada:** la rama `else` de `PianistInput` con `_playing`: `_rating.WrongNotes(1)`. Se
  sabe la nota tocada (`inputNote.Note`) y el acorde esperado, pero no "qué nota tocaba en su lugar"
  cuando el acorde tiene varias (ver D6). Con `Settings.FollowThroughErrors` en Seguir, una nota
  equivocada dentro de la zona **salta el acorde** (`FetchNextChord`) y cuenta las que faltaban como
  tarde.
- **Retraso:**
  - Seguir: en `RealTimeEngine`, estado `Waiting` y `_pianistTiming > _cfgPlayZoneLate` → una vez por
    acorde `_rating.LateNotes(faltan)`, `_followPlayingTimeOut = true` (las notas que se toquen después
    salen en el color "malo").
  - Acompañar: en `FollowPlaying`, `_chordDeltaTime > _cfgPlayZoneLate` →
    `_rating.LateNotes(faltan)` y `FetchNextChord()`: **es el "saltarse un tiempo"** (notas omitidas).
- **Adelanto:** hoy no se distingue. Una nota dentro de la zona pero antes de su momento
  (`_chordDeltaTime < 0`) es buena sin más. Hay que definir una ventana "a tiempo" (D2).
- **`Rating`** (`Core/Rating.cs`): solo contadores (total, equivocadas, tarde) y la precisión animada
  de la barra. No se toca; la puntuación nueva es una clase aparte.
- **Fin de la canción:** `EventBits.PlayingStopped` cuando se llega a `PbEof` (en `RealTimeEngine`);
  la app lo recibe en `MainWindow.axaml.cs` (`eventBits & EventBits.PlayingStopped`). Parar a mano
  también acaba ahí: hay que distinguirlo.
- **Fragmentos:** `Bar.SetPlayFromBar`, `Bar.SetLoopingBars`, `IsLoopEnabled`; las secciones (tabla
  `parts` de `library.db`, panel lateral, "Whole song" en `SidePanel.axaml.cs`). Una práctica es
  "entera" si empieza en el inicio de la canción, no hay bucle ni sección y llega a `PbEof`.
- **Hilos:** el `Song` solo con su lock (`ConductorThread.SyncRoot`); el `Conductor` corre en el hilo
  del `ConductorThread`. Lo que se registre durante la práctica se acumula en memoria en ese hilo y
  se guarda en la base de datos al terminar, desde el hilo de la interfaz.
- **Base de datos:** `src/PianoBooster.Data/Library.cs` (`library.db` en `SettingsStore.Folder`,
  mismo esquema que el C++, `user_version = 3`). Una canción se identifica por `songs.id`, con `path`
  y `hash` (SHA-1 del fichero; la canción movida se reconoce por el hash).

### 4.3 Decisiones (fase 1.0)

Aceptadas por el usuario el 27 nov 2026 tal como se propusieron, salvo **D2** (la cambió el usuario)
y **D7** (cambia por D2). **D2b** se añadió por D2 y también está aceptada.

- **D1. Valores.** Cada nota vale **10** puntos. Seguir: nota equivocada −10, adelanto/retraso −5.
  Acompañar: nota a tiempo +10, adelanto/retraso +5, omitida +0, nota equivocada +0 (se registra pero
  no resta). Constantes en una sola clase para poder cambiarlas.
- **D2. A tiempo, adelanto y retraso (decisión del usuario).** Se usa la zona de juego que ya tiene el
  `Conductor` (`Cfg.PlayZoneEarly/Late` = 25 ticks a 96 por negra, ≈ ¼ de negra a cada lado, escalado
  con la velocidad):
  - **dentro de la zona** (`-_cfgPlayZoneEarly < _chordDeltaTime ≤ _cfgPlayZoneLate`): **a tiempo**;
  - **antes de la zona**: **adelanto** (la nota es del acorde siguiente, pero el `Conductor` la rechaza);
  - **después de la zona**: **retraso** (en Seguir, lo que detecta `_followPlayingTimeOut`; en
    Acompañar, el acorde ya se ha saltado).
- **D2b. Media puntuación en Acompañar: ventana de tolerancia.** Con D2 el `Conductor` nunca da por
  buena una nota fuera de la zona: antes la rechaza y después salta el acorde. Así, en Acompañar nunca
  se daría la media puntuación. Por eso hay una **ventana de tolerancia** del mismo ancho que la zona a
  cada lado, que solo usa el `PracticeScorer` (el `Conductor` no cambia, y la partitura sigue pintando
  esas notas como ahora):
  - nota correcta en la ventana anterior → adelanto, +5; si luego se vuelve a tocar dentro de la zona,
    se queda la mejor (+10);
  - nota de un acorde ya saltado, en la ventana posterior → retraso, +5 (deja de ser omitida);
  - fuera de las ventanas → nota equivocada (y la omitida sigue omitida).
  En Seguir, la misma ventana anterior separa adelanto (−5) de nota equivocada (−10).
- **D3. Retraso en Seguir.** Un error de retraso por cada nota del acorde que aún no se había tocado
  al pasar la zona (lo mismo que hoy suma `LateNotes`), una sola vez por acorde.
- **D4. Seguir con `_skill < 3`** (basta una nota del acorde). Cada nota del acorde no tocada cuenta
  como error "omitida" con el mismo valor que nota equivocada.
- **D5. `FollowThroughErrors`.** Con ese ajuste, Seguir sí salta acordes. Las notas saltadas cuentan
  como "omitida"; la nota equivocada, como equivocada.
- **D6. "La nota que tocaba".** En un acorde no hay una correspondencia directa entre la nota
  equivocada y la que faltaba. Se guarda la nota esperada más cercana en altura que aún no se haya
  tocado; si no hay acorde esperado (nota entre dos acordes), la del siguiente acorde.
- **D7. Notas antes de la zona.** Hoy el `Conductor` las cuenta como equivocadas. Con D2: si la nota
  es del acorde esperado y está en la ventana de D2b, **adelanto**, en el compás/tiempo de ese acorde;
  si no, nota equivocada.
- **D8. Práctica no terminada** (parar a mano, cambiar de canción, cerrar): no se guarda nada.
- **D9. Velocidad.** Se puede practicar a cualquier velocidad; se guarda la velocidad en el registro.
  Sin multiplicador de puntuación.
- **D10. Otros modos.** Escuchar y Ritmo (`RhythmTapping`) no generan registros.
- **D11. Máxima con teclado reducido o transporte.** `TrimOutOfRangeNotes` puede quitar notas.
  En la canción se guarda la máxima **nominal** (todas las notas de cada mano). En cada práctica se
  guarda también la máxima **real** de esa práctica (las notas que de verdad se pedían). El porcentaje
  se calcula sobre la real.
- **D12. Dónde se guarda.** `library.db` lo comparte el C++. Base aparte, `progress.db`, junto a
  `library.db` en `SettingsStore.Folder`; la canción se enlaza por `hash` (y `path` para mostrarla),
  así `library.db` no cambia de versión.
- **D13. Perfil por defecto.** Al arrancar sin perfiles se crea uno ("Usuario"/nombre de la cuenta
  de Windows) y queda activo; el activo se guarda en los ajustes (`Profile/active`).

### 4.4 Diseño

```
Conductor ──(PerformanceEvent)──► IPerformanceObserver
                                     └─ PracticeScorer (Core)    reglas de §4.1 + D1–D7
                                            │  PracticeResult (en memoria)
App: PracticeSession ◄────────────────────────┘
   (sabe si la práctica es entera, perfil, modo, mano, velocidad, inicio/fin)
         └─► ProgressStore (Data, progress.db)
```

Tipos previstos (nombres orientativos):

- `Core/PerformanceEvents.cs`: `ChordExpected(position, notes, hand)`, `NotePlayed(pitch, expected,
  timing)`, `WrongNote(pitch, nearestExpected)`, `NotesMissed(notes, reason: Late|Skipped)`,
  `SongEnded(reachedEnd)`. `position` = `(bar, beat)` (+ ticks) del acorde esperado.
- `Core/PracticeScorer.cs`: recibe los eventos y lleva la puntuación según el modo. Expone
  `Score`, `MaxScore`, `Errors` (`PracticeError(bar, beat, kind, expectedNote, playedNote, hand)`).
- `Core/SongMaxScore.cs`: recorre las notas de la canción (los mismos acordes que alimentan
  `_wantedChordQueue`, sin tocar el `Conductor`) y da la máxima por mano.
- `Data/ProgressStore.cs` (`progress.db`):

  | Tabla | Columnas |
  |---|---|
  | `profiles` | `id`, `name` UNIQUE, `created` |
  | `songs` | `id`, `hash` UNIQUE, `path`, `title`, `notes_right`, `notes_left`, `max_right`, `max_left` (máxima ambas = suma) |
  | `practices` | `id`, `profile_id`, `song_id`, `mode` (`follow`/`playalong`), `hand` (`right`/`left`/`both`), `speed`, `started`, `ended`, `score`, `max_score` (real, D11), `notes_expected`, `error_count` |
  | `practice_errors` | `id`, `practice_id`, `bar`, `beat`, `ticks`, `kind` (`early`/`late`/`wrong`/`missed`), `expected_note`, `played_note` (NULL si omitida), `hand` |

  Fechas como `library.db` (`yyyy-MM-ddTHH:mm:ss`). Índices por `(profile_id, song_id, mode, hand)`.

### 4.5 Fases

#### 1.0 Decisiones

Repasar §4.3 con el usuario y escribir la decisión final de cada punto. Sin código.
**Hecha (27 nov 2026):** todas aceptadas; D2 la definió el usuario y D2b/D7 salen de ella.

#### 1.1 Eventos de interpretación en el `Conductor` (rama `feature-1.1-performance-events`)

- **C#:** `Core/Conductor.cs`, `Core/Chord.cs` (posición del acorde), `Core/Bar.cs`/`SongMap.cs` (ticks
  → compás y tiempo), nuevo `Core/PerformanceEvents.cs`.
- El `Conductor` emite los eventos de §4.4 en los puntos de §4.2 (nota buena con su `_chordDeltaTime`,
  equivocada, tarde en Seguir, saltada en Acompañar, `FollowThroughErrors`, notas no tocadas con
  `_skill < 3`, fin de canción). Solo emite: no cambia nada de lo que hace.
- Para D2b/D7 los eventos de nota llevan siempre la nota tocada y `_chordDeltaTime` **también cuando el
  `Conductor` la rechaza** (antes de la zona), y el evento de acorde saltado lleva sus notas, para que el
  `PracticeScorer` aplique la ventana de tolerancia sin que el `Conductor` cambie.
- El observador es opcional (`null` = como ahora) y se llama en el hilo del `Conductor`, sin bloquear.
- **Pruebas:** las 72 de referencia sin cambios. Nuevas en `PianoBooster.Core.Tests` con `RefDriver`
  (secuencias de notas simuladas): en un MIDI de prueba conocido, los eventos salen con el compás y
  tiempo correctos; un acorde con 1/2/3 notas mal da 1/2/3 eventos; en Acompañar, no tocar un tiempo
  da las notas de ese tiempo como saltadas.

**Lo hecho (27 nov 2026):**

- `Core/PerformanceEvents.cs`: `IPerformanceObserver` (un método, `OnPerformanceEvent`), `ExpectedNote`
  (tecla ya transportada + mano), `ExpectedChord` (`Id` desde 1, `Tick` del fichero, `Position` =
  `BarPosition`, `Notes` solo las pedidas: mano activa y dentro del teclado, D11), `PlayZone`
  (`Early`/`Late`, `IsBefore`/`IsAfter`/`Contains` con la misma regla que el `Conductor`) y los
  eventos: `PlayingStarted(From, LoopActive)`, `PlayingStopped(ReachedEnd)`, `ChordExpected`,
  `NotePlayed` (nota aceptada: `DeltaTime`, `PianistTiming`, `Zone`, `AfterTimeOut`), `WrongNote`
  (nota no aceptada tocando: `BeforeZone`, `InChord`, `NearestExpected`/`NearestChord` de D6,
  `PreviousChord`/`PreviousDeltaTime` para la ventana posterior de D2b), `NotesMissed(Chord, Notes,
  Reason)` con `MissReason.Late` (Seguir, una vez por acorde; luego las notas llegan con
  `AfterTimeOut`), `Skipped` (Acompañar), `SkippedByWrongNote` (`FollowThroughErrors`) y `NotNeeded`
  (`_skill < 3`, D4), y `ChordAccepted`.
- `Chord.Tick` (inicio de la primera nota, en ticks del fichero): lo pone `FindChord`, que ahora
  lleva la cuenta (`SongTick`); `Song.RegenerateChordQueue` la arranca en el tick del siguiente
  evento (`Conductor.NextMidiEventTick`). La posición se saca con `Bar.OffsetPosition` y el mapa de
  compases (no hizo falta tocar `Bar`/`SongMap`).
- `Conductor.PerformanceObserver` (null = como antes). Los eventos se crean solo si hay observador;
  si el observador lanza una excepción se registra con `Log.Warn` y el `Conductor` sigue. Para las
  notas no tocadas el `Conductor` lleva su propio `_chordPlayedNotes` (el `_goodPlayedNotes` del C++
  quita las notas al soltarlas y no se vacía al saltar un acorde en Acompañar).
- Ojo para 1.2: en Seguir la música espera, así que `DeltaTime` deja de crecer en el punto de parada;
  el retraso se mide con `PianistTiming` (como hace el `Conductor`). Los tiempos van en ticks ×
  `Speed.AdjustFactor` a la velocidad de la canción, igual que `Zone`.
- Pruebas: `PerformanceEventsTests` (posición de cada acorde contra `SongMap` en sy01 —4/4, 12/8, 3/4—
  y sy02 —ppqn 480—, posiciones a mano, 1/2/3 notas mal = 1/2/3 `WrongNote`, adelanto, retraso en
  Seguir, tiempo saltado y nota tardía de un acorde saltado en Acompañar, `_skill < 3`,
  `FollowThroughErrors`, parar a mano, tocar desde un compás) y `ReferenceTests.SameWithAPerformanceObserver`
  (los 16 escenarios `play` dan exactamente lo mismo con observador). Las 72 de referencia, sin cambios.

#### 1.2 Reglas de puntuación y máxima (rama `feature-1.2-scorer`, necesita 1.1)

- **C#:** nuevos `Core/PracticeScorer.cs`, `Core/SongMaxScore.cs`, `Core/ScoringRules.cs` (constantes D1).
- `PracticeScorer`: Seguir empieza en la máxima y resta (mínimo 0); Acompañar empieza en 0 y suma
  (media en adelanto/retraso). Zona = a tiempo; ventana de tolerancia de D2b a cada lado = adelanto o
  retraso; fuera = nota equivocada u omitida. Lista de errores con compás, tiempo, tipo, nota esperada y tocada.
- `SongMaxScore`: notas por mano → máxima derecha, izquierda y ambas (suma).
- **Pruebas** (unitarias, sin MIDI): cada regla de §4.1 con su caso (acordes parciales, mínimo 0,
  media puntuación, tiempo saltado, máxima de ambas = suma) y una de extremo a extremo con `RefDriver`.

**Lo hecho (27 nov 2026):**

- `Core/ScoringRules.cs`: las constantes de D1 (`NoteValue` 10; Seguir: equivocada −10, adelanto/retraso
  −5, omitida −10; Acompañar: a tiempo +10, adelanto/retraso +5, omitida y equivocada +0) y
  `ToleranceWindowZones` = 1 (ventanas de D2b del mismo ancho que la zona).
- `Core/PracticeScorer.cs`: `PracticeScorer : IPerformanceObserver` (solo `FollowYou`/`PlayAlong`, D10;
  otro modo lanza `ArgumentException`). Lleva el estado de cada nota de cada acorde (pendiente, a tiempo,
  adelanto, retraso, omitida), así los errores se cuentan por nota. `PlayingStarted` empieza de cero;
  `PlayingStopped` deja `ReachedEnd`. `Result` (desde cualquier hilo, con lock) da un `PracticeResult`:
  `Score`, `MaxScore`, `NotesExpected`, `Errors` (`PracticeError(Bar, Beat, Tick, Kind, ExpectedNote,
  PlayedNote, Hand)`, `Kind` = `Early`/`Late`/`Wrong`/`Missed`), `StartedFrom`, `LoopActive`,
  `ReachedEnd`, `CountOf(kind)`, `Percent`. Reglas:
  - `MaxScore` = 10 × notas de los `ChordExpected` recibidos: al llegar al final es la máxima **real**
    (D11). Seguir: `Score = max(0, MaxScore − descuentos)` (lo mismo que empezar en la máxima y restar).
    Acompañar: suma de puntos.
  - `WrongNote` con `BeforeZone && InChord` en la ventana anterior → adelanto. En Seguir cada pulsación
    temprana es un error (−5); en Acompañar cuenta una vez (+5) y si luego llega su `NotePlayed` sube a
    +10 y el error de adelanto **se quita** ("se queda la mejor").
  - Acompañar: `WrongNote` de una nota omitida de `PreviousChord` con `PreviousDeltaTime` en la ventana
    posterior → el error de omitida pasa a retraso con la nota tocada (+5). Solo el acorde anterior.
  - Resto de `WrongNote` → equivocada, en el compás/tiempo de `NearestChord` (o `Chord`, o
    `PreviousChord`; si no hay ninguno, 0/0), con `NearestExpected` como nota esperada (D6).
  - `NotesMissed(Late)` (Seguir) → un retraso por nota sin `PlayedNote`; cuando luego llega su
    `NotePlayed` con `AfterTimeOut` se rellena `PlayedNote`. `Skipped`, `SkippedByWrongNote` y
    `NotNeeded` → omitidas (D4, D5). En Acompañar una nota ya dada por adelanto no pasa a omitida.
  - En Seguir una nota que se tocó antes de tiempo y luego no se toca puede tener dos errores
    (adelanto y luego retraso u omitida): "varios errores mientras no se pulse la nota correcta".
- `Core/SongMaxScore.cs`: `SongMaxScore(NotesRight, NotesLeft)` con `MaxRight`, `MaxLeft`, `MaxBoth`
  (suma), `MaxFor(hand)`/`NotesFor(hand)`. `FromChords(chords)` cuenta por `Note.Part`;
  `FromMidiFile(path, activeChannel)` lee el fichero con un `MidiFile` y un `FindChord` nuevos (los
  mismos acordes que la cola del `Conductor`, máxima **nominal**, sin recortar por teclado ni mano) y
  devuelve null si no se puede leer. Usa las manos de los estáticos de `Note` de la canción cargada:
  **en 1.4, llamarlo después de `LoadSong` y con el lock de la canción** (`MidiFile` cambia un instante
  el ppqn estático; se restaura con el nuevo `MidiFile.RestorePulsesPerQuarterNote`, interno).
- Pruebas: `PracticeScorerTests` (eventos hechos a mano: máxima a tiempo en los dos modos, 1/2/3 notas
  mal en un acorde, mínimo 0, adelanto dentro/fuera de la ventana, retraso en Seguir con la nota tocada
  después, `_skill < 3`, `FollowThroughErrors`, media puntuación y "la mejor" en Acompañar, tiempo
  saltado y acorde parcial, retraso de un acorde saltado dentro/fuera de la ventana, equivocadas que no
  restan, nota entre acordes, varios errores en el mismo compás/tiempo, reinicio, máxima de ambas =
  suma, fichero que no existe) y `PracticeScorerSongTests` con el `PerformanceDriver` de 1.1 (que ahora
  acepta un segundo observador, `alsoTell`): canción entera a tiempo = máxima de `SongMaxScore` para
  ambas manos, derecha e izquierda, 2 notas mal, tiempo saltado, acorde tarde, y que leer la máxima no
  cambia el ppqn. Las 72 de referencia, sin cambios.

#### 1.3 Base de datos de progreso (rama `feature-1.3-progress-db`, independiente de 1.1/1.2 salvo los tipos)

- **C#:** nuevo `Data/ProgressStore.cs` (Microsoft.Data.Sqlite, como `Library.cs`: una conexión,
  `user_version`, `IsOpen`, lotes con transacción). Interfaz `IProgressStore` en Core
  (`Core/ProgressTypes.cs`) para que la app no dependa de SQLite.
- Operaciones: perfiles (crear, renombrar, borrar con sus prácticas, listar); canción (obtener o crear
  por hash, guardar máximas); práctica (guardar con sus errores en una transacción); consultas
  (prácticas de un perfil y canción por modo y mano, mejor puntuación, última).
- **Pruebas** en `PianoBooster.Data.Tests` con base temporal: esquema, guardar y leer una práctica con
  varios errores en el mismo compás y tiempo, borrar perfil borra sus prácticas, canción movida (mismo
  hash), reabrir.

#### 1.4 Sesión de práctica en la app (rama `feature-1.4-practice-session`, necesita 1.2 y 1.3)

- **C#:** nuevo `App/PracticeSession.cs`; `App/MainWindow.axaml.cs` (abrir `ProgressStore` junto a la
  biblioteca, empezar/terminar sesión al pulsar Play y al recibir `PlayingStopped`).
- Una sesión empieza al arrancar desde el inicio en Seguir o Acompañar sin bucle, sección ni "desde el
  compás" (§4.2 "Fragmentos"); si alguna de esas cosas cambia durante la práctica, se descarta. Solo se
  guarda si se llega a `PbEof` (D8). Guarda perfil, modo, mano, velocidad, inicio, fin, puntuación,
  máximas y errores.
- La máxima nominal de la canción se calcula al cargarla si `progress.db` no la tiene (o el hash cambió).
- **Probar** en la app con un MIDI corto de `tests/.../TestData/midi` y notas simuladas (teclado del
  ordenador como entrada, ver `KeyboardSetupDialog`): la práctica entera queda en `progress.db` del
  scratchpad; parar a mitad o tocar una sección no deja nada.

#### 1.5 Interfaz de perfiles (rama `feature-1.5-profiles-ui`, necesita 1.3)

- **C#:** nuevo `App/ProfilesDialog.axaml(.cs)` (crear, renombrar, borrar con confirmación, elegir);
  `MainWindow.axaml` (menú o selector en la `TopBar` con el perfil activo); `Settings` (`Profile/active`).
- Perfil por defecto (D13). Sin perfil activo no se guarda nada (no debería pasar).
- **Probar** en la app: crear dos perfiles, cambiar, reiniciar y que siga el activo.

#### 1.6 Resultado al terminar e historial (rama `feature-1.6-results-ui`, necesita 1.4 y 1.5)

- **C#:** nuevo `App/PracticeResultDialog.axaml(.cs)`: al terminar una canción entera, puntuación /
  máxima, porcentaje, nº de errores por tipo, lista de errores (compás, tiempo, tipo, nota esperada y
  tocada, con nombres de nota como en la partitura) y comparación con la mejor anterior.
  Nuevo `App/ProgressDialog.axaml(.cs)` (menú): prácticas del perfil activo por canción, modo y mano.
- Más adelante (fuera de esta fase, anotar si se quiere): marcar en la partitura los compases con más
  errores; gráfica de evolución.

#### 1.7 Cierre (rama `feature-1.7-close`)

- Textos nuevos en `Strings.es.resx` (y en `TranslationTests` si hace falta), `README.md` (qué es un
  perfil, qué cuenta como práctica entera), `CHANGELOG.md` y versión en `Directory.Build.props`;
  comprobar que `tools/package-windows.ps1` sigue funcionando (no hay ficheros nuevos que copiar:
  `progress.db` se crea en la carpeta de ajustes).

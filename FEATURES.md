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
| 1.3 | **hecha** (27 nov 2026) | `ProgressStore` (`progress.db`), `IProgressStore`. Rama `feature-1.3-progress-db` |
| 1.4 | **hecha** (27 nov 2026) | `PracticeSession`, `ActiveProfile` (perfil por defecto de D13 adelantado, sin interfaz). Rama `feature-1.4-practice-session` |
| 1.5 | **hecha** (27 nov 2026) | `ProfilesDialog`, botón del perfil en la `TopBar` y `Configuración → Perfiles...`. Rama `feature-1.5-profiles-ui` |
| 1.6 | **hecha** (27 nov 2026) | `PracticeResultDialog`, `ProgressDialog` (`Canción → Historial de prácticas...`), `SaveFinished` con la mejor anterior. Rama `feature-1.6-results-ui` |
| 1.7 | **hecha** (27 nov 2026) | Cierre: prueba de textos sin traducir, README, CHANGELOG, versión **1.5.0**, paquete probado. Rama `feature-1.7-close`. **Característica 1 terminada** |

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
  con otro modo **no lanza** —corregido en la fase 1.3: se crea en el hilo de la interfaz, sin
  manejador global, y lo cerraría—: registra un `Log.Error`, `IsScored` = false, ignora los eventos y
  su `Result` se queda vacío, sin `Started` ni `ReachedEnd`, así que nunca se guarda). Lleva el estado de cada nota de cada acorde (pendiente, a tiempo,
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

**Lo hecho (27 nov 2026):**

- `Core/ProgressTypes.cs`: `ProgressProfile(Id, Name, Created)`, `ProgressSong(Id, Hash, Path, Title,
  MaxScore)` (`SongMaxScore?`, null hasta que se calcule), `PracticeRecord` (propiedades `init`: perfil,
  canción, `Mode`, `Hand` Right/Left/Both, `Speed` en %, `Started`/`Ended`, `Score`, `MaxScore` real,
  `NotesExpected`, `ErrorCount`, `Errors`, `Percent`; `FromResult(PracticeResult, ...)` para 1.4) e
  `IProgressStore`.
- `Data/ProgressStore.cs`: el esquema de §4.4 tal cual (`user_version = 1`, `SchemaVersion`),
  `foreign_keys = ON` y `ON DELETE CASCADE` (perfil → prácticas → errores). Una conexión, sin pool
  (el fichero se cierra con `Dispose`); errores con `Log.Error` y la operación no hace nada; cerrada
  (`IsOpen` false) todo devuelve vacío/null/-1. Textos: modo `follow`/`playalong`, mano
  `right`/`left`/`both` (en errores, NULL si `None`), tipo `early`/`late`/`wrong`/`missed`, notas NULL
  si no hay; fechas `yyyy-MM-ddTHH:mm:ss` locales (se pierden los milisegundos).
  - Perfiles: `CreateProfile` (recorta espacios; -1 si vacío o repetido), `RenameProfile`,
    `DeleteProfile` (con sus prácticas), `Profiles()` (por nombre sin mayúsculas), `Profile(id)`.
    El perfil por defecto (D13) queda para 1.5.
  - Canciones: `Song(hash)`, `GetOrCreateSong(hash, path, title)` (si ya está con otra ruta o título
    los actualiza; un título vacío no borra el que había; hash vacío → null), `SetSongMaxScore`.
  - Prácticas: `SavePractice` (práctica y errores en una transacción; `error_count` = `Errors.Count`;
    perfil o canción que no existen → -1 y nada guardado; Escuchar/Ritmo o mano `None` → -1 con
    `Log.Error`, **sin excepción**: la app no tiene manejador global y una excepción en el hilo de la
    interfaz la cerraría), `Practice(id)` con errores, `Practices(perfil, canción, modo, mano)` (la más
    nueva primero, **sin** errores: usar `ErrorCount`), `BestPractice` (mayor porcentaje sobre la
    máxima real, luego mayor puntuación, luego la más antigua) y `LastPractice`, con errores. Las
    consultas con Escuchar/Ritmo o mano `None` dan vacío/null. El `ProgressStore` no lanza nunca.
- Pruebas: `ProgressStoreTests` (11, base en una carpeta temporal): esquema y columnas, perfiles
  (repetidos, vacíos, renombrar), práctica con 5 errores (2 en el mismo compás y tiempo, notas NULL)
  leída igual, práctica de perfil/canción inexistente no se guarda, modos sin prácticas (-1, nada
  guardado, consultas vacías y la base sigue funcionando), filtros y
  orden de `Practices`/`BestPractice`/`LastPractice`, borrar perfil borra prácticas y errores,
  canción movida (mismo hash), reabrir, `FromResult` y sin base de datos. Las 72 de referencia, sin
  cambios.
- Corregido de 1.2: `PracticeScorer` ya no lanza con Escuchar/Ritmo (ver su "Lo hecho"); la prueba
  `OnlyFollowYouAndPlayAlongAreScored` comprueba que no puntúa ni da una práctica entera.

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

**Lo hecho (27 nov 2026):**

- **Perfil (D13 adelantado, aceptado por el usuario):** `App/ActiveProfile.cs`. `Resolve(store, settings,
  defaultName)` al arrancar: el perfil de `Profile/active` (se guarda el **id**, así renombrarlo no lo
  pierde) si existe; si no, el primero de `Profiles()`; si no hay ninguno, crea uno con el nombre de
  la cuenta de Windows (`DefaultName`, y si sale vacío `Strings.Tr("User")` = «Usuario») y lo deja en
  `Profile/active`. Sin `progress.db` o si no se puede crear: -1 y no se guarda nada (`Log.Warn`).
  `Set(settings, id)` queda para el selector de 1.5. Sin interfaz.
- `App/PracticeSession.cs`: `PracticeSession : IPerformanceObserver`, el `PerformanceObserver` del
  `Song` solo si hay perfil (si no, el `Conductor` sigue sin observador, como antes). Lo que lee del
  `Conductor` va por `IPracticeState` (`SongPracticeState` en la app, uno falso en las pruebas): modo,
  mano, velocidad en %, canal activo, `LoopActive`, `PlayFrom` y `CurrentNotePosition`.
  - Empieza una práctica (con un `PracticeScorer` nuevo) en `PlayingStarted` si hay perfil y canción,
    el modo es Seguir o Acompañar (D10), `From` es el inicio, no hay bucle (`LoopActive` del evento y
    del `Conductor`) y **la canción está de verdad en el inicio** (`CurrentNotePosition`). Ojo: al
    reanudar tras una pausa (`P`, `On_playButton_clicked` sin `Rewind`) el `Conductor` vuelve a decir
    `PlayingStarted(From = inicio)` aunque siga desde la mitad; eso no cuenta como práctica entera.
  - Se descarta (D8) si en cualquier evento cambia la canción, el modo, la mano, la velocidad (D9: se
    guarda la del inicio, así que cambiarla a mitad la descarta), el instrumento, se activa un bucle o
    sección o «desde el compás», o un `ChordExpected` va hacia atrás en ticks (vuelta de un bucle).
    Parar a mano, cambiar de canción o cerrar dan `PlayingStopped(false)` y no dejan nada.
  - Con `PlayingStopped(ReachedEnd)` y `Result.ReachedEnd` (y alguna nota pedida) el registro
    (`PracticeRecord.FromResult`, fin = ahora) queda en una lista en memoria. `SaveFinished()` lo
    guarda desde el hilo de la interfaz: en `MainWindow.SongEventUpdated` con el bit `PlayingStopped`
    (antes de «Repetir canción», que vuelve a empezar) y en `Shutdown`. El lock propio de la sesión se
    toma siempre después del de la canción, nunca al revés.
  - `SongLoaded(path, title, maxScore)` desde `MainWindow.SongLoaded` (después de `LoadSong`):
    `GetOrCreateSong` por `Library.FileHash`; si la canción no tiene máxima (nueva, o el hash cambió)
    llama a `SongMaxScore.FromMidiFile(path, ActiveChannel)` con el lock de la canción y la guarda.
    Es la máxima nominal del canal activo al cargar; si luego se elige otro instrumento no se recalcula.
  - Nada lanza excepciones (`ProgressStore` y `PracticeScorer` tampoco).
- `MainWindow`: abre `ProgressStore` en `store.Folder` junto a `library.db` y lo cierra en `Shutdown`.
  Texto nuevo: «User» → «Usuario» en `Strings.es.resx`.
- Pruebas: `PracticeSessionTests` (App.Tests, 21 casos con estado falso y `progress.db` temporal):
  práctica entera guardada con sus datos y errores, Acompañar aparte, parar a mitad, desde un compás /
  sección / bucle, reanudar tras pausa, cada cambio a mitad (bucle, desde, modo, mano, velocidad,
  instrumento, canción, vuelta atrás), la siguiente práctica tras una descartada, Escuchar/Ritmo, sin
  perfil o canción, máxima calculada solo si falta (canción movida, cambiada, ilegible) y el perfil
  por defecto (creado una vez, sigue tras renombrar, el primero si se borra el activo, sin base).
  Las 72 de referencia, sin cambios (Core no se ha tocado).
- **Probado en la app** (`--settings=<scratchpad>\app\s.json`, sin salida MIDI, `bc01-StartWithMiddleC.mid`
  copiado al scratchpad, la tecla `t` que toca el acorde esperado mandada a la ventana de la app):
  - al arrancar crea el perfil con el nombre de la cuenta y `Profile/active` = 1, y la canción con su
    máxima nominal (90 + 80);
  - Seguir parando con `P` en el compás 3: nada;
  - Seguir entera: una práctica (`follow`, `both`, 100 %, 26 s, máxima real 170, 52 errores);
  - sección «Bars 1-3» (añadida al `library.db` del scratchpad) elegida en el panel y repetida: nada;
  - pausa y seguir hasta el final: nada;
  - Acompañar entera: otra práctica (`playalong`, 160/170).
  Al pulsar `t` mucho antes de tiempo (fuera de la ventana de D2b), la nota cuenta como equivocada
  aunque sea la esperada (D7). Por eso hay errores `wrong` con nota esperada = tocada.

#### 1.5 Interfaz de perfiles (rama `feature-1.5-profiles-ui`, necesita 1.3)

- **C#:** nuevo `App/ProfilesDialog.axaml(.cs)` (crear, renombrar, borrar con confirmación, elegir);
  `MainWindow.axaml` (menú o selector en la `TopBar` con el perfil activo); `Settings` (`Profile/active`).
- Perfil por defecto (D13): ya hecho en 1.4 (`ActiveProfile`); falta solo la interfaz, que use `ActiveProfile.Set` y cambie `PracticeSession.ProfileId`. Sin perfil activo no se guarda nada (no debería pasar).
- **Probar** en la app: crear dos perfiles, cambiar, reiniciar y que siga el activo.

**Lo hecho (27 nov 2026):**

- **Decisiones (aceptadas por el usuario):**
  - **Cambiar de perfil durante una práctica la descarta** (como cualquier otro cambio a mitad, D8): no se
    guarda ni para el perfil anterior ni para el nuevo. La siguiente práctica desde el inicio ya es del nuevo.
  - **Borrar el perfil activo** se puede: pasa a ser activo el primero que quede por nombre (lo mismo que
    `Resolve`). **El último perfil no se puede borrar** (botón desactivado), así siempre hay un perfil activo.
  - **Selector:** un botón «Perfil: <nombre>» a la derecha de la `TopBar` que abre el diálogo (no un
    combo, para no cambiar de perfil sin querer con el teclado mientras se toca) y `Configuración →
    Perfiles...`. Sin `progress.db`, los dos están desactivados («Sin perfil»).
- `Core/ProgressTypes.cs` + `Data/ProgressStore.cs`: `IProgressStore.PracticeCount(profileId)` (0 si no hay
  o con error, sin lanzar), para la confirmación de borrado.
- `App/ActiveProfile.cs`: `Delete(store, settings, id, activeId, out active)`: no borra el último (ni uno
  que no existe), comprueba que se ha borrado y, si era el activo, deja activo el primero que queda
  (`Profile/active`). Devuelve false si no se ha borrado.
- `App/PracticeSession.cs`: `StillWhole` también descarta la práctica si `ProfileId` ya no es el de su
  inicio. Una práctica ya terminada y aún sin guardar conserva su perfil; si ese perfil se ha borrado,
  `SavePractice` da -1 y no se guarda nada (sin excepción).
- `App/ProfilesDialog.axaml(.cs)`: lista por nombre con el activo en negrita y «(activo)»; botones «Usar
  este perfil» (o doble clic), «Nuevo...», «Renombrar...», «Borrar...» y «Cerrar». Los cambios van a
  `progress.db` en el momento (no hay Aceptar/Cancelar). El nombre se pide con `MessageDialog.GetText` y se
  vuelve a pedir si está vacío o repetido (avisos propios). Borrar pregunta «¿Borrar el perfil «X»?» con
  «También se borrarán sus N prácticas» / «No tiene prácticas» y «No se puede deshacer». El cambio de
  perfil activo llega a la sesión **en el momento** (callback de `Init`), no al cerrar el diálogo.
- `TopBar`: la raíz pasa a ser un `DockPanel` con el botón del perfil a la derecha (`SetProfileName`,
  evento `ProfilesRequested`; se retraduce en `UpdateTranslate`). `MainWindow`: `OnShowProfilesDialog`,
  `ShowActiveProfile` (al arrancar, al cambiar de perfil y al cerrar el diálogo, por si se renombró: el
  nombre de la `TopBar` no cambia hasta cerrarlo) y la entrada del menú.
- Textos nuevos en `Strings.es.resx` (diálogo, avisos, botón y menú `Per&files ...`, plural de
  «%n práctica(s)»). También se han añadido **OK / Cancel / Yes / No** (Aceptar, Cancelar, Sí, No), que no
  estaban y salían en inglés en todos los `MessageDialog`.
- Pruebas: `PracticeSessionTests` (+4: cambio de perfil a mitad descartado y la siguiente para el nuevo,
  borrar el activo a mitad, práctica terminada de un perfil borrado, `ActiveProfile.Delete` con otro, con
  el activo y con el último) y `ProgressStoreTests` (`PracticeCount` antes y después de borrar, y sin base).
  Las 72 de referencia, sin cambios; 0 avisos nuevos (solo el `CS0414` de siempre).
- **Probado en la app** (`--settings=<scratchpad>\app\s.json` en español, sin salida MIDI,
  `bc01-StartWithMiddleC.mid`, la tecla `t` mandada solo con la app delante y ratón absoluto):
  - al arrancar, «Perfil: gejoj» (el de 1.4); «Borrar...» desactivado con un solo perfil;
  - crear «Ana» y «Bea» (con «Ana» repetido: aviso «Ya hay un perfil que se llama «Ana»» y se vuelve a pedir);
  - «Usar este perfil» con Ana: `Profile/active` = 2 y «Perfil: Ana» en la barra;
  - Seguir desde el inicio, cambio a Bea en el compás 4 y hasta el final: nada guardado;
  - Seguir entera con Bea: una práctica a nombre de Bea en el `progress.db` del scratchpad (`follow`,
    `both`, 100 %, 0/170, 30 errores: la `t` pulsada antes de tiempo cuenta como equivocada, como en 1.4);
  - cerrar y volver a abrir: sigue «Perfil: Bea» (`Profile/active` = 3);
  - desde el menú, borrar Bea (la activa): «También se borrará su práctica», se borra con su práctica y
    pasa a activa Ana; renombrar Ana a «Ana M»; borrar gejoj («No tiene prácticas»); con uno solo,
    «Borrar...» desactivado;
  - Seguir entera con «Ana M»: la práctica queda a su nombre.
  Los ajustes y bases reales (`%APPDATA%\PianoBooster`) no se han tocado.

#### 1.6 Resultado al terminar e historial (rama `feature-1.6-results-ui`, necesita 1.4 y 1.5)

- **C#:** nuevo `App/PracticeResultDialog.axaml(.cs)`: al terminar una canción entera, puntuación /
  máxima, porcentaje, nº de errores por tipo, lista de errores (compás, tiempo, tipo, nota esperada y
  tocada, con nombres de nota como en la partitura) y comparación con la mejor anterior.
  Nuevo `App/ProgressDialog.axaml(.cs)` (menú): prácticas del perfil activo por canción, modo y mano.
- Más adelante (fuera de esta fase, anotar si se quiere): marcar en la partitura los compases con más
  errores; gráfica de evolución.

**Lo hecho (27 nov 2026):**

- **Decisiones (aceptadas por el usuario):**
  - **Qué práctica se acaba de guardar:** `PracticeSession.SaveFinished()` ya no devuelve cuántas, sino
    `List<SavedPractice>`: `SavedPractice(Practice, PreviousBest)`, con la práctica guardada (con su `Id` y
    sus errores) y la mejor del mismo perfil, canción, modo y mano **leída antes de guardarla** (así no es
    su propia mejor; null si es la primera). `IsNewBest` usa el orden de `BestPractice`, ahora también
    en `PracticeRecord.IsBetterThan` (Core): un empate no es nueva mejor, la más antigua sigue siendo la mejor.
    Si `SavePractice` falla (perfil borrado), no está en la lista.
  - **«Repetir canción»:** con la casilla marcada **no se abre el diálogo** (pararía las repeticiones y
    se quedaría el teclado); la práctica se guarda igual y está en el historial. Al cerrar la app
    (`Shutdown`) se guarda sin diálogo. Sin repetir, el diálogo es modal y si hubiera varias se muestra la última.
  - **Nombres de las notas:** `App/NoteText.cs`, `Name(pitch, keyOfC)`: `StavePos.MidiNote2Name` con
    `Draw.NoteNames` (traducidos: Do, Re...), ♯/♭ según la armadura de la canción cargada (como los pinta
    la partitura; las teclas blancas no cambian de nombre) y la octava científica (Do central = Do4, también
    en español). Las notas guardadas ya son la tecla pulsada (con el transporte aplicado), que es lo que
    muestra la partitura: se nombran tal cual. Una práctica de otra canción (desde el historial) se nombra
    en Do (`keyOfC`: C♯, E♭, F♯, G♯, B♭). Sin nota (la tocada de una omitida): «—».
  - **Historial:** solo el perfil activo, de consulta (no borra nada): arriba una fila por canción, modo y
    mano (prácticas, la mejor, la última), la última practicada primero; abajo las prácticas de la fila
    elegida, la más nueva primero, con la mejor en negrita y «(mejor)»; «Ver errores...» o doble clic abre
    el resultado de esa práctica (sin la comparación).
- `Core/ProgressTypes.cs`: `PracticeSummary(Song, Mode, Hand, Count, Best, LastEnded)`,
  `IProgressStore.PracticeSummaries(profileId)`, `PracticeRecord.IsBetterThan` y `WithId`.
  `Data/ProgressStore.cs`: `PracticeSummaries` (las prácticas del perfil agrupadas en memoria, `Best` sin
  errores; vacío si no hay o con error, sin lanzar) y `Song(hash)` comparte la consulta con un `SongById` interno.
- `App/PracticeResultDialog.axaml(.cs)`: canción; modo · mano · velocidad · fecha · duración; puntuación /
  máxima real y porcentaje (redondeado hacia abajo: 100 % solo si es perfecta); la comparación («Mejor
  anterior: 140 / 170 (82 %), el ...» y «¡Nueva mejor marca! N puntos porcentuales más.» / «N puntos
  porcentuales por debajo de tu mejor marca.» / «Igual que tu mejor marca.», o «Es tu primera práctica de
  esta canción en este modo y con esta mano.»); errores por tipo; la lista (compás como en la partitura,
  `Bar + Bar.FirstBarNumber`; tiempo `Beat + 1`; tipo; esperada; tocada; y la mano solo con ambas manos),
  por compás, tiempo y tick, o «Sin errores. ¡Muy bien!».
- `App/ProgressDialog.axaml(.cs)` y `Canción → Historial de prácticas...` (`historyAct`, desactivado sin
  `progress.db`, como «Perfiles...»). `App/PracticeText.cs`: los textos comunes (modo, mano, tipo,
  puntuación, fecha `g` de la cultura del sistema, duración) y `SimpleTable` (cabecera + `ListBox` de
  filas con columnas fijas, como la biblioteca).
- `MainWindow`: `SongEventUpdated` abre el resultado (`ShowPracticeResult`, `async void` con `try/catch` y
  `Log.Error`: no hay manejador global) y `OnShowProgressDialog`, también protegido.
- `Strings.Tr` con 3 y 4 argumentos. 42 textos nuevos en `Strings.es.resx` (con los plurales de «%n
  error(s)» y de los puntos porcentuales).
- Pruebas: `PracticeSessionTests` (las de `SaveFinished` con la lista, y `TheSavedPracticeComesWithTheBestBeforeIt`:
  primera, igual, mejor, peor y otra mano), `ProgressStoreTests` (`SummariesAreBySongModeAndHandOfOneProfile`
  y sin base) y `TranslationTests` (`NoteNamesOfTheResults` con armaduras de bemoles y sostenidos, en Do y
  en español; `TextsOfTheResults`). Las 72 de referencia, sin cambios; 0 avisos nuevos (solo el `CS0414` de siempre).
- **Probado en la app** (`--settings=<scratchpad>\app\s.json` en español, sin salida MIDI,
  `bc01-StartWithMiddleC.mid`, la `t` mandada solo con la app delante y ratón absoluto):
  - Seguir entera: el resultado al terminar, «0 / 170 (0 %)», «Es tu primera práctica...», 55 errores por
    tipo y la lista (Do4, «Der»/«Izq»);
  - segunda y tercera en Seguir: «Mejor anterior: 0 / 170 (0 %), el ...» e «Igual que tu mejor marca.»
    (con la `t` que se adelanta en la entrada, Seguir sale a 0, como en 1.4/1.5);
  - parar con `P` en el compás 4: no se abre nada ni se guarda;
  - Acompañar: «30 / 170 (17 %)» (primera en ese modo, omitidas con «—») y luego «170 / 170 (100 %)» con
    «¡Nueva mejor marca! 83 puntos porcentuales más.»;
  - con «Repetir canción»: la canción vuelve a empezar sin diálogo, y la práctica está en el historial;
  - historial de gejoj: Acompaña (3, mejor 170/170) y Sigue (3); las prácticas con la mejor en negrita;
    «Ver errores...» abre el resultado de una (sin comparación);
  - nuevo perfil «Ana» activo: historial vacío («Todavía no hay prácticas...»); tras una práctica, solo la suya.
  Los ajustes y bases reales (`%APPDATA%\PianoBooster`) no se han tocado.

#### 1.7 Cierre (rama `feature-1.7-close`)

- Textos nuevos en `Strings.es.resx` (y en `TranslationTests` si hace falta), `README.md` (qué es un
  perfil, qué cuenta como práctica entera), `CHANGELOG.md` y versión en `Directory.Build.props`;
  comprobar que `tools/package-windows.ps1` sigue funcionando (no hay ficheros nuevos que copiar:
  `progress.db` se crea en la carpeta de ajustes).

**Lo hecho (27 nov 2026):**

- **Textos:** todos los de 1.4–1.6 ya estaban en `Strings.es.resx`, que no cambia. Nueva prueba
  `TranslationTests.EveryTextAfterThePortIsInSpanish`: lee los ficheros nuevos de la app
  (`s_filesAfterThePort`: `ActiveProfile`, `NoteText`, `PracticeSession`, `PracticeText` y los tres
  diálogos con su `.axaml`) y comprueba que cada `Strings.Tr` / `TrPlural` (formas `#0` y `#1`) /
  `Translate` y cada texto de los `.axaml` que traduce `TranslateTree` (`Title`, `Content`, `Header`,
  `Text`, `PlaceholderText`, `ToolTip.Tip`, con la tecla de acceso pasada a `&` de Qt) tiene su clave en
  el .resx. En esos ficheros el texto tiene que ir escrito tal cual en la llamada (una variable o `"a" + "b"`
  hacen fallar la prueba: no se podrían comprobar). Los textos añadidos a ficheros del port (menús,
  botón de la `TopBar`, «User», OK/Cancel/Yes/No) van en `s_textsAddedToThePort`, que avisa si alguno
  desaparece. Los formatos sin palabras (`%1 / %2 (%3 %)`) no se exigen. **Un fichero nuevo de la
  interfaz se añade a `s_filesAfterThePort`.** `TheScanOfTheTexts` prueba la lectura (escapes, llamadas
  en varias líneas, XAML, tecla de acceso, enlaces `{...}` ignorados). Comprobado que falla con un texto
  sin traducir.
- **README.md** (en inglés): sección «Profiles and practice scores» (perfiles, práctica entera, tabla
  de puntuación por modo con zona y ventana de D2/D2b, máxima nominal y real de D11, resultado,
  historial y dónde está `progress.db`), fila 7 del Roadmap, `PianoBooster.Data` en la estructura, y el
  aviso de «work in progress» del principio actualizado (estaba desfasado desde la fase 5). En la app
  `_skill` siempre es 3, así que la omitida en Seguir solo sale con «Follow Through Errors».
- **CHANGELOG.md** con la 1.5.0 y `<Version>1.5.0</Version>` en `Directory.Build.props` (aceptado por
  el usuario). Sin tag (el de `v1.4.0` existe; crear `v1.5.0` si se quiere al publicar).
- **Paquete:** `tools/package-windows.ps1` sin cambios da `artifacts/package/PianoBoosterCS-1.5.0-win64.zip`
  (185 MB; el aviso de que el SoundFont FluidR3_GM dice «DO NOT REDISTRIBUTE» es el de siempre). El zip
  descomprimido en el scratchpad arranca con `--settings=<scratchpad>\s.json --Xquit-after` y sale con
  código 0: título «PianoBoosterCS 1.5.0», en español, «Perfil: gejoj» activo, y `progress.db` (perfil 1,
  `Profile/active` = 1) y `library.db` creados junto a `s.json`. Ojo al probar el paquete: en el primer
  arranque sin `FluidSynth/SoundFont` ni `LastSoundFontDir`, `SetupDefaultSoundFont` elige el SoundFont
  del paquete y pone `Midi/Output` = FluidSynth aunque esté vacío; para probar sin sonido, poner
  `"LastSoundFontDir": ""` en el `s.json`. En Windows no se descomprime la música (solo se guarda la ruta).
- Las 72 de referencia sin cambios (88 en `ReferenceTests` con las 16 de `SameWithAPerformanceObserver`);
  0 avisos nuevos (solo el `CS0414` de siempre). `%APPDATA%\PianoBooster` sin tocar.

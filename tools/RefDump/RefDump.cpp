// RefDump: writes what the original C++ core of PianoBooster does with a MIDI file, so
// that the C# port can be checked against it (tests/PianoBooster.Core.Tests).
//
// It is built from the original sources (see CMakeLists.txt) with stubs for the
// settings and the MIDI device. The C# twin of this driver is
// tests/PianoBooster.Core.Tests/RefDriver.cs: both MUST write exactly the same lines.
// The format is described in README.md.
//
// Usage: refdump <mode> <midi file> <output file> [key=value ...]
//   modes: events, songmap, info, notation, play

#include <QCoreApplication>
#include <QDir>
#include <QTemporaryDir>

#include <cstdio>
#include <cstring>
#include <map>
#include <string>
#include <vector>

// The system and Qt headers are included first, so that they are not affected by the
// "#define private public" below
#include <QtWidgets>
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <GL/gl.h>
#include <GL/glu.h>
#include <cassert>
#include <cmath>
#include <deque>
#include <fstream>
#include <functional>
#include <limits>

// The driver looks at some private state of the classes, to check it too
#define private public
#define protected public
#include "Song.h"
#include "Score.h"
#include "Scroll.h"
#include "Piano.h"
#include "Settings.h"
#include "SongMap.h"
#undef private
#undef protected

static FILE *out = nullptr;

static unsigned int fbits(float value)
{
    unsigned int bits;
    std::memcpy(&bits, &value, sizeof(bits));
    return bits;
}

static unsigned long long dbits(double value)
{
    unsigned long long bits;
    std::memcpy(&bits, &value, sizeof(bits));
    return bits;
}

static std::string colorStr(CColor c)
{
    char buffer[40];
    std::snprintf(buffer, sizeof(buffer), "%08x,%08x,%08x", fbits(c.red), fbits(c.green), fbits(c.blue));
    return buffer;
}

typedef std::map<std::string, std::string> Params;

static std::string param(const Params &params, const char *key, const char *def)
{
    auto it = params.find(key);
    return (it == params.end()) ? std::string(def) : it->second;
}
static int paramInt(const Params &params, const char *key, int def)
{
    return std::stoi(param(params, key, std::to_string(def).c_str()));
}
static double paramDouble(const Params &params, const char *key, double def)
{
    auto it = params.find(key);
    return (it == params.end()) ? def : std::stod(it->second);
}

//////////////////////////////////////////////////////////////////////////////
// events: the merged events of the MIDI file, as CSong reads them

static void dumpEvents(const QString &file)
{
    CMidiFile midiFile;
    midiFile.openMidiFile(file.toStdString());
    std::fprintf(out, "H %d %d %d %s\n", midiFile.numberOfTracks(), CMidiFile::getPulsesPerQuarterNote(),
                 static_cast<int>(midiFile.getMidiError()), midiFile.getSongTitle().toUtf8().constData());
    if (midiFile.getMidiError() != SMF_NO_ERROR)
        return;
    while (true)
    {
        CMidiEvent event = midiFile.readMidiEvent();
        const bool eof = event.type() == MIDI_PB_EOF;
        std::fprintf(out, "E %d %d %d %d %d %d %d\n", event.type(), event.deltaTime(), event.channel(),
                     event.note(), event.velocity(), event.getDuration(), eof ? -1 : event.track());
        if (eof)
            break;
    }
}

//////////////////////////////////////////////////////////////////////////////
// songmap: CSongMap, used by the section editor

static void dumpSongMap(const QString &file)
{
    CSongMap map;
    const bool ok = map.load(file);
    std::fprintf(out, "M %d %d %lld %lld %d %d %d\n", ok ? 1 : 0, map.ppqn(), map.lastTick(), map.maxNoteLength(),
                 map.lowestNote(), map.highestNote(), map.barCount());
    if (!ok)
        return;
    for (const CSongNote &note : map.notes())
        std::fprintf(out, "N %lld %lld %d %d %d %d\n", note.tick, note.length, note.note, note.channel, note.track, note.velocity);
    for (const CSongEvent &event : map.events())
        std::fprintf(out, "V %lld %d %d %d\n", event.tick, event.status, event.data1, event.data2);
    for (int bar = 0; bar <= map.barCount() + 1; bar++)
    {
        const qint64 start = map.barStartTick(bar);
        std::fprintf(out, "B %d %lld %d %d %lld %016llx %d %lld\n", bar, start, map.beatsInBar(bar), map.timeSigBottom(bar),
                     map.beatTicks(bar), dbits(map.msOfTick(start)), map.barOfTick(start + 1),
                     map.tickOfMs(map.msOfTick(start) + 0.5));
        const CBarPosition position = map.positionOfTick(start + map.beatTicks(bar) + 3);
        std::fprintf(out, "P %d %d %lld %lld %lld %016llx\n", position.bar, position.beat, position.ticks,
                     map.tickOfPosition(position), map.tickOfBarBeat(bar, 1.5), dbits(map.beatOfTick(start + 5)));
    }
}

//////////////////////////////////////////////////////////////////////////////
// The objects of the application, set up like QtWindow, CGLView and CSettings do

struct Env
{
    CSettings *settings = nullptr;
    CSong *song = nullptr;
    CScore *score = nullptr;
};

static Env setupEnv(const QString &iniFile, const Params &params)
{
    Cfg::setDefaults();
    Cfg::logLevel = 0;

    Env env;
    env.settings = new CSettings(iniFile);
    env.settings->m_followThroughErrors = paramInt(params, "followErrors", 0) != 0;
    env.settings->m_noteDurationTrail = paramInt(params, "trail", 0) != 0;

    // CGLView's constructor
    env.song = new CSong();
    env.score = new CScore(env.settings);
    // QtWindow's constructor
    env.song->init2(env.score, env.settings);
    env.song->getTrackList()->init(env.song, env.settings);
    // CGLView::initializeGL()
    Cfg::setStaveEndX(400);
    env.song->setActiveHand(PB_PART_both);
    env.score->init();
    env.song->regenerateChordQueue();
    // CGLView::resizeGL() for a window of 1024 x 700
    CStavePos::setStaveCenterY(296.0f);
    Cfg::setAppDimentions(0, 43, 1024, 652);
    Cfg::setStaveEndX(1004.0f);
    CStavePos::setStaveCentralOffset(84.0f);
    CDraw::forceCompileRedraw();
    return env;
}

// CSettings::setCurrentSongName()
static void loadSong(Env &env, const QString &file)
{
    env.song->loadSong(file);
    CNote::setChannelHands(-2, -2); // CSettings::loadSongSettings() without saved settings
    CTrackList *trackList = env.song->getTrackList();
    trackList->refresh();                                      // GuiSidePanel::refresh()
    trackList->currentRowChanged(trackList->getActiveItemIndex()); // its list selects the item
}

//////////////////////////////////////////////////////////////////////////////
// info: what is found out about the song when it is loaded

static void dumpInfo(Env &env)
{
    CSong *song = env.song;
    std::fprintf(out, "T %s\n", song->getSongTitle().toUtf8().constData());
    std::fprintf(out, "K %d\n", CStavePos::getKeySignature());
    std::fprintf(out, "L %d %d\n", CNote::leftHandChan(), CNote::rightHandChan());
    std::fprintf(out, "A %d\n", song->getActiveChannel());
    std::fprintf(out, "C");
    for (int chan = 0; chan < MAX_MIDI_CHANNELS; chan++)
        if (song->hasPianistKeyboardChannel(chan))
            std::fprintf(out, " %d", chan);
    std::fprintf(out, "\n");
    std::fprintf(out, "RT");
    for (int chan = 0; chan < MAX_MIDI_CHANNELS; chan++)
        std::fprintf(out, " %d", CNote::rightHandTrack(chan));
    std::fprintf(out, "\n");
    for (const QString &name : song->getTrackList()->getAllChannelProgramNames())
        std::fprintf(out, "PN %s\n", name.toUtf8().constData());
    for (int chan : song->getTrackList()->getAllChannels())
        std::fprintf(out, "CH %d\n", chan);
    int top, bottom;
    song->getTimeSig(&top, &bottom);
    std::fprintf(out, "TS %d %d\n", top, bottom);
    for (int bar = 0; bar < 40; bar++)
        std::fprintf(out, "BB %d %d\n", bar, song->beatsInBar(bar));
    std::fprintf(out, "Q %d %d\n", song->m_wantedChordQueue->length(), song->m_songEventQueue->length());
    for (int i = 0; i < song->m_wantedChordQueue->length(); i++)
    {
        CChord chord = song->m_wantedChordQueue->index(i);
        std::fprintf(out, "WQ %d %d", chord.getDeltaTime(), chord.length());
        for (int n = 0; n < chord.length(); n++)
            std::fprintf(out, " %d:%d", chord.getNote(n).pitch(), static_cast<int>(chord.getNote(n).part()));
        std::fprintf(out, "\n");
    }
}

//////////////////////////////////////////////////////////////////////////////
// notation: the slots (notes, bar lines and beat markers) of the score

static void dumpSlot(CSlot &slot)
{
    std::fprintf(out, "S %lld %lld %d %d\n", slot.getDeltaTime(), slot.getLeftSideDeltaTime(), slot.getAv8Left(), slot.length());
    for (int i = 0; i < slot.length(); i++)
    {
        CSymbol symbol = slot.getSymbol(i);
        CStavePos stavePos = symbol.getStavePos();
        std::fprintf(out, "Y %d %d %d %d %d %s %d %d %d %d %d %lld %08x\n", static_cast<int>(symbol.getType()),
                     static_cast<int>(symbol.getHand()), symbol.getNote(), stavePos.getStaveIndex(), stavePos.getAccidental(),
                     colorStr(symbol.getColor()).c_str(), symbol.getMidiDuration(), static_cast<int>(symbol.getAccidentalModifer()),
                     symbol.getBarNumber(), symbol.getNoteIndex(), symbol.getNoteTotal(), symbol.getPianistTiming(),
                     fbits(stavePos.getPosY()));
    }
}

static void dumpNotation(Env &env, const QString &file)
{
    CNotation notation;
    notation.setChannel(env.song->getActiveChannel());
    notation.reset();
    CMidiFile midiFile;
    midiFile.openMidiFile(file.toStdString());
    bool eof = false;
    for (int count = 0; count < 100000; count++)
    {
        while (!eof && notation.midiEventSpace() > 100)
        {
            CMidiEvent event = midiFile.readMidiEvent();
            notation.midiEventInsert(event);
            if (event.type() == MIDI_PB_EOF)
                eof = true;
        }
        CSlot slot = notation.nextSlot();
        dumpSlot(slot);
        if (slot.length() == 0 || slot.getSymbolType(0) == PB_SYMBOL_theEndMarker)
            break;
    }
}

//////////////////////////////////////////////////////////////////////////////
// play: plays the song with a pianist that follows simple fixed rules

static int s_step = 0;

static void onOutput(const CMidiEvent &event)
{
    CMidiEvent e = event;
    std::fprintf(out, "O %d %d %d %d %d\n", s_step, e.type(), e.channel(), e.note(), e.velocity());
}

static std::string chordStr(CChord chord)
{
    std::string s = std::to_string(chord.length());
    for (int i = 0; i < chord.length(); i++)
        s += " " + std::to_string(chord.getNote(i).pitch()) + ":" + std::to_string(static_cast<int>(chord.getNote(i).part()));
    return s;
}

static void pianistNote(bool on, int note)
{
    CMidiEvent event;
    if (on)
        event.noteOnEvent(0, 0, note, 64);
    else
        event.noteOffEvent(0, 0, note, 64);
    CMidiDevice::s_input.push_back(event);
}

static void dumpPlay(Env &env, const Params &params)
{
    CSong *song = env.song;
    CScore *score = env.score;

    const std::string mode = param(params, "mode", "follow");
    int playMode = PB_PLAY_MODE_followYou;
    if (mode == "listen") playMode = PB_PLAY_MODE_listen;
    else if (mode == "along") playMode = PB_PLAY_MODE_playAlong;
    else if (mode == "rhythm") playMode = PB_PLAY_MODE_rhythmTapping;
    const std::string hand = param(params, "hand", "both");
    whichPart_t part = PB_PART_both;
    if (hand == "right") part = PB_PART_right;
    else if (hand == "left") part = PB_PART_left;

    const int steps = paramInt(params, "steps", 6000);
    const int tick = paramInt(params, "tick", 4);
    const int transpose = paramInt(params, "transpose", 0);
    // The pianist plays the wanted chord when it is this far from its time (in ticks of
    // 96 ppqn, negative for early), using the values in turn
    const int offsets[] = {-10, 0, 5, -20, 2, -3, 8};
    const int wrongEvery = paramInt(params, "wrongEvery", 7); // a wrong note before every Nth chord (0 for never)
    const int holdSteps = paramInt(params, "hold", 30);
    const int giveUpSteps = 400; // plays anyway after waiting this long

    CMidiDevice::s_output = onOutput;
    song->setPlayMode(playMode);
    song->setActiveHand(part);
    song->setSkill(paramInt(params, "skill", 3));
    song->setSpeed(static_cast<float>(paramDouble(params, "speed", 1.0)));
    song->transpose(transpose);
    song->mutePianistPart(paramInt(params, "mute", 1) != 0);
    song->boostVolume(paramInt(params, "boost", 0));
    song->pianoVolume(paramInt(params, "piano", 0));
    song->setLatencyFix(paramInt(params, "latency", 0));
    song->cfg_timingMarkersFlag = paramInt(params, "markers", 0) != 0;
    const double from = paramDouble(params, "from", 0.0);
    const double upto = paramDouble(params, "upto", 0.0);
    song->setPlayFromBar(from);
    song->setPlayUptoBar(upto);
    std::fprintf(out, "START\n");
    song->playFromStartBar();

    std::string lastWanted = "0";
    int chordCounter = 0;
    int chordSince = 0;
    int attempts = 0;
    int nextTryStep = 0;
    int releaseAt = -1;
    std::vector<int> pressed;
    int lastBar = -1;
    std::string lastRating;
    std::string lastPiano;

    for (s_step = 0; s_step < steps; s_step++)
    {
        const eventBits_t bits = song->task(tick);
        if (bits != 0)
            std::fprintf(out, "B %d %lu\n", s_step, static_cast<unsigned long>(bits));
        if (bits & EVENT_BITS_UptoBarReached)
            song->playFromStartBar();
        if (s_step % 4 == 3)
            score->drawScrollingSymbols(true);

        if (song->getBarNumber() != lastBar)
        {
            lastBar = song->getBarNumber();
            std::fprintf(out, "N %d %d %016llx\n", s_step, lastBar, dbits(song->getCurrentBarPos()));
        }

        CRating *rating = song->getRating();
        char buffer[200];
        std::snprintf(buffer, sizeof(buffer), "%d %d %d %08x %d", rating->totalNoteCount(), rating->wrongNoteCount(),
                      rating->lateNoteCount(), fbits(rating->getAccuracyValue()), rating->isAccuracyGood() ? 1 : 0);
        if (lastRating != buffer)
        {
            lastRating = buffer;
            std::fprintf(out, "R %d %s\n", s_step, buffer);
        }

        CPiano *piano = score->getPianoObject();
        std::snprintf(buffer, sizeof(buffer), "%d %d %u", piano->pianistAllNotesDown(), piano->pianistBadNotesDown(),
                      piano->m_noteNameListLength);
        if (lastPiano != buffer)
        {
            lastPiano = buffer;
            std::fprintf(out, "PI %d %s\n", s_step, buffer);
        }

        CChord wanted = song->getWantedChord();
        std::string wantedStr = chordStr(wanted);
        if (wantedStr != lastWanted)
        {
            lastWanted = wantedStr;
            std::fprintf(out, "W %d %s\n", s_step, wantedStr.c_str());
            if (wanted.length() > 0)
            {
                chordCounter++;
                chordSince = s_step;
                attempts = 0;
                nextTryStep = s_step;
            }
        }

        if (s_step == releaseAt)
        {
            for (int note : pressed)
                pianistNote(false, note);
            pressed.clear();
            releaseAt = -1;
        }
        if (wanted.length() > 0 && pressed.empty() && s_step >= nextTryStep)
        {
            const qint64 threshold = static_cast<qint64>(offsets[(chordCounter + attempts) % 7]) *
                                     CMidiFile::getPulsesPerQuarterNote() / DEFAULT_PPQN * SPEED_ADJUST_FACTOR;
            if (song->m_chordDeltaTime >= threshold || s_step - chordSince >= giveUpSteps)
            {
                if (wrongEvery > 0 && chordCounter % wrongEvery == 3 && attempts == 0)
                {
                    const int wrong = wanted.getNote(0).pitch() + transpose + 1;
                    pianistNote(true, wrong);
                    pressed.push_back(wrong);
                }
                for (int i = 0; i < wanted.length(); i++)
                {
                    const int note = wanted.getNote(i).pitch() + transpose;
                    pianistNote(true, note);
                    pressed.push_back(note);
                }
                attempts++;
                releaseAt = s_step + holdSteps;
                nextTryStep = s_step + holdSteps + 20;
            }
        }

        if (s_step % 50 == 0)
        {
            CScroll *scroll = (score->m_activeScroll >= 0) ? score->m_scroll[score->m_activeScroll] : nullptr;
            std::fprintf(out, "Z %d %d %lld %lld %d %d %lld %lld", s_step, static_cast<int>(song->m_followState),
                         song->m_chordDeltaTime, song->m_playingDeltaTime, song->m_songEventQueue->length(),
                         song->m_wantedChordQueue->length(), song->m_pianistTiming, song->m_stopPoint);
            if (scroll)
                std::fprintf(out, " %d %lld %lld %d %lld %d", scroll->m_scrollQueue->length(), scroll->m_deltaHead,
                             scroll->m_deltaTail, scroll->m_wantedIndex, scroll->m_wantedDelta, scroll->m_trailOnlySlots);
            std::fprintf(out, " %08x\n", fbits(song->getSpeed()));
        }
        if (s_step % 500 == 0 && score->m_activeScroll >= 0)
        {
            CScroll *scroll = score->m_scroll[score->m_activeScroll];
            for (int i = 0; i < scroll->m_scrollQueue->length(); i++)
            {
                CSlot slot = scroll->m_scrollQueue->index(i);
                dumpSlot(slot);
            }
        }

        if (bits & EVENT_BITS_playingStopped)
        {
            std::fprintf(out, "STOP %d\n", s_step);
            break;
        }
    }
    CMidiDevice::s_output = nullptr;
    std::fprintf(out, "END %d %d %d %016llx\n", song->getRating()->totalNoteCount(), song->getRating()->wrongNoteCount(),
                 song->getRating()->lateNoteCount(), dbits(song->getRating()->rating()));
}

int main(int argc, char *argv[])
{
    QCoreApplication app(argc, argv);
    if (argc < 4)
    {
        std::fprintf(stderr, "Usage: refdump <events|songmap|info|notation|play> <midi file> <output file> [key=value ...]\n");
        return 2;
    }
    const std::string mode = argv[1];
    const QString file = QString::fromLocal8Bit(argv[2]);
    out = std::fopen(argv[3], "wb");
    if (!out)
    {
        std::fprintf(stderr, "Cannot write %s\n", argv[3]);
        return 2;
    }
    Params params;
    for (int i = 4; i < argc; i++)
    {
        const std::string arg = argv[i];
        const auto eq = arg.find('=');
        if (eq != std::string::npos)
            params[arg.substr(0, eq)] = arg.substr(eq + 1);
    }

    QTemporaryDir tempDir;
    const QString iniFile = tempDir.path() + "/refdump.ini";

    if (mode == "events")
        dumpEvents(file);
    else if (mode == "songmap")
        dumpSongMap(file);
    else
    {
        Env env = setupEnv(iniFile, params);
        // CMidiDevice::s_output is only set while playing: the events sent while the song is
        // loaded go nowhere
        loadSong(env, file);
        if (mode == "info")
            dumpInfo(env);
        else if (mode == "notation")
            dumpNotation(env, file);
        else if (mode == "play")
            dumpPlay(env, params);
        else
        {
            std::fprintf(stderr, "Unknown mode %s\n", mode.c_str());
            return 2;
        }
    }
    std::fclose(out);
    return 0;
}

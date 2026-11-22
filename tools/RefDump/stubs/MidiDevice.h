// Stub of PianoBooster's MidiDevice.h for the reference dump tool (RefDump).
// The output events are recorded and the input events come from a queue that the
// driver fills, so that a song can be "played" without any MIDI hardware.
#ifndef __MIDI_DEVICE_H__
#define __MIDI_DEVICE_H__

#include <deque>
#include <functional>
#include <QString>
#include <QSettings>

#include "Util.h"
#include "MidiEvent.h"

class CMidiDevice
{
public:
    typedef enum {MIDI_INPUT, MIDI_OUTPUT} midiType_t;

    void init() {}
    void playMidiEvent(const CMidiEvent &event)
    {
        if (s_output)
            s_output(event);
    }
    int checkMidiInput() { return static_cast<int>(s_input.size()); }
    CMidiEvent readMidiInput()
    {
        CMidiEvent event = s_input.front();
        s_input.pop_front();
        return event;
    }
    bool validMidiOutput() { return true; }
    bool validMidiConnection() { return true; }
    bool openMidiPort(midiType_t, const QString &) { return true; }
    void closeMidiPort(midiType_t, int) {}
    void setQSettings(QSettings *) {}
    void flushMidiInput() { s_input.clear(); }
    // the real CMidiDevice is a QObject, whose tr() CConductor uses
    static QString tr(const char *text) { return QString::fromUtf8(text); }

    static std::function<void(const CMidiEvent &)> s_output;
    static std::deque<CMidiEvent> s_input;
};

#endif // __MIDI_DEVICE_H__

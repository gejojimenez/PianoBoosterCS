// Stub of PianoBooster's Settings.h for the reference dump tool (RefDump).
// It replaces the real CSettings, which needs the whole Qt interface, with only
// what the core classes (Conductor, Score, Scroll, Piano, Draw, TrackList) use.
#ifndef __SETTINGS_H__
#define __SETTINGS_H__

#include <QSettings>
#include "Song.h"
#include "Notation.h"

class CSettings : public QSettings
{
public:
    explicit CSettings(const QString &iniFile) : QSettings(iniFile, QSettings::IniFormat) {}

    void setAdvancedMode(bool value) { m_advancedMode = value; }
    bool advancedMode() const { return m_advancedMode; }
    bool showNoteNames() { return m_showNoteNames; }
    bool coloredNotes() { return false; }
    bool followThroughErrors() { return m_followThroughErrors; }
    bool isNoteDurationTrailEnabled() { return m_noteDurationTrail; }
    void updateWarningMessages() {}
    QString getWarningMessage() { return QString(); }
    void setChannelHands(int left, int right) { CNote::setChannelHands(left, right); }

    bool m_followThroughErrors = false;
    bool m_noteDurationTrail = false;
    bool m_showNoteNames = false;

private:
    bool m_advancedMode = false;
};

#endif // __SETTINGS_H__

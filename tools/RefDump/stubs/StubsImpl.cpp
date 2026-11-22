// Implementations for the stubs of the reference dump tool (RefDump).
#include "MidiDevice.h"
#include "KeepDisplayOn.h"

std::function<void(const CMidiEvent &)> CMidiDevice::s_output;
std::deque<CMidiEvent> CMidiDevice::s_input;

// The display is not kept on (that would change the power settings of the machine)
void CKeepDisplayOn::setRequired(bool) {}
void CKeepDisplayOn::userActivity() {}

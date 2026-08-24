#pragma once

#include <string.h>

// Persistent, personality-independent device toggles set via SOUND/
// SCANLINES (see PROTOCOL.md) — kept separate from Personality because
// they're about how the physical device itself behaves (audio, screen
// post-FX), not Brobot's expression/behavior state that Personality/
// FaceState already model (Personality's own doc comment is explicit that
// it "knows nothing about how any of this gets drawn"). Both default to
// enabled so a PC app that never sends either command sees the same
// behavior MiMo already had before these toggles existed.
class DeviceSettings {
public:
    void onSoundCommand(const char* args) { _soundEnabled = parseOnOff(args, _soundEnabled); }
    void onScanlinesCommand(const char* args) { _scanlinesEnabled = parseOnOff(args, _scanlinesEnabled); }

    bool soundEnabled() const { return _soundEnabled; }
    bool scanlinesEnabled() const { return _scanlinesEnabled; }

private:
    bool _soundEnabled = true;
    bool _scanlinesEnabled = true;

    // Unrecognized text leaves the setting unchanged rather than picking a
    // default — same "ignore what we don't understand" leniency
    // Protocol::dispatch already applies to unknown commands.
    static bool parseOnOff(const char* args, bool current) {
        if (strcmp(args, "ON") == 0) return true;
        if (strcmp(args, "OFF") == 0) return false;
        return current;
    }
};

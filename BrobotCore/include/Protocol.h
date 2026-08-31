#pragma once

#include <Arduino.h>
#include "DeviceSettings.h"
#include "Personality.h"

// Reads control commands (FACE / MSG, see PROTOCOL.md) off a Stream one
// byte at a time and dispatches complete lines to a Personality, or to a
// DeviceSettings for the handful of commands (SOUND/SCANLINES) that aren't
// about Brobot's expression/behavior state. Never blocks — safe to call
// every loop() iteration.
//
// PING is the one command answered here rather than forwarded anywhere:
// it's about the link itself, not about Brobot, so there's no Personality
// or DeviceSettings state for it to touch.
class Protocol {
public:
    Protocol(Personality& personality, DeviceSettings& deviceSettings)
        : _personality(personality), _deviceSettings(deviceSettings) {}

    void poll(Stream& serial, unsigned long now);

private:
    // Must comfortably fit "MSG " + the longest message Personality accepts.
    static constexpr size_t LINE_CAPACITY = 264;
    // If a gap this long passes without a completed line, whatever partial
    // bytes are buffered are stale (noise, a dropped newline, a client that
    // disconnected mid-command) and get discarded before the next byte is
    // appended — otherwise they'd silently corrupt the next real command.
    static constexpr unsigned long LINE_STALE_TIMEOUT_MS = 300;

    char _line[LINE_CAPACITY] = {0};
    size_t _length = 0;
    unsigned long _lastByteAt = 0;
    Personality& _personality;
    DeviceSettings& _deviceSettings;

    // Takes the Stream (rather than only poll() holding it) purely so PING
    // can write its reply back to whoever asked — every other command is
    // one-way PC->Core.
    void dispatch(Stream& serial, char* line, unsigned long now);
};

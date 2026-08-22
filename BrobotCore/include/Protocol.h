#pragma once

#include <Arduino.h>
#include "Personality.h"

// Reads control commands (FACE / MSG, see PROTOCOL.md) off a Stream one
// byte at a time and dispatches complete lines to a Personality. Never
// blocks — safe to call every loop() iteration.
class Protocol {
public:
    explicit Protocol(Personality& personality) : _personality(personality) {}

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

    void dispatch(char* line, unsigned long now);
};

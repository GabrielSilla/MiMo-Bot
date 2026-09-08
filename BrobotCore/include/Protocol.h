#pragma once

#include <Arduino.h>
#include "Buzzer.h"
#include "DeviceSettings.h"
#include "Personality.h"
#include "PongGame.h"
#include "RpgBattle.h"

// Reads control commands (FACE / MSG, see PROTOCOL.md) off a Stream one
// byte at a time and dispatches complete lines to a Personality, to a
// DeviceSettings for the handful of commands (SOUND/SCANLINES) that aren't
// about Brobot's expression/behavior state, or to a PongGame/RpgBattle for
// the two exclusive minigame command families (see PROTOCOL.md's Pong and
// RPG Battle sections). Never blocks — safe to call every loop() iteration.
//
// PING is the one command answered here rather than forwarded anywhere:
// it's about the link itself, not about Brobot, so there's no Personality
// or DeviceSettings state for it to touch.
//
// Also the one place that arbitrates between the two minigames — PongGame
// and RpgBattle stay unaware of each other (same separation Personality and
// PongGame already have), so a PONG/RPG START is simply ignored while the
// other minigame is already active, rather than letting one clobber the
// other's exclusive screen.
//
// BUZZ <cue> triggers a Buzzer cue directly, bypassing Personality/
// Expression entirely — a manual hook for testing a cue (e.g. the RPG
// victory fanfare) without having to actually win a battle first. See
// Protocol.cpp's dispatch for the recognized cue names.
class Protocol {
public:
    Protocol(Personality& personality, DeviceSettings& deviceSettings, PongGame& pongGame, RpgBattle& rpgBattle,
             Buzzer& buzzer)
        : _personality(personality), _deviceSettings(deviceSettings), _pongGame(pongGame), _rpgBattle(rpgBattle),
          _buzzer(buzzer) {}

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
    PongGame& _pongGame;
    RpgBattle& _rpgBattle;
    Buzzer& _buzzer;

    // Takes the Stream (rather than only poll() holding it) purely so PING
    // can write its reply back to whoever asked — every other command is
    // one-way PC->Core.
    void dispatch(Stream& serial, char* line, unsigned long now);
};

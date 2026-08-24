#pragma once

#include <Arduino.h>

#include "Face.h" // Expression

// One step of a sound cue. A fixed cue is just an array of these, played
// back to back — the same "declarative table instead of imperative code"
// shape BEDTIME_MESSAGES/PausaMessages already use elsewhere in this
// codebase. THINKING doesn't use a fixed array at all (see
// Buzzer::nextChatterSegment in the .cpp) — it generates one of these on
// the fly, with randomized frequencies/durations, every time the previous
// one finishes.
enum class SoundSegmentKind : uint8_t { SWEEP, TRILL, TONE, SILENCE };

struct SoundSegment {
    SoundSegmentKind kind;
    int freqA;                // SWEEP: start freq / TRILL: freq1 / TONE: the one freq (unused for SILENCE)
    int freqB;                // SWEEP: end freq / TRILL: freq2 (unused for TONE/SILENCE)
    unsigned long durationMs; // how long this segment occupies in total
    unsigned long stepMs;     // SWEEP/TRILL only: how often the tone re-triggers with a new frequency
};

// Non-blocking R2D2-style beeps on BUZZER_PIN, adapted from an early
// breadboard test sketch whose sweep()/trill() helpers stepped through
// frequencies with delay() between each one. That blocks for the sound's
// entire duration (up to whole seconds) — fine for a standalone test
// sketch, fatal here, since it would freeze the ~60fps render loop and stop
// Protocol::poll from reading incoming FACE/MSG commands for as long as a
// sound was playing. This is a from-scratch rewrite of the same sound
// shapes as a small state machine stepped from nowMs instead (see
// Buzzer.cpp) — same "no delay(), driven purely off nowMs" approach every
// other timed effect in this codebase already uses (blink, look-around,
// THINKING's eye glitch, COFFEE's steam, ...).
//
// Firmware-only: unlike Face/Personality/Protocol, this isn't part of the
// native dev build's shared source list (see native/build.ps1) — there's no
// speaker on a dev PC, so main_native.cpp simply never references it, the
// same way ST7735PhysicalDisplay/WifiSetup are already PlatformIO-only.
class Buzzer {
public:
    void begin();
    void update(unsigned long nowMs);

    // Starts the cue mapped to this expression (see Buzzer.cpp's switch),
    // replacing whatever's currently playing, or silences the buzzer if
    // this expression has no cue of its own. Call only on an actual
    // expression *change* (see main.cpp) — calling this every frame while
    // the expression stays the same would keep restarting a still-playing
    // cue from its first segment, so a one-shot cue would never finish and
    // THINKING's endless chatter would never be allowed to actually vary.
    void playForExpression(Expression expression, unsigned long nowMs);

    // Immediately silences the buzzer, regardless of what's currently
    // playing — used when the SOUND device setting is off (see
    // DeviceSettings/main.cpp), safe to call every frame.
    void mute();

private:
    // Produces the next segment to play into `out` and returns true, or
    // returns false once there's nothing left (ending the cue) — one
    // implementation walks a fixed table (nextFixedSegment, for
    // FINISHED/FAILED/READING), the other generates a fresh random R2D2
    // "chirp" or inter-chirp gap every time and never returns false
    // (nextChatterSegment, for THINKING) — update()'s step-triggering logic
    // is written once and doesn't care which kind is feeding it.
    using SegmentSupplier = bool (*)(Buzzer& self, SoundSegment& out);

    void play(SegmentSupplier supplier, unsigned long nowMs);
    void stop();
    void beginSegment(const SoundSegment& seg, unsigned long nowMs);

    static bool nextFixedSegment(Buzzer& self, SoundSegment& out);
    static bool nextChatterSegment(Buzzer& self, SoundSegment& out);

    SegmentSupplier _supplier = nullptr;
    SoundSegment _current{};
    unsigned long _segmentStartedAt = 0;
    int _lastStepIndex = -1; // -1 = no step played yet within the current segment

    // nextFixedSegment's own iteration state.
    const SoundSegment* _fixedSegments = nullptr;
    uint8_t _fixedSegmentCount = 0;
    uint8_t _fixedSegmentIndex = 0;

    // nextChatterSegment's own state: alternates between a chirp and a
    // silent gap, same shape sayPensando()'s original delay()-separated
    // loop had.
    bool _chatterInGap = false;
};

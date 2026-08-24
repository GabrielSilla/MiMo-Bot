#include "Buzzer.h"

#include "Config.h"

namespace {

// Every frequency in this file was shifted up from the original breadboard
// test sketch's own numbers, all the way through THINKING's chatter below —
// small passive piezos are far louder near their resonant peak (roughly
// 2-4kHz for the cheap ones these R2D2 sounds were designed around) than
// down at a few hundred Hz, and on this ESP32-C3 build (3.3V GPIO swing,
// versus 5V on the Uno the original sketch was tested on — no amount of
// frequency tuning recovers *that* difference) every bit of loudness matters
// more than it did there. Shifting up trades away some of the deeper tones
// the original had, but keeps each cue's relative shape (which leg rises,
// which falls, trill spread) intact — R2D2's own voice leans high-pitched
// anyway, so the character survives the move reasonably well.

// Adapted from the breadboard test's sayTerminei(): a triumphant rising
// sweep in three legs. One-shot — plays once when FINISHED starts, same as
// its "Terminei!" message.
constexpr SoundSegment CUE_FINISHED[] = {
    {SoundSegmentKind::SWEEP, 1800, 2600, 130, 21},
    {SoundSegmentKind::SILENCE, 0, 0, 40, 0},
    {SoundSegmentKind::SWEEP, 2400, 3400, 150, 21},
    {SoundSegmentKind::SILENCE, 0, 0, 40, 0},
    {SoundSegmentKind::SWEEP, 3600, 2200, 320, 26},
};

// Adapted from sayAtencao(): three quick high trills — an "uh-oh" chirp for
// FAILED. The original also had a much longer sayAlarme() (20s of siren),
// deliberately not used here: nothing in Core's current expression
// vocabulary represents a sustained alarm, and 20s is far too long to fire
// automatically off a single expression change.
constexpr SoundSegment CUE_FAILED[] = {
    {SoundSegmentKind::TRILL, 2000, 2800, 160, 20},
    {SoundSegmentKind::SILENCE, 0, 0, 60, 0},
    {SoundSegmentKind::TRILL, 2000, 2800, 160, 20},
    {SoundSegmentKind::SILENCE, 0, 0, 60, 0},
    {SoundSegmentKind::TRILL, 2000, 2800, 160, 20},
};

// Adapted from sayOlhaAqui(): an up-then-down curious chirp, one-shot when
// READING starts.
constexpr SoundSegment CUE_READING[] = {
    {SoundSegmentKind::SWEEP, 2200, 3800, 200, 20},
    {SoundSegmentKind::SILENCE, 0, 0, 40, 0},
    {SoundSegmentKind::SWEEP, 3800, 2000, 170, 19},
};

template <size_t N>
constexpr uint8_t count(const SoundSegment (&)[N]) {
    return static_cast<uint8_t>(N);
}

} // namespace

void Buzzer::begin() {
    pinMode(BUZZER_PIN, OUTPUT);
}

void Buzzer::playForExpression(Expression expression, unsigned long nowMs) {
    switch (expression) {
        case Expression::FINISHED:
            _fixedSegments = CUE_FINISHED;
            _fixedSegmentCount = count(CUE_FINISHED);
            _fixedSegmentIndex = 0;
            play(&Buzzer::nextFixedSegment, nowMs);
            break;
        case Expression::FAILED:
            _fixedSegments = CUE_FAILED;
            _fixedSegmentCount = count(CUE_FAILED);
            _fixedSegmentIndex = 0;
            play(&Buzzer::nextFixedSegment, nowMs);
            break;
        case Expression::READING:
            _fixedSegments = CUE_READING;
            _fixedSegmentCount = count(CUE_READING);
            _fixedSegmentIndex = 0;
            play(&Buzzer::nextFixedSegment, nowMs);
            break;
        case Expression::THINKING:
            // Starts on a chirp, not a gap — R2D2 starts "talking"
            // immediately rather than sitting in silence for the first
            // 100-250ms of THINKING.
            _chatterInGap = false;
            play(&Buzzer::nextChatterSegment, nowMs);
            break;
        default:
            // No cue mapped to this expression — also cuts off whatever was
            // playing, which matters specifically for THINKING's endless
            // chatter: without this it would keep going forever once
            // THINKING ends, since nothing else would ever tell it to stop.
            stop();
            break;
    }
}

bool Buzzer::nextFixedSegment(Buzzer& self, SoundSegment& out) {
    if (self._fixedSegmentIndex >= self._fixedSegmentCount) {
        return false;
    }
    out = self._fixedSegments[self._fixedSegmentIndex];
    self._fixedSegmentIndex++;
    return true;
}

// Directly mirrors sayPensando()'s random branch-picking (tipo 0-3: an
// up-sweep, a down-sweep, a trill, or a bare blip), just spread across
// separate update() calls instead of a blocking 3-second while/delay loop,
// and with no fixed total duration — it keeps generating a fresh random
// chirp for as long as THINKING stays the active expression, giving the
// "R2D2 muttering to itself" character the original had rather than a
// short, identically-repeating clip. Never returns false: the chatter only
// ever stops via Buzzer::stop() (see playForExpression's default: branch)
// when THINKING itself ends.
bool Buzzer::nextChatterSegment(Buzzer& self, SoundSegment& out) {
    if (!self._chatterInGap) {
        self._chatterInGap = true;
        out = {SoundSegmentKind::SILENCE, 0, 0, (unsigned long)random(100, 250), 0};
        return true;
    }

    self._chatterInGap = false;
    // Shifted up from the original sayPensando()'s 280-700Hz range toward
    // this piezo's louder ~2-4kHz band — see the file-level comment above
    // CUE_FINISHED for why. Kept the same relative spread/ratio between the
    // four "tipo" shapes so the chatter still reads as clearly varied, not
    // just louder.
    int tipo = random(0, 4);
    switch (tipo) {
        case 0: {
            unsigned long dur = (unsigned long)random(100, 180);
            out = {SoundSegmentKind::SWEEP, (int)random(1800, 2200), (int)random(2600, 3000), dur, dur / 6};
            break;
        }
        case 1: {
            unsigned long dur = (unsigned long)random(100, 180);
            out = {SoundSegmentKind::SWEEP, (int)random(2600, 3000), (int)random(1800, 2200), dur, dur / 6};
            break;
        }
        case 2: {
            unsigned long dur = (unsigned long)random(150, 250);
            out = {SoundSegmentKind::TRILL, (int)random(2000, 2400), (int)random(3200, 3600), dur, 25};
            break;
        }
        default:
            out = {SoundSegmentKind::TONE, (int)random(1800, 3400), 0, 90, 0};
            break;
    }
    return true;
}

void Buzzer::play(SegmentSupplier supplier, unsigned long nowMs) {
    _supplier = supplier;
    if (!_supplier(*this, _current)) {
        stop();
        return;
    }
    beginSegment(_current, nowMs);
}

void Buzzer::stop() {
    _supplier = nullptr;
    noTone(BUZZER_PIN);
}

void Buzzer::mute() {
    stop();
}

// Fires whatever this segment's very first sound is immediately, rather
// than waiting for the next update() tick to notice — otherwise every
// segment transition would carry an audible gap up to one update() period
// long.
void Buzzer::beginSegment(const SoundSegment& seg, unsigned long nowMs) {
    _segmentStartedAt = nowMs;
    switch (seg.kind) {
        case SoundSegmentKind::SILENCE:
            _lastStepIndex = -1;
            noTone(BUZZER_PIN);
            break;
        case SoundSegmentKind::TONE:
            _lastStepIndex = -1;
            tone(BUZZER_PIN, seg.freqA, seg.durationMs);
            break;
        case SoundSegmentKind::SWEEP:
        case SoundSegmentKind::TRILL:
            _lastStepIndex = 0;
            tone(BUZZER_PIN, seg.freqA, (seg.stepMs > 0 ? seg.stepMs : seg.durationMs) + 5);
            break;
    }
}

void Buzzer::update(unsigned long nowMs) {
    if (_supplier == nullptr) {
        return;
    }

    unsigned long elapsed = nowMs - _segmentStartedAt;

    if (elapsed >= _current.durationMs) {
        if (!_supplier(*this, _current)) {
            stop();
            return;
        }
        beginSegment(_current, nowMs);
        return;
    }

    // SWEEP/TRILL re-trigger tone() with a new frequency each time the
    // sub-step advances; TONE/SILENCE only ever fire once, from
    // beginSegment() above, so there's nothing to do here for them.
    if (_current.kind != SoundSegmentKind::SWEEP && _current.kind != SoundSegmentKind::TRILL) {
        return;
    }

    unsigned long stepMs = (_current.stepMs > 0) ? _current.stepMs : _current.durationMs;
    int stepIndex = (int)(elapsed / stepMs);
    if (stepIndex == _lastStepIndex) {
        return;
    }
    _lastStepIndex = stepIndex;

    int freq;
    if (_current.kind == SoundSegmentKind::TRILL) {
        freq = (stepIndex % 2 == 0) ? _current.freqA : _current.freqB;
    } else {
        // SWEEP: linear interpolation of the target frequency across the
        // whole segment's duration, evaluated at this step's elapsed time —
        // matches the original sweep()'s per-step frequency formula.
        long span = (long)_current.freqB - (long)_current.freqA;
        freq = _current.freqA + (int)(span * (long)elapsed / (long)_current.durationMs);
    }
    tone(BUZZER_PIN, freq, stepMs + 5);
}

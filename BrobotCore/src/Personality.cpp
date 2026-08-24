#include "Personality.h"

#include <Arduino.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

namespace {

constexpr unsigned long BLINK_DURATION_MS = 280; // longer than a real blink so ~20fps still yields several frames

constexpr unsigned long BLINK_MIN_INTERVAL_MS = 2000;
constexpr unsigned long BLINK_MAX_INTERVAL_MS = 6000;

// Look-around: ease to a direction, hold there a while, ease back to center.
constexpr unsigned long LOOK_EASE_MS = 300;
constexpr unsigned long LOOK_HOLD_MIN_MS = 1000;
constexpr unsigned long LOOK_HOLD_MAX_MS = 2000;
constexpr unsigned long LOOK_MIN_INTERVAL_MS = 6000;
constexpr unsigned long LOOK_MAX_INTERVAL_MS = 14000;
// Large enough that a look swings the eyes out near the screen edges
// (160x128 frame, ~42px eyes) without ever clipping off it.
constexpr int LOOK_OFFSET_X_PX = 24;
constexpr int LOOK_OFFSET_Y_PX = 16;

// Left, right, up, down, and the four corners — picked at random each time.
constexpr int8_t LOOK_DIRECTIONS[8][2] = {
    {-1, 0}, {1, 0}, {0, -1}, {0, 1},
    {-1, -1}, {1, -1}, {-1, 1}, {1, 1},
};
constexpr uint8_t LOOK_DIRECTION_COUNT = 8;

// MATRIX pins the eyes near the bottom edge (see Face.cpp's
// MATRIX_EYE_BOTTOM_MARGIN) — there's little clearance left to look further
// down there, so down/down-left/down-right are dropped from the pool while
// this theme is active. Indices into LOOK_DIRECTIONS rather than a separate
// offset table, so both pools stay in sync with LOOK_OFFSET_X_PX/Y_PX.
constexpr uint8_t LOOK_DIRECTIONS_NO_DOWN[5] = {0, 1, 2, 4, 5};
constexpr uint8_t LOOK_DIRECTIONS_NO_DOWN_COUNT = 5;

constexpr unsigned long MESSAGE_DURATION_MS = 10000; // how long a MSG stays on screen before clearing itself
constexpr unsigned long TYPING_CHAR_INTERVAL_MS = 40; // typewriter reveal speed
constexpr unsigned long FACE_OVERRIDE_DURATION_MS = 4000;
constexpr unsigned long SLEEP_TIMEOUT_MS = 10UL * 60UL * 1000UL; // 10 minutes idle before sleeping

// SLEEPY (drowsy, not yet the deep SLEEPING) kicks in purely off the clock,
// independent of _lastInteractionAt — someone actively chatting with the AI
// at 23h should still see MiMo get sleepy. Window spans midnight the same
// way Face.cpp's own isNightHour does, since no separate "wake up" time was
// specified — just reused that existing day/night boundary.
constexpr int BEDTIME_START_HOUR = 22;
constexpr int BEDTIME_END_HOUR = 6;

// A slower, heavier-lidded blink while SLEEPY — see updateBlink — so it's
// obviously not the same quick blink NEUTRAL uses.
constexpr unsigned long BLINK_DURATION_SLEEPY_MS = 900;

// One random pick every 30 minutes for as long as it stays bedtime, nudging
// whoever's still up to go to sleep. Kept short (fits the 3-line message
// window without its start scrolling off before typing finishes) and in the
// same casual PT-BR voice as the rest of the AI-activity messages.
constexpr unsigned long BEDTIME_MESSAGE_INTERVAL_MS = 30UL * 60UL * 1000UL;
constexpr const char* BEDTIME_MESSAGES[] = {
    "Eai? Terminou? Vamos dormir...",
    "Ja passou da hora, hein! Bora descansar?",
    "Psiu... seus olhos ja tao pesados. Hora de dormir!",
    "To com sono so de te ver acordado ainda kkkk vai dormir",
    "Amanha voce agradece. Bora pra cama!",
    "22h ja foi! Desliga tudo e vem dormir comigo (eletronicamente)",
    "Serio, ja e tarde. Seu travesseiro ta com saudade",
    "Aviso final: vai dormir ou vai virar a noite de novo?",
    "Zzz... eu ja to com sono so de pensar. E voce?",
    "Bora, chefe. Amanha tem o dia inteiro pra terminar isso",
};
constexpr int BEDTIME_MESSAGE_COUNT = sizeof(BEDTIME_MESSAGES) / sizeof(BEDTIME_MESSAGES[0]);

// True from BEDTIME_START_HOUR up to (not including) BEDTIME_END_HOUR the
// next morning, based on the hour of the last TIME command Core received —
// same reasoning as Face.cpp's isNightHour: Core has no clock of its own.
// No TIME yet: default to "not bedtime" rather than guessing.
bool isBedtimeHour(const char* timeText) {
    if (timeText == nullptr || timeText[0] == '\0') {
        return false;
    }
    int hour = (int)strtol(timeText, nullptr, 10);
    return hour >= BEDTIME_START_HOUR || hour < BEDTIME_END_HOUR;
}

// Boot animation: eyes drop in from just above the frame and land with a
// little bounce (BOOT_FALL_DURATION_MS), then settle into place with a
// quick decaying side-to-side wobble (BOOT_WOBBLE_DURATION_MS) — "falling
// and reorganizing" rather than just popping into view. Only vertical travel
// during the fall; only horizontal during the wobble.
constexpr unsigned long BOOT_FALL_DURATION_MS = 700;
constexpr unsigned long BOOT_WOBBLE_DURATION_MS = 500;
constexpr unsigned long BOOT_ANIMATION_DURATION_MS = BOOT_FALL_DURATION_MS + BOOT_WOBBLE_DURATION_MS;
// EYE_Y is 24 (see Face.cpp) — this stays shy of pushing the eyes to a
// negative on-screen Y, which a couple of IDisplay implementations
// (esp. the ST7735's SPI address window) don't handle safely.
constexpr int BOOT_FALL_START_OFFSET_Y_PX = -20;
constexpr int BOOT_WOBBLE_AMPLITUDE_PX = 6;
constexpr int BOOT_WOBBLE_CYCLES = 3;

// Cubic "ease out back": approaches 1 but overshoots past it before settling
// back — reads as landing with a bit of weight/bounce instead of drifting
// to a stop. Plain polynomial, no trig/pow() needed (see smoothstep below
// for why that matters on this project).
float easeOutBack(float t) {
    constexpr float c1 = 1.70158f;
    constexpr float c3 = c1 + 1.0f;
    float tm1 = t - 1.0f;
    return 1.0f + c3 * tm1 * tm1 * tm1 + c1 * tm1 * tm1;
}

// Triangle wave over one cycle: 0 at phase 0, +1 at 0.25, 0 at 0.5, -1 at
// 0.75, back to 0 at 1. Hand-written instead of sinf() — this firmware also
// builds for AVR and for BrobotCore/native's minimal Arduino.h shim (see
// native/include/Arduino.h), neither of which can be assumed to have <math.h>
// wired up, so every easing curve in this file is plain arithmetic on purpose.
float triangleWave(float phase) {
    if (phase < 0.25f) return phase * 4.0f;
    if (phase < 0.75f) return 1.0f - (phase - 0.25f) * 4.0f;
    return -1.0f + (phase - 0.75f) * 4.0f;
}

Expression parseExpression(const char* name) {
    if (strcmp(name, "HAPPY") == 0) return Expression::HAPPY;
    if (strcmp(name, "SAD") == 0) return Expression::SAD;
    if (strcmp(name, "ANGRY") == 0) return Expression::ANGRY;
    if (strcmp(name, "SLEEPING") == 0) return Expression::SLEEPING;
    if (strcmp(name, "MUSIC") == 0) return Expression::MUSIC;
    if (strcmp(name, "WATCHING") == 0) return Expression::WATCHING;
    if (strcmp(name, "ERROR") == 0) return Expression::FAILED;
    if (strcmp(name, "READING") == 0) return Expression::READING;
    if (strcmp(name, "FINISHED") == 0) return Expression::FINISHED;
    if (strcmp(name, "THINKING") == 0) return Expression::THINKING;
    if (strcmp(name, "PLAYING") == 0) return Expression::PLAYING;
    if (strcmp(name, "SLEEPY") == 0) return Expression::SLEEPY;
    if (strcmp(name, "COFFEE") == 0) return Expression::COFFEE;
    return Expression::NEUTRAL;
}

WeatherCondition parseWeatherCondition(const char* name) {
    if (strcmp(name, "CLOUDY") == 0) return WeatherCondition::CLOUDY;
    if (strcmp(name, "RAIN") == 0) return WeatherCondition::RAIN;
    if (strcmp(name, "STORM") == 0) return WeatherCondition::STORM;
    if (strcmp(name, "SNOW") == 0) return WeatherCondition::SNOW;
    if (strcmp(name, "FOG") == 0) return WeatherCondition::FOG;
    return WeatherCondition::CLEAR;
}

// Eases the 0..1 travel fraction so motion accelerates in and decelerates
// out, instead of moving at a constant speed — reads as noticeably smoother
// with the same number of frames.
float smoothstep(float t) {
    return t * t * (3.0f - 2.0f * t);
}

} // namespace

void Personality::TypedMessage::set(const char* text, unsigned long now) {
    strncpy(full, text, MESSAGE_CAPACITY - 1);
    full[MESSAGE_CAPACITY - 1] = '\0';

    // A new message always replaces whatever was showing (or being typed)
    // and restarts the typewriter reveal from scratch. The expiry countdown
    // doesn't start until that reveal finishes — see updateTyping.
    visible[0] = '\0';
    typingStartedAt = now;
    fullyRevealed = (full[0] == '\0'); // empty text: nothing to type, clears immediately
    expiresAt = now;
}

void Personality::TypedMessage::updateTyping(unsigned long now, unsigned long persistDurationMs) {
    int fullLength = (int)strlen(full);
    if (fullLength == 0) {
        visible[0] = '\0';
        return;
    }

    if (fullyRevealed) {
        if (persistDurationMs != 0 && now >= expiresAt) {
            full[0] = '\0';
            visible[0] = '\0';
        }
        return; // already typed out in full; nothing left to animate
    }

    unsigned long elapsed = now - typingStartedAt;
    int revealed = (int)(elapsed / TYPING_CHAR_INTERVAL_MS);
    if (revealed >= fullLength) {
        revealed = fullLength;
        fullyRevealed = true;
        expiresAt = now + persistDurationMs;
    }

    memcpy(visible, full, revealed);
    visible[revealed] = '\0';
}

bool Personality::isBackgroundExpression(Expression e) {
    return e == Expression::MUSIC || e == Expression::WATCHING || e == Expression::PLAYING;
}

void Personality::begin(unsigned long now) {
    _lastInteractionAt = now;
    _bootStartedAt = now;
    _nextBlinkAt = now + random(BLINK_MIN_INTERVAL_MS, BLINK_MAX_INTERVAL_MS);
    _nextLookAt = now + random(LOOK_MIN_INTERVAL_MS, LOOK_MAX_INTERVAL_MS);
}

// FACE NEUTRAL and FACE IDLE both mean "nothing to show here anymore", but
// for different tiers — see the Tier comment in Personality.h. Pensamentos
// da IA sends NEUTRAL (clears whatever AI expression was showing, e.g. on
// SessionEnd) while Mídia/Jogos send IDLE (clears MUSIC/WATCHING/PLAYING
// when playback/the game stops) — Core has no other way to tell which PC
// app a plain "nothing" means "nothing" for, since both currently reuse the
// same wire connection. IDLE isn't a real Expression (it renders identically
// to NEUTRAL), so it's intercepted here before parseExpression rather than
// added to the enum.
void Personality::onFaceCommand(const char* name, unsigned long now) {
    _lastInteractionAt = now;

    if (strcmp(name, "IDLE") == 0) {
        _backgroundExpression = Expression::NEUTRAL;
        _lastCommandTier = Tier::BACKGROUND;
        return;
    }

    Expression e = parseExpression(name);
    if (isBackgroundExpression(e)) {
        // Doesn't touch the foreground expression/override at all — if AI
        // foreground is currently showing, this just gets stored and takes
        // over once foreground releases (see resolveExpression).
        _backgroundExpression = e;
        _lastCommandTier = Tier::BACKGROUND;
        return;
    }

    _expression = e;
    // NEUTRAL releases the foreground override immediately (now, not +4s)
    // so a stored background expression can take over right away instead of
    // sitting on a redundant "NEUTRAL" override window first.
    _expressionOverrideUntil = (e == Expression::NEUTRAL) ? now : now + FACE_OVERRIDE_DURATION_MS;
    _lastCommandTier = Tier::FOREGROUND;
}

void Personality::onMessageCommand(const char* text, unsigned long now) {
    _lastInteractionAt = now;
    // Logged once here regardless of which tier it's about to land in below
    // — covers AI hook messages, media "now playing", "Jogando X", weather
    // alerts, and Pausa's coffee reminders, all in one place, since they all
    // flow through this same command.
    pushLogLine(text);

    // A lone MSG (no FACE on the same "turn", e.g. a Notification hook
    // event) routes to whichever tier the last FACE command belonged to —
    // Notifications always follow a THINKING/READING from the same AI
    // session, so this lands on the foreground message as intended.
    if (_lastCommandTier == Tier::BACKGROUND) {
        _backgroundMessage.set(text, now);
        return;
    }

    _foregroundMessage.set(text, now);
    if (text[0] == '\0') {
        return; // clearing the message shouldn't manufacture new foreground hold time
    }

    // Keep foreground active for as long as this message needs to type in
    // and hold, even past whatever _expressionOverrideUntil a preceding (or
    // absent) FACE command set. This is what makes a lone Notification (no
    // FACE change at all — see MainWindow.xaml.cs's OnAiThoughtReceived)
    // still count as high-priority content that interrupts whatever
    // background (media/game) is currently showing instead of being
    // silently swallowed behind it, and keeps a longer PreToolUse/Stop
    // message from visually outliving its own face's override window.
    unsigned long holdMs = (unsigned long)strlen(text) * TYPING_CHAR_INTERVAL_MS + MESSAGE_DURATION_MS;
    unsigned long candidateUntil = now + holdMs;
    if (candidateUntil > _expressionOverrideUntil) {
        _expressionOverrideUntil = candidateUntil;
    }
}

// Neither of these touch _lastInteractionAt: they're passive background
// telemetry from whichever PC app is connected, not user interaction, so
// they must not keep resetting the idle/sleep timer — otherwise a Sender
// pushing TIME every minute would keep Brobot awake forever.
void Personality::onWeatherCommand(const char* args, unsigned long now) {
    (void)now;

    if (args[0] == '\0') { // "WEATHER" with nothing after it clears the badge, same convention as empty MSG/TIME
        _hasWeather = false;
        return;
    }

    _weatherTempC = (int)strtol(args, nullptr, 10);

    const char* space = strchr(args, ' ');
    _weatherCondition = parseWeatherCondition(space ? space + 1 : args);
    _hasWeather = true;
}

void Personality::onTimeCommand(const char* args, unsigned long now) {
    (void)now;
    strncpy(_timeText, args, TIME_TEXT_CAPACITY - 1);
    _timeText[TIME_TEXT_CAPACITY - 1] = '\0';
}

// Same "passive telemetry" reasoning as onWeatherCommand/onTimeCommand:
// never touches _lastInteractionAt, and just holds whatever theme was last
// sent until replaced.
void Personality::onThemeCommand(const char* name, unsigned long now) {
    (void)now;
    _theme = (strcmp(name, "MATRIX") == 0) ? Theme::MATRIX : Theme::CLASSIC;
}

// Appends to MATRIX's console log, dropping the oldest line once full — a
// plain shift-and-append rather than a wraparound ring buffer, since
// MATRIX_LOG_LINES is tiny (6) and this keeps currentState() able to just
// hand out _log[0..count) in chronological order with no index math.
// Empty text is a no-op: clearing a message shouldn't leave a blank log line.
void Personality::pushLogLine(const char* text) {
    if (text == nullptr || text[0] == '\0') {
        return;
    }

    if (_logCount == MATRIX_LOG_LINES) {
        for (int i = 1; i < MATRIX_LOG_LINES; i++) {
            strncpy(_log[i - 1], _log[i], MATRIX_LOG_LINE_CAPACITY);
        }
        _logCount--;
    }

    strncpy(_log[_logCount], text, MATRIX_LOG_LINE_CAPACITY - 1);
    _log[_logCount][MATRIX_LOG_LINE_CAPACITY - 1] = '\0';
    _logCount++;
}

void Personality::update(unsigned long now) {
    _currentNow = now;
    _renderExpression = resolveExpression(now);

    // Background messages (MUSIC/WATCHING/PLAYING "now playing" labels)
    // never auto-expire — they hold until replaced or the background is
    // cleared (FACE IDLE). Foreground messages expire after MESSAGE_DURATION_MS
    // once fully typed, except while THINKING is showing, which uses its
    // "Pensando..." text as a sticky status label the same way background
    // does.
    unsigned long foregroundPersistMs = (_expression == Expression::THINKING) ? 0 : MESSAGE_DURATION_MS;
    _foregroundMessage.updateTyping(now, foregroundPersistMs);
    _backgroundMessage.updateTyping(now, 0);

    // Bedtime reminder: fires the moment bedtime starts, then every
    // BEDTIME_MESSAGE_INTERVAL_MS after that for as long as it stays
    // bedtime — tracked independently of _renderExpression so the schedule
    // stays aligned even while something else (AI activity, media) is
    // occupying the screen instead of SLEEPY.
    bool bedtime = isBedtimeHour(_timeText);
    if (bedtime && !_wasBedtime) {
        _nextBedtimeMessageAt = now;
    }
    _wasBedtime = bedtime;
    if (bedtime && now >= _nextBedtimeMessageAt) {
        int index = random(0, BEDTIME_MESSAGE_COUNT);
        _bedtimeMessage.set(BEDTIME_MESSAGES[index], now);
        pushLogLine(BEDTIME_MESSAGES[index]);
        _nextBedtimeMessageAt = now + BEDTIME_MESSAGE_INTERVAL_MS;
    }

    _bedtimeMessage.updateTyping(now, MESSAGE_DURATION_MS);

    unsigned long bootElapsed = now - _bootStartedAt;
    if (bootElapsed < BOOT_ANIMATION_DURATION_MS) {
        // Eyes are still dropping/settling into place — hold blink off and
        // drive the look offsets from the boot animation instead of the
        // idle blink/look-around timers (same "something else owns the
        // offsets right now" idea as the branch below, just for startup
        // instead of an expression).
        _blinking = false;
        _blinkAmount = 0.0f;
        _looking = false;
        updateBootAnimation(bootElapsed);
    } else if (_renderExpression == Expression::SLEEPING || _renderExpression == Expression::MUSIC
        || _renderExpression == Expression::FAILED || _renderExpression == Expression::READING
        || _renderExpression == Expression::THINKING || _renderExpression == Expression::COFFEE) {
        // Asleep, lost in the music, dead (FAILED), absorbed in reading, lost
        // in thought, or on a coffee break: hold still on the idle
        // blink/look-around timers. MUSIC, READING, THINKING, and COFFEE
        // still move — Face::render drives their sway/glitch/steam itself
        // from nowMs, same idea as the sleeping "Z Z Z" animating off nowMs
        // instead of a Personality timer. COFFEE specifically needs this:
        // its eyes are pinned to a fixed spot near the left edge (see
        // Face::render), and look-around's offset would otherwise be able
        // to push them past the screen edge.
        _blinking = false;
        _blinkAmount = 0.0f;
        _looking = false;
        _lookOffsetX = 0;
        _lookOffsetY = 0;
    } else {
        updateBlink(now);
        updateLook(now);
    }
}

FaceState Personality::currentState() const {
    FaceState state;
    state.expression = _renderExpression;
    state.blinkAmount = _blinkAmount;
    state.lookOffsetX = _lookOffsetX;
    state.lookOffsetY = _lookOffsetY;
    // Whichever tier is actually being rendered owns the message shown
    // below the eyes — a foreground message that's still "queued" behind an
    // active background render (or vice versa) stays hidden until its own
    // tier is the one on screen.
    if (_renderExpression == Expression::SLEEPY) {
        state.message = _bedtimeMessage.visible;
    } else {
        state.message = isBackgroundExpression(_renderExpression) ? _backgroundMessage.visible : _foregroundMessage.visible;
    }
    state.nowMs = _currentNow;
    state.hasWeather = _hasWeather;
    state.weatherTempC = _weatherTempC;
    state.weatherCondition = _weatherCondition;
    state.timeText = _timeText;

    state.theme = _theme;
    state.logLineCount = _logCount;
    for (int i = 0; i < _logCount; i++) {
        state.logLines[i] = _log[i];
    }

    return state;
}

Expression Personality::resolveExpression(unsigned long now) const {
    // THINKING is the only remaining sticky foreground expression: unlike
    // the others it doesn't time out via _expressionOverrideUntil — it holds
    // until a new foreground FACE command (READING/FINISHED/NEUTRAL/...)
    // picks something else. MUSIC/WATCHING/PLAYING used to be sticky here
    // too, but now live entirely in _backgroundExpression instead (see the
    // Tier comment in Personality.h), which is what lets foreground
    // (AI messages/notifications) take over the screen without permanently
    // losing track of them.
    if (_expression == Expression::THINKING) {
        return _expression;
    }
    if (now < _expressionOverrideUntil) {
        return _expression;
    }
    // Foreground has nothing active right now: fall back to whatever
    // background (media/game) is stored before giving up to idle/SLEEPING —
    // this is what makes the paused media/game reappear once the AI message
    // that interrupted it goes away.
    if (_backgroundExpression != Expression::NEUTRAL) {
        return _backgroundExpression;
    }
    if (now - _lastInteractionAt > SLEEP_TIMEOUT_MS) {
        return Expression::SLEEPING;
    }
    // Bedtime hours (see isBedtimeHour) win over plain NEUTRAL but lose to
    // the 10-minute-idle SLEEPING check above — once actually asleep, the
    // drowsy nudge stops making sense.
    if (isBedtimeHour(_timeText)) {
        return Expression::SLEEPY;
    }
    return Expression::NEUTRAL;
}

void Personality::updateBlink(unsigned long now) {
    if (!_blinking) {
        if (now < _nextBlinkAt) {
            _blinkAmount = 0.0f;
            return;
        }
        _blinking = true;
        _blinkStartedAt = now;
    }

    // Heavy-lidded, obviously-slower blink while SLEEPY (see
    // BLINK_DURATION_SLEEPY_MS) — everything else about the blink cycle
    // (interval, easing) stays the same, only the close/open motion itself
    // drags out.
    unsigned long blinkDuration = (_renderExpression == Expression::SLEEPY) ? BLINK_DURATION_SLEEPY_MS : BLINK_DURATION_MS;

    unsigned long elapsed = now - _blinkStartedAt;
    if (elapsed >= blinkDuration) {
        _blinking = false;
        _blinkAmount = 0.0f;
        _nextBlinkAt = now + random(BLINK_MIN_INTERVAL_MS, BLINK_MAX_INTERVAL_MS);
        return;
    }

    float t = (float)elapsed / (float)blinkDuration;
    float triangular = (t < 0.5f) ? (t * 2.0f) : ((1.0f - t) * 2.0f);
    _blinkAmount = smoothstep(triangular);
}

void Personality::updateLook(unsigned long now) {
    if (!_looking) {
        if (now < _nextLookAt) {
            _lookOffsetX = 0;
            _lookOffsetY = 0;
            return;
        }
        _looking = true;
        _lookStartedAt = now;
        int dir = (_theme == Theme::MATRIX)
            ? LOOK_DIRECTIONS_NO_DOWN[random(0, LOOK_DIRECTIONS_NO_DOWN_COUNT)]
            : random(0, LOOK_DIRECTION_COUNT);
        _lookDirX = LOOK_DIRECTIONS[dir][0];
        _lookDirY = LOOK_DIRECTIONS[dir][1];
        _lookHoldDuration = random(LOOK_HOLD_MIN_MS, LOOK_HOLD_MAX_MS);
    }

    unsigned long elapsed = now - _lookStartedAt;
    unsigned long holdStart = LOOK_EASE_MS;
    unsigned long holdEnd = holdStart + _lookHoldDuration;
    unsigned long episodeEnd = holdEnd + LOOK_EASE_MS;

    float t;
    if (elapsed < holdStart) {
        t = smoothstep((float)elapsed / (float)LOOK_EASE_MS);
    } else if (elapsed < holdEnd) {
        t = 1.0f;
    } else if (elapsed < episodeEnd) {
        float outT = (float)(elapsed - holdEnd) / (float)LOOK_EASE_MS;
        t = smoothstep(1.0f - outT);
    } else {
        _looking = false;
        _lookOffsetX = 0;
        _lookOffsetY = 0;
        _nextLookAt = now + random(LOOK_MIN_INTERVAL_MS, LOOK_MAX_INTERVAL_MS);
        return;
    }

    _lookOffsetX = (int)((float)(LOOK_OFFSET_X_PX * _lookDirX) * t);
    _lookOffsetY = (int)((float)(LOOK_OFFSET_Y_PX * _lookDirY) * t);
}

// elapsed is time since Personality::begin(), not since some sub-phase
// start — the caller (update()) already checked it's under
// BOOT_ANIMATION_DURATION_MS before calling this.
void Personality::updateBootAnimation(unsigned long elapsed) {
    if (elapsed < BOOT_FALL_DURATION_MS) {
        float t = (float)elapsed / (float)BOOT_FALL_DURATION_MS;
        float eased = easeOutBack(t);
        // eased travels 0 -> ~1.1 (overshoot) -> 1; offset travels
        // START -> slightly past 0 -> 0, i.e. "drop past resting position,
        // bounce back up to it".
        _lookOffsetY = (int)((float)BOOT_FALL_START_OFFSET_Y_PX * (1.0f - eased));
        _lookOffsetX = 0;
        return;
    }

    unsigned long wobbleElapsed = elapsed - BOOT_FALL_DURATION_MS;
    unsigned long cycleMs = BOOT_WOBBLE_DURATION_MS / BOOT_WOBBLE_CYCLES;
    float phase = (float)(wobbleElapsed % cycleMs) / (float)cycleMs;
    float decay = 1.0f - (float)wobbleElapsed / (float)BOOT_WOBBLE_DURATION_MS;

    _lookOffsetY = 0;
    _lookOffsetX = (int)((float)BOOT_WOBBLE_AMPLITUDE_PX * decay * triangleWave(phase));
}

#pragma once

#include "Face.h"

// Gives Brobot its "alive" idle behavior: autonomous blinking, looking
// around, and falling asleep after a period of inactivity. Also tracks the
// currently commanded expression and message. Knows nothing about how any
// of this gets drawn — it only produces a FaceState for Face::render.
class Personality {
public:
    void begin(unsigned long now);

    void onFaceCommand(const char* name, unsigned long now);
    void onMessageCommand(const char* text, unsigned long now);
    void onWeatherCommand(const char* args, unsigned long now);
    void onTimeCommand(const char* args, unsigned long now);
    void onThemeCommand(const char* name, unsigned long now);

    void update(unsigned long now);
    FaceState currentState() const;

private:
    static constexpr size_t MESSAGE_CAPACITY = 255;

    // Two independent priority tiers, so an AI message/notification never
    // permanently clobbers a MUSIC/WATCHING/PLAYING that was already
    // showing: FOREGROUND (THINKING/READING/FINISHED/HAPPY/etc., driven by
    // Pensamentos da IA) always wins the render while it's active; once it
    // releases (times out, or an explicit FACE NEUTRAL/new foreground
    // command), rendering falls back to BACKGROUND (MUSIC/WATCHING/PLAYING,
    // driven by Mídia/Jogos) if one is set, before falling further back to
    // idle/NEUTRAL/SLEEPING. A background FACE command received while
    // foreground is active only updates the stored background state — it
    // doesn't interrupt what's currently showing.
    enum class Tier { FOREGROUND, BACKGROUND };

    // Holds a message's full/typed-so-far text plus its typewriter timing.
    // Foreground and background each get their own instance so an AI
    // message typing in never stomps the media/game "now playing" text
    // (and vice versa) — see the two members below.
    struct TypedMessage {
        char full[MESSAGE_CAPACITY] = {0};
        char visible[MESSAGE_CAPACITY] = {0};
        unsigned long typingStartedAt = 0;
        bool fullyRevealed = true;
        unsigned long expiresAt = 0;

        void set(const char* text, unsigned long now);
        // persistDurationMs == 0 means "never auto-expire" (background tier,
        // and foreground while THINKING is showing).
        void updateTyping(unsigned long now, unsigned long persistDurationMs);
    };

    Expression _expression = Expression::NEUTRAL;       // foreground
    Expression _backgroundExpression = Expression::NEUTRAL; // NEUTRAL = none set
    Expression _renderExpression = Expression::NEUTRAL;
    unsigned long _expressionOverrideUntil = 0;
    unsigned long _lastInteractionAt = 0;
    unsigned long _currentNow = 0;
    Tier _lastCommandTier = Tier::FOREGROUND; // which tier a lone MSG (no preceding FACE) routes to
    unsigned long _bootStartedAt = 0; // set by begin(); anchors the eyes-falling-into-place boot animation

    bool _blinking = false;
    unsigned long _blinkStartedAt = 0;
    unsigned long _nextBlinkAt = 0;
    float _blinkAmount = 0.0f;

    bool _looking = false;
    unsigned long _lookStartedAt = 0;
    unsigned long _nextLookAt = 0;
    unsigned long _lookHoldDuration = 0;
    int _lookDirX = 0;
    int _lookDirY = 0;
    int _lookOffsetX = 0;
    int _lookOffsetY = 0;

    TypedMessage _foregroundMessage;
    TypedMessage _backgroundMessage;

    // SLEEPY's own periodic "go to bed" nudge — not FOREGROUND (no PC app
    // commanded it) or BACKGROUND (not media/game), so it gets its own
    // TypedMessage rather than overloading either tier. Only ever shown
    // while _renderExpression == SLEEPY (see currentState()); scheduling is
    // driven entirely by _timeText (see isBedtimeHour in Personality.cpp),
    // since Core has no RTC of its own.
    TypedMessage _bedtimeMessage;
    unsigned long _nextBedtimeMessageAt = 0;
    bool _wasBedtime = false; // detects the moment bedtime starts, to fire the first message right away

    // Persistent overlays: unlike _message, these don't type in, expire, or
    // get pre-empted by anything — they just hold whatever was last sent
    // until replaced. See FaceState's comment on why Core needs to be told.
    bool _hasWeather = false;
    int _weatherTempC = 0;
    WeatherCondition _weatherCondition = WeatherCondition::CLEAR;
    static constexpr size_t TIME_TEXT_CAPACITY = 6; // "HH:MM\0"
    char _timeText[TIME_TEXT_CAPACITY] = {0};

    // MATRIX theme's own state — see Face.h's Theme/MATRIX_LOG_* comments.
    // The theme itself never times out or gets pre-empted, same "just holds
    // whatever was last sent" idea as _hasWeather/_timeText above.
    Theme _theme = Theme::CLASSIC;
    char _log[MATRIX_LOG_LINES][MATRIX_LOG_LINE_CAPACITY] = {{0}};
    int _logCount = 0; // number of filled slots, index 0 = oldest
    char _lastLoggedTimeText[TIME_TEXT_CAPACITY] = {0}; // last "HH:MM" a time+weather line was logged for, so it's once per minute, not once per second

    void updateBlink(unsigned long now);
    void updateLook(unsigned long now);
    void updateBootAnimation(unsigned long elapsed);
    Expression resolveExpression(unsigned long now) const;
    static bool isBackgroundExpression(Expression e);
    void pushLogLine(const char* text);
};

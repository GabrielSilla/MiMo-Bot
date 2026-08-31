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
    void onNotifyCommand(const char* args, unsigned long now);
    void onStatsCommand(const char* args, unsigned long now);

    void update(unsigned long now);
    FaceState currentState() const;

private:
    static constexpr size_t MESSAGE_CAPACITY = 255;

    // Four independent priority tiers, highest first, so that nothing ever
    // permanently clobbers something lower that was already showing — each
    // keeps its own expression and its own message, and rendering simply
    // falls to the highest tier that currently has something (see
    // resolveExpression):
    //
    //   NOTIFICATION  Pausa/Clima/bedtime — the things MiMo interrupts you
    //                 *for*. Takes the whole screen (see
    //                 FaceState::isNotification), outranks even AI, and
    //                 expires on its own after NOTIFICATION_DURATION_MS.
    //   FOREGROUND    AI activity (THINKING/READING/FINISHED/...), driven
    //                 by Atividade da IA.
    //   GAME          PLAYING, driven by Jogos.
    //   MEDIA         MUSIC/WATCHING, driven by Mídia.
    //
    // GAME and MEDIA used to be one shared BACKGROUND tier where whichever
    // arrived last won, so starting music while a game was open replaced
    // "Jogando X" with the track and never brought it back. They're
    // separate now, which is why FACE IDLE grew the IDLE_GAME/IDLE_MEDIA
    // variants (see onFaceCommand) — a single "clear the background" no
    // longer says which of the two it means.
    //
    // A lower-tier FACE command received while a higher tier is active only
    // updates that lower tier's stored state — it never interrupts what's
    // currently on screen.
    enum class Tier { NOTIFICATION, FOREGROUND, GAME, MEDIA };

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

    Expression _expression = Expression::NEUTRAL;       // foreground (AI)
    Expression _gameExpression = Expression::NEUTRAL;   // NEUTRAL = no game set
    Expression _mediaExpression = Expression::NEUTRAL;  // NEUTRAL = no media set
    Expression _renderExpression = Expression::NEUTRAL;
    // When _renderExpression last changed — handed to Face via
    // FaceState::expressionStartedMs, which see for why a stateless
    // renderer needs it.
    unsigned long _renderExpressionStartedAt = 0;
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
    TypedMessage _gameMessage;
    TypedMessage _mediaMessage;

    // The notification tier. Unlike every other tier this one is not
    // commanded piecemeal by FACE-then-MSG: it arrives whole, on a single
    // NOTIFY line (see onNotifyCommand), precisely because it is the
    // highest priority thing on the display and must never be caught
    // half-applied between two commands. Core raises its own for bedtime,
    // with no PC app involved at all.
    Expression _notificationExpression = Expression::NEUTRAL;
    TypedMessage _notificationMessage;
    unsigned long _notificationUntil = 0;   // 0 / past = no notification showing
    unsigned long _notificationStartedAt = 0;

    // SLEEPY's own periodic "go to bed" nudge. It used to have a
    // TypedMessage of its own, shown only while _renderExpression ==
    // SLEEPY; it now raises a normal NOTIFICATION instead, so the only
    // state left here is the schedule. Driven entirely by _timeText (see
    // isBedtimeHour in Personality.cpp), since Core has no RTC of its own.
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
    // When the last THEME command arrived — handed to Face via
    // FaceState::themeStartedMs, which see for why a stateless renderer
    // needs it. Only MI84 reads it (its boot sequence).
    unsigned long _themeChangedAt = 0;
    // MATRIX's console log is split in two, one per tier, rather than one
    // shared list: AI activity and media/game "now playing" lines were
    // interleaving into a single stream where neither was readable. They
    // work like two tabs — currentState() hands Face whichever one matches
    // what's currently being rendered, so AI messages take over the log
    // while they're on screen and media's own history comes back
    // underneath, intact, the moment they clear (exactly the same
    // foreground-then-fall-back-to-background rule resolveExpression
    // already applies to the face itself). With no media stored, the
    // background log simply never becomes the visible one.
    char _foregroundLog[MATRIX_LOG_LINES][MATRIX_LOG_LINE_CAPACITY] = {{0}};
    int _foregroundLogCount = 0; // number of filled slots, index 0 = oldest
    // Sized MATRIX_MEDIA_LOG_LINES (1), not MATRIX_LOG_LINES — see that
    // constant in Face.h for why the media tab holds no history.
    char _backgroundLog[MATRIX_MEDIA_LOG_LINES][MATRIX_LOG_LINE_CAPACITY] = {{0}};
    int _backgroundLogCount = 0;
    // The game tab. A game's "Jogando X" used to land in _backgroundLog
    // beside music and video, but it's the only one whose line gets machine
    // stats drawn beneath it, so it stands on its own. Same single-slot depth
    // as the media log, for the same reason: it's a current-state label, not
    // a history worth scrolling.
    char _gameLog[MATRIX_MEDIA_LOG_LINES][MATRIX_LOG_LINE_CAPACITY] = {{0}};
    int _gameLogCount = 0;

    // Machine load pushed in by STATS (see Face.h's FaceState comment for why
    // -1 rather than 0 means "unavailable"). Held exactly like _hasWeather
    // above: persistent, never expiring, replaced only by the next STATS.
    bool _hasStats = false;
    int _statsCpuLoad = -1;
    int _statsCpuTempC = -1;
    int _statsGpuLoad = -1;
    int _statsGpuTempC = -1;
    int _statsRamLoad = -1;

    void updateBlink(unsigned long now);
    void updateLook(unsigned long now);
    void updateBootAnimation(unsigned long elapsed);
    Expression resolveExpression(unsigned long now) const;
    bool notificationActive(unsigned long now) const;
    void raiseNotification(Expression e, const char* text, unsigned long now);
    static bool isGameExpression(Expression e);
    static bool isMediaExpression(Expression e);
    static bool isBackgroundExpression(Expression e);
    void pushLogLine(const char* text, LogTab tab);
};

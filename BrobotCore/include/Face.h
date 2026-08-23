#pragma once

#include "IDisplay.h"

// Facial expressions Brobot can show. Most are expressed only through eye
// size/position/motion — no new IDisplay primitives needed — but FAILED
// replaces the eyes outright (see Face.cpp's drawEyeX).
//
// Named FAILED, not ERROR: <wingdi.h>, pulled in transitively on the native
// Windows build, #defines ERROR as a macro, which would silently mangle an
// enumerator of that name. The wire protocol command is still "FACE ERROR"
// (see PROTOCOL.md) — Personality::parseExpression is what maps the string
// to this enumerator, so the two names don't have to match.
enum class Expression : uint8_t { NEUTRAL, HAPPY, SAD, ANGRY, SLEEPING, MUSIC, WATCHING, FAILED, READING, FINISHED, THINKING, PLAYING, SLEEPY, COFFEE };

// Weather pictograms shown in the persistent top-left badge (see WEATHER in
// PROTOCOL.md). Deliberately small — just enough categories to read clearly
// as a ~10px icon, not a full meteorological classification.
enum class WeatherCondition : uint8_t { CLEAR, CLOUDY, RAIN, STORM, SNOW, FOG };

// Overall rendering style, set via THEME (see PROTOCOL.md) — independent of
// Expression/message content, which stay exactly the same regardless of
// theme; only Face::render's *drawing* changes. CLASSIC is the normal
// teal-eyes-plus-speech-bubble look (the wire command is "THEME DEFAULT" —
// named CLASSIC here instead of DEFAULT only because <Arduino.h> #defines
// DEFAULT as a macro, same reasoning as Expression::FAILED not being named
// ERROR; Personality::onThemeCommand is what maps the two names together).
// MATRIX pins the eyes to the bottom of the frame, recolors everything
// green, and replaces the badges/message box with a scrolling console log
// (see FaceState::logLines below).
enum class Theme : uint8_t { CLASSIC, MATRIX };

// Fixed capacity for MATRIX's console log — shared between Personality
// (which owns the actual ring buffer) and FaceState/Face::render (which
// only ever sees read-only pointers into it), so the two can't drift apart.
constexpr int MATRIX_LOG_LINES = 6;
constexpr size_t MATRIX_LOG_LINE_CAPACITY = 34;

struct FaceState {
    Expression expression = Expression::NEUTRAL;
    float blinkAmount = 0.0f;      // 0 = fully open, 1 = fully closed
    int lookOffsetX = 0;           // horizontal shift for autonomous "look around"
    int lookOffsetY = 0;           // vertical shift for autonomous "look around"
    const char* message = nullptr; // nullptr/empty = no message shown
    unsigned long nowMs = 0;       // clock time, used to animate the sleeping "Z Z Z"

    // Persistent overlays (top corners) — independent of expression/message,
    // set via WEATHER/TIME and never auto-cleared like a FACE override. Set
    // by whichever PC app currently owns the connection (see
    // BrobotCore/native/README.md's multi-client note); Core has no RTC or
    // network of its own to source these itself.
    bool hasWeather = false;
    int weatherTempC = 0;
    WeatherCondition weatherCondition = WeatherCondition::CLEAR;
    const char* timeText = nullptr; // "HH:MM", nullptr/empty = no clock shown

    Theme theme = Theme::CLASSIC;
    // Only meaningful (and only drawn) while theme == MATRIX — oldest entry
    // at index 0, newest at logLineCount-1. Pointers into Personality's own
    // ring buffer, same non-owning convention as message/timeText above.
    const char* logLines[MATRIX_LOG_LINES] = {nullptr};
    int logLineCount = 0;
};

// Draws two eyes (and an optional message below them) onto an IDisplay.
// Purely a shape renderer — it has no timers, no state, no opinion about
// *when* to blink or what a message should say. That's Personality's job.
class Face {
public:
    static void render(IDisplay& display, const FaceState& state);
};

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
// WEATHER is the odd one out: it names no mood at all, and only ever
// reaches the notification tier (NOTIFY WEATHER <text>). It exists so a
// weather alert can pick its own artwork *without* one enumerator per
// condition — drawNotificationScreen reads FaceState::weatherCondition,
// which the WEATHER command already keeps current for the badge, so the
// remaining conditions cost one `case` each rather than a new command and
// a new enumerator each. Everywhere else it falls through to NEUTRAL's
// shape and no cue, which is exactly right for something that never
// renders as a face.
enum class Expression : uint8_t { NEUTRAL, HAPPY, SAD, ANGRY, SLEEPING, MUSIC, WATCHING, FAILED, READING, FINISHED, THINKING, PLAYING, SLEEPY, COFFEE, WEATHER, BYE };

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
// (see FaceState::logLines below). MI2MO2 is much smaller a change: same
// layout as CLASSIC (badges, corner icons, message bubble all untouched),
// just a solid red circle in place of the usual rounded-square eye, red
// message text instead of white, and — its own R2D2-flavored touch — each
// message character renders in AUREBESH (see IDisplay.h's TextFont) right
// after being revealed, then flips to the normal LATIN font a moment later
// (see Face.cpp's drawWrappedMessageMi2Mo2), reading as the message
// "translating" from alien script into Portuguese in real time. Works on
// both the Brobot Virtual Display/native build and the physical ST7735
// build — see IDisplay.h's TextFont comment for how each one covers only
// A-Z/0-9 and falls back to LATIN for anything else.
// MI84 is a 1984 amber-CRT terminal: black frame, everything drawn in one
// amber, a fixed text header/status/tab chrome at the top and MATRIX's own
// bottom-pinned eyes below it. It reuses MATRIX's log/tab machinery
// wholesale (same FaceState::logLines/logTab, same Personality side) and
// differs only in how that gets drawn — plus a boot sequence played once
// each time the theme is selected (see FaceState::themeStartedMs).
enum class Theme : uint8_t { CLASSIC, MATRIX, MI2MO2, MI84 };

// CLASSIC's own primary color, set via CLASSICCOLOR (see PROTOCOL.md) —
// every other theme has a fixed palette of its own and ignores this
// entirely (Personality still tracks and forwards it regardless of which
// theme is active, same "just holds whatever was last sent" treatment as
// Theme itself, so switching back to CLASSIC doesn't lose the choice).
// GREEN and AMBER deliberately resolve to MATRIX's and MI84's own ink
// colors rather than picking new values (see Face.cpp's classicColorRGB) —
// "MiMo Classic in green" reads as the same green MiMo already has
// elsewhere, not a third, slightly-different green. BLUE is the original
// default eye color from before this setting existed.
enum class ClassicColor : uint8_t { BLUE, GREEN, AMBER, RED, PINK, WHITE };

// Typewriter reveal speed — shared between Personality (which paces
// TypedMessage::updateTyping off it) and Face (which needs the same value
// to work out, purely from nowMs and a message's typingStartedAt, how long
// ago each individual character was revealed — see MI2MO2's translation
// effect above). Living here rather than duplicated in both .cpp files
// keeps the two from silently drifting apart.
constexpr unsigned long TYPING_CHAR_INTERVAL_MS = 40;

// Fixed capacity for MATRIX's console log — shared between Personality
// (which owns the actual ring buffer) and FaceState/Face::render (which
// only ever sees read-only pointers into it), so the two can't drift apart.
constexpr int MATRIX_LOG_LINES = 6;
// Sized so a single entry can word-wrap across a full 3 on-screen lines —
// same visible-line budget CLASSIC's own message box gives one message
// (MESSAGE_VISIBLE_LINES) — before Personality::pushLogLine truncates it,
// rather than the previous 34, which cut a raw entry off well short of
// even 2 wrapped lines at this display's width (see Face.cpp's
// drawMatrixLog for the actual per-line wrap budget this is sized against).
constexpr size_t MATRIX_LOG_LINE_CAPACITY = 74;
// The media tab keeps only what's playing right now, not a history: once a
// track or game ends its line is stale, and the tab exists to answer "what
// is on?" rather than "what was on?". The AI tab still scrolls
// MATRIX_LOG_LINES deep, where earlier lines are the context for the
// current one. Expressed as a capacity rather than a special case in
// Personality::pushLogLine, so the same shift-and-append handles both: at
// capacity 1 the shift loop simply does nothing and the single slot is
// overwritten.
constexpr int MATRIX_MEDIA_LOG_LINES = 1;

// MATRIX's log tabs. AI activity and media "now playing" lines were
// interleaving into one unreadable stream, which is why they were split; the
// header marks whichever is currently showing. MONITOR is the game tab: a game
// used to log into MEDIA alongside music and video, but it's the one tab whose
// entry gets machine stats drawn under it (see FaceState::hasStats), so it
// stands on its own rather than sharing with media that has no stats to show.
enum class LogTab : uint8_t { AI, MEDIA, MONITOR };

struct FaceState {
    Expression expression = Expression::NEUTRAL;
    float blinkAmount = 0.0f;      // 0 = fully open, 1 = fully closed
    int lookOffsetX = 0;           // horizontal shift for autonomous "look around"
    int lookOffsetY = 0;           // vertical shift for autonomous "look around"
    const char* message = nullptr; // nullptr/empty = no message shown
    // When the currently-shown message's typewriter reveal started — only
    // meaningful alongside `message` (see Personality::currentState, which
    // copies whichever TypedMessage's typingStartedAt matches `message`).
    // Combined with TYPING_CHAR_INTERVAL_MS above, this is what lets
    // MI2MO2's drawWrappedMessageMi2Mo2 work out each character's own
    // reveal age without Personality needing to track per-character state.
    unsigned long messageTypingStartedMs = 0;
    // When the currently-rendered expression became the rendered one (see
    // Personality::update). Face::render is stateless, so a *bounded*
    // animation — one that has to run a fixed number of times and then
    // stop, rather than loop off nowMs forever like the glitch/rain/steam
    // effects — needs this anchor to measure its own elapsed time from.
    // MI2MO2's three-flash ERROR is the first such animation.
    unsigned long expressionStartedMs = 0;
    // When the current theme was last selected (see
    // Personality::onThemeCommand). Same reason expressionStartedMs exists:
    // MI84's boot sequence is a *bounded* animation — it runs once and
    // stops, rather than looping off nowMs forever like the glitch/rain/
    // steam effects — so a stateless Face::render needs an anchor to
    // measure its own elapsed time from.
    unsigned long themeStartedMs = 0;

    // A notification is the one thing that takes the whole screen: while
    // this is set, Face::render draws nothing but the dedicated
    // notification layout for `expression` plus `message` (see
    // drawNotificationScreen) — no eyes, no badges, no log, no message box.
    // It's the top priority tier (see Personality::Tier), above even AI
    // activity, and clears itself after NOTIFICATION_DURATION_MS, at which
    // point rendering falls straight back to whatever was underneath with
    // nothing to restore by hand.
    bool isNotification = false;
    // When the current notification was raised — its screen's animations are
    // bounded (they play for the notification's own lifetime), so a
    // stateless Face::render needs the anchor, same as themeStartedMs and
    // expressionStartedMs above.
    unsigned long notificationStartedMs = 0;

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

    // Machine load, set via STATS by whichever PC app is connected (Core has
    // no way to know any of this itself). Persistent and independent of
    // expression/message, exactly like hasWeather/timeText above — but only
    // *drawn* while a game is being played, which is the one situation anyone
    // wants to read them in. -1 in any field means "no source could supply
    // this", and renders as "--" rather than as a made-up zero: a CPU
    // temperature of 0 C would be a lie, a dash is honest.
    bool hasStats = false;
    int statsCpuLoad = -1;
    int statsCpuTempC = -1;
    int statsGpuLoad = -1;
    int statsGpuTempC = -1;
    int statsRamLoad = -1;

    // Claude Code session telemetry, set via AISTATS by whichever PC app is
    // connected — same persistent, expression-independent shape as hasStats
    // above, and the same -1 convention for "no source could supply this",
    // drawn as "--" rather than a zero nobody should believe. Only drawn in
    // the themes that have a console log to put it in (MATRIX, MI84), where
    // it sits under the AI tab exactly as the machine stats sit under the
    // MONITOR tab's game name.
    bool hasAiStats = false;
    int aiContextPercent = -1;  // share of the context window currently loaded
    int aiCostCents = -1;       // session cost in USD cents; cents because the wire is integers only
    int aiRateFiveHour = -1;    // 5-hour rate limit consumed, 0..100
    int aiRateSevenDay = -1;    // 7-day rate limit consumed, 0..100
    const char* aiModelName = nullptr; // non-owning, same convention as message/timeText

    Theme theme = Theme::CLASSIC;
    // Only meaningful (and only drawn) while theme == CLASSIC — see
    // ClassicColor above. Carried here regardless of the active theme so a
    // color chosen while on CLASSIC is still remembered after switching
    // away and back.
    ClassicColor classicColor = ClassicColor::BLUE;
    // Only meaningful (and only drawn) while theme == MATRIX or MI84 —
    // the two log-based themes; see Theme above. Oldest entry
    // at index 0, newest at logLineCount-1. Pointers into Personality's own
    // ring buffer, same non-owning convention as message/timeText above.
    const char* logLines[MATRIX_LOG_LINES] = {nullptr};
    int logLineCount = 0;
    // Which of the log tabs the lines above came from (see Personality's
    // _foregroundLog/_backgroundLog/_gameLog). Face only uses it to mark the
    // active tab in the log's header and, for MONITOR, to draw the machine
    // stats under the game's name; the lines themselves are already the right
    // ones by the time they get here.
    LogTab logTab = LogTab::AI;
};

// Draws two eyes (and an optional message below them) onto an IDisplay.
// Purely a shape renderer — it has no timers, no state, no opinion about
// *when* to blink or what a message should say. That's Personality's job.
class Face {
public:
    static void render(IDisplay& display, const FaceState& state);
};

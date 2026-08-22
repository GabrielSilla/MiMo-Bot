#include "Face.h"

#include <math.h>
#include <stdio.h>
#include <string.h>

namespace {

struct ExpressionShape {
    float openFactor;   // 1.0 = fully open eye, smaller = more squinted/closed
    int gapDelta;        // added to the gap between eyes
    int verticalShift;   // added to the eyes' vertical position
};

ExpressionShape shapeFor(Expression expression) {
    switch (expression) {
        case Expression::HAPPY:  return {0.55f, 0, -2};
        case Expression::SAD:    return {0.45f, 0, 6};
        case Expression::ANGRY:  return {0.40f, -6, -2};
        case Expression::SLEEPY: return {0.20f, 0, 4};
        case Expression::MUSIC:  return {1.00f, 0, 0}; // eyes open — the dance bounce carries the expression
        case Expression::WATCHING: return {1.00f, 0, 0}; // eyes open and still — the play icon carries the expression
        case Expression::READING: return {1.00f, 0, 0}; // eyes open — the reading sweep carries the expression
        case Expression::PLAYING: return {1.00f, 0, 0}; // eyes open — the gamepad icon carries the expression
        case Expression::FINISHED: return {1.00f, 0, 0}; // eyes open — drawEyeCaret replaces the shape entirely
        case Expression::THINKING: return {1.00f, 0, 0}; // eyes open — drawEyeGlitch replaces the shape entirely
        case Expression::NEUTRAL:
        default:                 return {1.00f, 0, 0};
    }
}

constexpr uint8_t EYE_R = 0, EYE_G = 200, EYE_B = 190;
constexpr uint8_t MSG_R = 255, MSG_G = 255, MSG_B = 255;
constexpr uint8_t MSG_BOX_R = 40, MSG_BOX_G = 40, MSG_BOX_B = 40;
constexpr uint8_t BG_R = 0, BG_G = 0, BG_B = 0;

// Corner rounding: a small 2-row "staircase" cut (2px, then 1px) instead of
// one flat diagonal chamfer. It approximates a quarter-circle of radius ~4,
// which reads as a soft round corner rather than a cut-off corner — closer
// to the reference eyes, and much subtler than a single big chamfer.
constexpr int CORNER_ROWS = 2;
constexpr int CORNER_CUT_WIDTH[CORNER_ROWS] = {2, 1};
constexpr int CORNER_MIN_SIZE = CORNER_ROWS * 3;

// Tuned for a 160x128 landscape frame. Eyes stay this size whether or not a
// message is showing — the message area is bottom-anchored and grows
// upward independently (see drawWrappedMessage), so there's no need to
// shrink the eyes to make room for it.
constexpr int EYE_SIZE = 42;
constexpr int EYE_GAP = 16;
constexpr int EYE_Y = 24;

constexpr int MESSAGE_LINE_HEIGHT = 9;    // 7px glyph + 2px between lines
// The display area is a fixed 3-line window. Once the (still-typing) text
// wraps past that, older lines scroll off the top instead of the block
// growing forever — MESSAGE_COMPUTE_LINES just bounds how many lines we're
// willing to wrap the full revealed text into before taking the last few.
constexpr int MESSAGE_VISIBLE_LINES = 3;
constexpr int MESSAGE_COMPUTE_LINES = 16;
constexpr int MESSAGE_MAX_LINE_CHARS = 32; // cap for the local line buffer
// Must match the renderer's per-character advance (Font5x7 on the PC side)
// so lines break where the text will actually fit.
constexpr int CHAR_ADVANCE_PX = 6;

// The message sits inside a speech-bubble-style box: a fixed-size rounded
// rect (sized for the full 3-line window, not just however much text is
// currently showing) with the text inset inside it.
constexpr int MESSAGE_BOX_MARGIN_X = 4;
constexpr int MESSAGE_BOX_MARGIN_BOTTOM = 4;
constexpr int MESSAGE_BOX_PADDING_Y = 4;
constexpr int MESSAGE_BOX_HEIGHT = MESSAGE_VISIBLE_LINES * MESSAGE_LINE_HEIGHT + 2 * MESSAGE_BOX_PADDING_Y;
constexpr int MESSAGE_MARGIN_X = MESSAGE_BOX_MARGIN_X + 4; // text inset from the box's edge

constexpr int MIN_EYE_HEIGHT = 2;

// Fakes a filled rounded rect by cutting a small background-colored
// staircase from each corner. IDisplay only offers a rounded-rect *outline*,
// not a filled one, so this composes what's already there instead of adding
// a new primitive just for this.
void fillRoundedRect(IDisplay& display, int x, int y, int w, int h, uint8_t r, uint8_t g, uint8_t b) {
    display.fillRect(x, y, w, h, r, g, b);

    if (h < CORNER_MIN_SIZE || w < CORNER_MIN_SIZE) {
        return; // too thin to bother rounding
    }

    for (int row = 0; row < CORNER_ROWS; row++) {
        int cut = CORNER_CUT_WIDTH[row];
        display.fillRect(x, y + row, cut, 1, BG_R, BG_G, BG_B);                   // top-left
        display.fillRect(x + w - cut, y + row, cut, 1, BG_R, BG_G, BG_B);         // top-right
        display.fillRect(x, y + h - 1 - row, cut, 1, BG_R, BG_G, BG_B);           // bottom-left
        display.fillRect(x + w - cut, y + h - 1 - row, cut, 1, BG_R, BG_G, BG_B); // bottom-right
    }
}

// The speech-bubble box behind the message text. Fixed size — sized for the
// full 3-line window regardless of how much text is currently showing —
// rather than shrinking to fit, so it doesn't hop around as text types in.
void drawMessageBox(IDisplay& display) {
    int boxX = MESSAGE_BOX_MARGIN_X;
    int boxW = display.width() - 2 * MESSAGE_BOX_MARGIN_X;
    int boxY = display.height() - MESSAGE_BOX_MARGIN_BOTTOM - MESSAGE_BOX_HEIGHT;
    fillRoundedRect(display, boxX, boxY, boxW, MESSAGE_BOX_HEIGHT, MSG_BOX_R, MSG_BOX_G, MSG_BOX_B);
}

// Greedy word-wrap into a fixed-height 3-line window, bottom-anchored inside
// the message box: the last line always sits just above the box's bottom
// padding, the block grows upward one line at a time as text is typed, and
// once a 4th line would be needed the oldest visible line scrolls off instead.
void drawWrappedMessage(IDisplay& display, const char* message, uint8_t r, uint8_t g, uint8_t b) {
    int len = (int)strlen(message);
    if (len == 0) {
        return;
    }

    int maxCharsPerLine = (display.width() - 2 * MESSAGE_MARGIN_X) / CHAR_ADVANCE_PX;
    if (maxCharsPerLine > MESSAGE_MAX_LINE_CHARS) {
        maxCharsPerLine = MESSAGE_MAX_LINE_CHARS;
    }
    if (maxCharsPerLine < 1) {
        return;
    }

    int lineStart[MESSAGE_COMPUTE_LINES];
    int lineLength[MESSAGE_COMPUTE_LINES];
    int lineCount = 0;

    int pos = 0;
    while (pos < len && lineCount < MESSAGE_COMPUTE_LINES) {
        int remaining = len - pos;
        int take = (remaining <= maxCharsPerLine) ? remaining : maxCharsPerLine;

        if (take < remaining) {
            // Forced to break mid-window: back up to the last space in it
            // so we don't cut a word in half.
            int breakAt = -1;
            for (int k = take - 1; k >= 0; k--) {
                if (message[pos + k] == ' ') {
                    breakAt = k;
                    break;
                }
            }
            if (breakAt > 0) {
                take = breakAt;
            }
        }

        lineStart[lineCount] = pos;
        lineLength[lineCount] = take;
        lineCount++;

        pos += take;
        while (pos < len && message[pos] == ' ') {
            pos++;
        }
    }

    int visibleStart = (lineCount > MESSAGE_VISIBLE_LINES) ? (lineCount - MESSAGE_VISIBLE_LINES) : 0;
    int visibleCount = lineCount - visibleStart;
    int boxBottom = display.height() - MESSAGE_BOX_MARGIN_BOTTOM - MESSAGE_BOX_PADDING_Y;
    int startY = boxBottom - visibleCount * MESSAGE_LINE_HEIGHT;

    char lineBuffer[MESSAGE_MAX_LINE_CHARS + 1];
    for (int i = 0; i < visibleCount; i++) {
        int lineIndex = visibleStart + i;
        int n = lineLength[lineIndex];
        memcpy(lineBuffer, message + lineStart[lineIndex], n);
        lineBuffer[n] = '\0';
        display.drawText(lineBuffer, MESSAGE_MARGIN_X, startY + i * MESSAGE_LINE_HEIGHT, r, g, b);
    }
}

void drawEye(IDisplay& display, int x, int y, int w, int h) {
    fillRoundedRect(display, x, y, w, h, EYE_R, EYE_G, EYE_B);
}

// FAILED: the eye becomes an "X" instead of the usual rounded square —
// IDisplay has no line primitive, so each diagonal stroke is approximated
// as a row of small square blocks, the same "compose it from fillRect"
// approach the rounded corners already use. Step is half the block size —
// stepping by the full block (as a first pass did) leaves the two diagonals
// just missing each other for a row or two right where they cross in the
// middle, showing up as a thin gap straight through the center of the X.
// Halving the step means consecutive blocks in *each* diagonal overlap, so
// near the crossing the two diagonals always end up either exactly
// coincident or exactly touching — never gapped.
constexpr int EYE_X_BLOCK = 6;
constexpr int EYE_X_STEP = EYE_X_BLOCK / 2;

void drawEyeX(IDisplay& display, int x, int y, int size) {
    for (int i = 0; i + EYE_X_BLOCK <= size; i += EYE_X_STEP) {
        display.fillRect(x + i, y + i, EYE_X_BLOCK, EYE_X_BLOCK, EYE_R, EYE_G, EYE_B);                          // top-left to bottom-right
        display.fillRect(x + size - i - EYE_X_BLOCK, y + i, EYE_X_BLOCK, EYE_X_BLOCK, EYE_R, EYE_G, EYE_B);     // top-right to bottom-left
    }
}

// FINISHED: a "^" per eye (a shallow upward chevron/caret) instead of
// squinting the normal eye shape — a squint alone read as closed/sleepy
// eyes, not a happy "^^". Built the same way as the X above: two short
// diagonal strokes meeting at a peak, each a row of overlapping blocks.
// Shallower and narrower than the full eye box (a real "^" is a low, wide
// peak, not a tall mountain), and self-centers vertically within the eye
// box rather than going through the normal openFactor/eyeTop squint math.
constexpr int EYE_CARET_HEIGHT = 18;
constexpr int EYE_CARET_BLOCK = 6;
constexpr int EYE_CARET_STEP = EYE_CARET_BLOCK / 2;

void drawEyeCaret(IDisplay& display, int x, int y, int size) {
    int halfWidth = size / 2;
    int caretTop = y + (size - EYE_CARET_HEIGHT) / 2;

    for (int dx = 0; dx <= halfWidth; dx += EYE_CARET_STEP) {
        int dy = (dx * EYE_CARET_HEIGHT) / halfWidth;
        display.fillRect(x + halfWidth - dx - EYE_CARET_BLOCK / 2, caretTop + dy, EYE_CARET_BLOCK, EYE_CARET_BLOCK, EYE_R, EYE_G, EYE_B); // left stroke
        display.fillRect(x + halfWidth + dx - EYE_CARET_BLOCK / 2, caretTop + dy, EYE_CARET_BLOCK, EYE_CARET_BLOCK, EYE_R, EYE_G, EYE_B); // right stroke
    }
}

// THINKING: the eye keeps its normal rounded shape but is sliced into
// horizontal bands, each independently shifted left/right — a "signal
// interference" look, deliberately jarring instead of the eased smoothstep
// motion everything else uses. Face::render has no timers/state of its own
// (see Face.h), so the offsets are derived purely from nowMs and (band, eye)
// via a small hash rather than an evolving random() seed — same nowMs input,
// same picture, every time, which also means both eyes glitch independently
// of each other (their hashes differ) instead of moving in lockstep.
constexpr int GLITCH_BANDS = 5;
constexpr unsigned long GLITCH_INTERVAL_MS = 120; // how often the offsets re-roll
constexpr int GLITCH_MAX_OFFSET_PX = 3;

unsigned long glitchHash(unsigned long tick, int band, int eyeIndex) {
    unsigned long h = tick * 2654435761UL + (unsigned long)band * 40503UL + (unsigned long)eyeIndex * 374761393UL;
    h ^= (h >> 13);
    h *= 2246822519UL;
    h ^= (h >> 16);
    return h;
}

void drawEyeGlitch(IDisplay& display, int x, int y, int w, int h, unsigned long nowMs, int eyeIndex) {
    unsigned long tick = nowMs / GLITCH_INTERVAL_MS;
    int bandHeight = (h + GLITCH_BANDS - 1) / GLITCH_BANDS; // ceil, so only the last band is short
    int drawn = 0;

    for (int band = 0; band < GLITCH_BANDS && drawn < h; band++) {
        int bh = bandHeight;
        if (drawn + bh > h) {
            bh = h - drawn;
        }

        int offset = (int)(glitchHash(tick, band, eyeIndex) % (2 * GLITCH_MAX_OFFSET_PX + 1)) - GLITCH_MAX_OFFSET_PX;
        display.fillRect(x + offset, y + drawn, w, bh, EYE_R, EYE_G, EYE_B);
        drawn += bh;
    }
}

// Pushed down out of the fixed top strip the weather/clock badges live in
// (see drawWeatherBadge/drawClockBadge) — this corner's x-range (roughly
// 0-20px) sits to the left of both eyes regardless of expression, so
// there's no risk of colliding with them, only with the new top badges.
constexpr int CORNER_ICON_Y_SHIFT = 14;

// A small eighth note bobbing gently in the corner while the eyes dance.
// The eyes themselves sway together (not out of phase) along a left-up-right
// arc: s sweeps -1..1 left/right, and 1-s*s peaks at the center of that
// sweep and bottoms out at each extreme, so the path arcs upward through the
// middle every time regardless of which way it's currently swinging.
constexpr int DANCE_ARC_X_PX = 8;
constexpr int DANCE_ARC_Y_PX = 5;
constexpr float DANCE_ARC_PERIOD_MS = 250.0f;

void drawMusicNote(IDisplay& display, unsigned long nowMs) {
    float phase = (float)nowMs / 400.0f;
    int bob = (int)(sin(phase) * 2.0f);
    int baseX = 8;
    int baseY = 18 + CORNER_ICON_Y_SHIFT + bob;

    display.fillRect(baseX, baseY, 5, 4, EYE_R, EYE_G, EYE_B);          // notehead
    display.fillRect(baseX + 4, baseY - 14, 2, 16, EYE_R, EYE_G, EYE_B); // stem
    display.fillRect(baseX + 6, baseY - 14, 4, 3, EYE_R, EYE_G, EYE_B);  // flag
}

// A right-pointing play triangle in the same corner spot as the music note —
// same "staircase" scanline trick as the eye corners, just built out to a
// full triangle: each row's width traces a flat left edge and a point on the
// right, widest at the vertical middle.
constexpr int PLAY_ROWS = 9;
constexpr int PLAY_ROW_HEIGHT = 2;
constexpr int PLAY_ROW_WIDTH[PLAY_ROWS] = {1, 2, 4, 6, 8, 6, 4, 2, 1};

void drawPlayIcon(IDisplay& display, unsigned long nowMs) {
    float phase = (float)nowMs / 400.0f;
    int bob = (int)(sin(phase) * 2.0f);
    int baseX = 8;
    int baseY = 10 + CORNER_ICON_Y_SHIFT + bob;

    for (int row = 0; row < PLAY_ROWS; row++) {
        display.fillRect(baseX, baseY + row * PLAY_ROW_HEIGHT, PLAY_ROW_WIDTH[row], PLAY_ROW_HEIGHT, EYE_R, EYE_G, EYE_B);
    }
}

// A small open book bobbing in the corner while READING — two "pages" with
// a thin background-colored cut down the middle for the spine, the same
// cut-a-gap trick the rounded eye corners use.
void drawBookIcon(IDisplay& display, unsigned long nowMs) {
    float phase = (float)nowMs / 500.0f;
    int bob = (int)(sin(phase) * 1.5f);
    int baseX = 7;
    int baseY = 13 + CORNER_ICON_Y_SHIFT + bob;

    display.fillRect(baseX, baseY, 6, 8, EYE_R, EYE_G, EYE_B);      // left page
    display.fillRect(baseX + 7, baseY, 6, 8, EYE_R, EYE_G, EYE_B);  // right page
    display.fillRect(baseX + 6, baseY - 1, 1, 10, BG_R, BG_G, BG_B); // spine gap
}

// A small game controller bobbing in the corner while PLAYING — one solid
// body block with a d-pad cross and two face buttons cut out of it in
// background color, the same "cut a gap from a filled block" trick the eye
// corners and the book's spine gap use, instead of adding new shapes drawn
// on top.
void drawGamepadIcon(IDisplay& display, unsigned long nowMs) {
    float phase = (float)nowMs / 400.0f;
    int bob = (int)(sin(phase) * 2.0f);
    int baseX = 7;
    int baseY = 11 + CORNER_ICON_Y_SHIFT + bob;

    display.fillRect(baseX, baseY, 14, 8, EYE_R, EYE_G, EYE_B);       // body
    display.fillRect(baseX - 2, baseY + 4, 3, 3, EYE_R, EYE_G, EYE_B); // left grip
    display.fillRect(baseX + 13, baseY + 4, 3, 3, EYE_R, EYE_G, EYE_B); // right grip

    display.fillRect(baseX + 3, baseY + 2, 1, 4, BG_R, BG_G, BG_B); // d-pad, vertical cut
    display.fillRect(baseX + 1, baseY + 4, 5, 1, BG_R, BG_G, BG_B); // d-pad, horizontal cut

    display.fillRect(baseX + 9, baseY + 2, 2, 2, BG_R, BG_G, BG_B); // face button
    display.fillRect(baseX + 11, baseY + 4, 2, 2, BG_R, BG_G, BG_B); // face button
}

// Persistent top-corner badges (weather top-left, clock top-right) — unlike
// every icon above, these don't depend on state.expression at all, so they
// live in their own fixed strip (roughly y=0-13) rather than the corner
// spot the expression icons use (which starts at CORNER_ICON_Y_SHIFT to
// stay clear of this strip). See PROTOCOL.md's WEATHER/TIME commands.
constexpr int TOP_BADGE_MARGIN = 2;

// Small cloud silhouette shared by every condition except CLEAR (no cloud —
// just a sun/moon) and FOG (just haze lines, no cloud outline).
void drawWeatherCloud(IDisplay& display, int x, int y) {
    display.fillRect(x, y + 3, 10, 4, EYE_R, EYE_G, EYE_B);
    display.fillRect(x + 2, y + 1, 6, 3, EYE_R, EYE_G, EYE_B);
}

// CLEAR at night swaps the sun disc for a crescent: same filled disc, then a
// second disc cut out of it in background color, offset up-right so what's
// left reads as a crescent rather than a smaller sun.
void drawWeatherMoon(IDisplay& display, int x, int y) {
    fillRoundedRect(display, x + 1, y + 1, 8, 8, EYE_R, EYE_G, EYE_B);
    fillRoundedRect(display, x + 3, y - 1, 8, 8, BG_R, BG_G, BG_B);
    // The cut leaves a sharp 90-degree reflex corner where the crescent's
    // two arms meet, reading as a blocky "L" rather than a curve — filling
    // the notch right at that corner (not floating in the cut's middle)
    // rounds it into a proper crescent silhouette.
    display.fillRect(x + 3, y + 5, 2, 2, EYE_R, EYE_G, EYE_B);
}

// True from 18:00 up to (not including) 06:00, based on the hour of the last
// TIME command Core received — see FaceState's comment on why Core has to be
// told rather than knowing the time itself. No TIME yet: default to day/sun.
bool isNightHour(const char* timeText) {
    if (timeText == nullptr || timeText[0] == '\0') {
        return false;
    }
    int hour = (int)strtol(timeText, nullptr, 10);
    return hour >= 18 || hour < 6;
}

void drawWeatherIcon(IDisplay& display, int x, int y, WeatherCondition condition, bool isNight) {
    switch (condition) {
        case WeatherCondition::CLEAR:
            if (isNight) {
                drawWeatherMoon(display, x, y);
            } else {
                fillRoundedRect(display, x + 1, y + 1, 8, 8, EYE_R, EYE_G, EYE_B);
            }
            break;

        case WeatherCondition::CLOUDY:
            drawWeatherCloud(display, x, y + 1);
            break;

        case WeatherCondition::RAIN:
            drawWeatherCloud(display, x, y);
            display.fillRect(x + 1, y + 8, 1, 2, EYE_R, EYE_G, EYE_B);
            display.fillRect(x + 4, y + 8, 1, 2, EYE_R, EYE_G, EYE_B);
            display.fillRect(x + 7, y + 8, 1, 2, EYE_R, EYE_G, EYE_B);
            break;

        case WeatherCondition::STORM:
            drawWeatherCloud(display, x, y);
            display.fillRect(x + 3, y + 7, 2, 2, EYE_R, EYE_G, EYE_B);
            display.fillRect(x + 5, y + 9, 2, 2, EYE_R, EYE_G, EYE_B);
            break;

        case WeatherCondition::SNOW:
            drawWeatherCloud(display, x, y);
            display.fillRect(x + 1, y + 8, 2, 2, EYE_R, EYE_G, EYE_B);
            display.fillRect(x + 5, y + 9, 2, 2, EYE_R, EYE_G, EYE_B);
            display.fillRect(x + 8, y + 8, 2, 2, EYE_R, EYE_G, EYE_B);
            break;

        case WeatherCondition::FOG:
            display.fillRect(x, y + 1, 10, 2, EYE_R, EYE_G, EYE_B);
            display.fillRect(x + 1, y + 4, 8, 2, EYE_R, EYE_G, EYE_B);
            display.fillRect(x, y + 7, 10, 2, EYE_R, EYE_G, EYE_B);
            break;
    }
}

void drawWeatherBadge(IDisplay& display, int tempC, WeatherCondition condition, const char* timeText) {
    drawWeatherIcon(display, TOP_BADGE_MARGIN, TOP_BADGE_MARGIN, condition, isNightHour(timeText));

    char buf[8];
    snprintf(buf, sizeof(buf), "%dC", tempC);
    display.drawText(buf, TOP_BADGE_MARGIN + 13, TOP_BADGE_MARGIN + 2, EYE_R, EYE_G, EYE_B);
}

void drawClockBadge(IDisplay& display, const char* timeText) {
    int textWidth = (int)strlen(timeText) * CHAR_ADVANCE_PX;
    int x = display.width() - TOP_BADGE_MARGIN - textWidth;
    display.drawText(timeText, x, TOP_BADGE_MARGIN + 2, EYE_R, EYE_G, EYE_B);
}

// READING: eyes sweep left-to-right slowly (tracking across a line of
// text), then snap back to the start quickly (jumping to the next line) —
// deliberately asymmetric, unlike the MUSIC dance's even side-to-side sway.
// s still ends up in -1..1, same as the dance arc, just via a sawtooth
// instead of a sine.
constexpr int READING_SWEEP_X_PX = 12;
// Durations are independent (not a period split into a fraction) so the
// sweep can be tuned slower without also dragging the snap-back out.
constexpr float READING_LINE_DURATION_MS = 1600.0f; // slow left-to-right sweep across the "line"
constexpr float READING_SNAP_DURATION_MS = 180.0f;  // fast return to the start, like jumping to the next line
constexpr float READING_SWEEP_PERIOD_MS = READING_LINE_DURATION_MS + READING_SNAP_DURATION_MS;

float readingSweep(unsigned long nowMs) {
    float t = fmodf((float)nowMs, READING_SWEEP_PERIOD_MS);

    if (t < READING_LINE_DURATION_MS) {
        float lineT = t / READING_LINE_DURATION_MS;          // 0..1 across the slow sweep
        float eased = lineT * lineT * (3.0f - 2.0f * lineT); // smoothstep: eases into and out of the sweep
        return -1.0f + 2.0f * eased;                          // -1 (left) -> 1 (right)
    }

    float snapT = (t - READING_LINE_DURATION_MS) / READING_SNAP_DURATION_MS; // 0..1 across the fast snap
    return 1.0f - 2.0f * snapT;                                               // 1 (right) -> -1 (left), abruptly
}

// Three "Z"s drifting up and to the right from the right eye, each gently
// bobbing out of phase with the others, while asleep.
void drawSleepZzz(IDisplay& display, int rightEyeX, int eyeSize, int eyeTop, unsigned long nowMs) {
    constexpr int ZZZ_COUNT = 3;
    constexpr int ZZZ_STEP_X = 8;
    constexpr int ZZZ_STEP_Y = 8;

    for (int i = 0; i < ZZZ_COUNT; i++) {
        float phase = (float)nowMs / 300.0f + (float)i * 1.4f;
        int bob = (int)(sin(phase) * 2.0f);
        int zx = rightEyeX + eyeSize - 4 + i * ZZZ_STEP_X;
        int zy = eyeTop - 12 - i * ZZZ_STEP_Y + bob;
        display.drawText("Z", zx, zy, EYE_R, EYE_G, EYE_B);
    }
}

} // namespace

void Face::render(IDisplay& display, const FaceState& state) {
    bool hasMessage = state.message != nullptr && state.message[0] != '\0';

    int eyeSize = EYE_SIZE;
    int eyeGap = EYE_GAP;
    int eyeY = EYE_Y;

    ExpressionShape shape = shapeFor(state.expression);
    eyeGap += shape.gapDelta;
    eyeY += shape.verticalShift + state.lookOffsetY;

    int totalWidth = eyeSize * 2 + eyeGap;
    int leftX = (display.width() - totalWidth) / 2 + state.lookOffsetX;
    int rightX = leftX + eyeSize + eyeGap;

    float openFactor = shape.openFactor * (1.0f - state.blinkAmount);
    int eyeHeight = (int)(eyeSize * openFactor);
    if (eyeHeight < MIN_EYE_HEIGHT) {
        eyeHeight = MIN_EYE_HEIGHT;
    }
    int eyeTop = eyeY + (eyeSize - eyeHeight) / 2;

    // Dancing to the music: both eyes sway together through a left-up-right
    // arc instead of sitting still.
    if (state.expression == Expression::MUSIC) {
        float s = sin((float)state.nowMs / DANCE_ARC_PERIOD_MS);
        leftX += (int)(DANCE_ARC_X_PX * s);
        rightX += (int)(DANCE_ARC_X_PX * s);
        eyeTop -= (int)(DANCE_ARC_Y_PX * (1.0f - s * s));
    } else if (state.expression == Expression::READING) {
        float s = readingSweep(state.nowMs);
        leftX += (int)(READING_SWEEP_X_PX * s);
        rightX += (int)(READING_SWEEP_X_PX * s);
    }

    if (state.expression == Expression::FAILED) {
        drawEyeX(display, leftX, eyeTop, eyeSize);
        drawEyeX(display, rightX, eyeTop, eyeSize);
    } else if (state.expression == Expression::FINISHED) {
        drawEyeCaret(display, leftX, eyeTop, eyeSize);
        drawEyeCaret(display, rightX, eyeTop, eyeSize);
    } else if (state.expression == Expression::THINKING) {
        drawEyeGlitch(display, leftX, eyeTop, eyeSize, eyeHeight, state.nowMs, 0);
        drawEyeGlitch(display, rightX, eyeTop, eyeSize, eyeHeight, state.nowMs, 1);
    } else {
        drawEye(display, leftX, eyeTop, eyeSize, eyeHeight);
        drawEye(display, rightX, eyeTop, eyeSize, eyeHeight);
    }

    if (state.expression == Expression::SLEEPY) {
        drawSleepZzz(display, rightX, eyeSize, eyeTop, state.nowMs);
    } else if (state.expression == Expression::MUSIC) {
        drawMusicNote(display, state.nowMs);
    } else if (state.expression == Expression::WATCHING) {
        drawPlayIcon(display, state.nowMs);
    } else if (state.expression == Expression::READING) {
        drawBookIcon(display, state.nowMs);
    } else if (state.expression == Expression::PLAYING) {
        drawGamepadIcon(display, state.nowMs);
    }

    if (hasMessage) {
        drawMessageBox(display);
        drawWrappedMessage(display, state.message, MSG_R, MSG_G, MSG_B);
    }

    // Persistent overlays: drawn last (on top), always in the same two
    // corners regardless of expression or message — see the badge functions'
    // comment above for why they don't share the expression icons' spot.
    if (state.hasWeather) {
        drawWeatherBadge(display, state.weatherTempC, state.weatherCondition, state.timeText);
    }
    if (state.timeText != nullptr && state.timeText[0] != '\0') {
        drawClockBadge(display, state.timeText);
    }
}

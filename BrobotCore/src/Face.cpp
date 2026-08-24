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
        case Expression::SLEEPING: return {0.20f, 0, 4};
        case Expression::SLEEPY: return {0.75f, 0, 2}; // near-NEUTRAL, just a light squint — the slow blink (see Personality.cpp) carries most of the "getting sleepy" read
        case Expression::MUSIC:  return {1.00f, 0, 0}; // eyes open — the dance bounce carries the expression
        case Expression::WATCHING: return {1.00f, 0, 0}; // eyes open and still — the play icon carries the expression
        case Expression::READING: return {1.00f, 0, 0}; // eyes open — the reading sweep carries the expression
        case Expression::PLAYING: return {1.00f, 0, 0}; // eyes open — the gamepad icon carries the expression
        case Expression::COFFEE: return {1.00f, 0, 0}; // eyes open — Face::render shrinks/repositions them separately, the cup carries the rest
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

// How dim the whole frame reads while SLEEPING (see Face::render) — this
// board's ST7735 clone ties its backlight LED straight to 3.3V with no GPIO
// broken out to it, so there's no real PWM backlight to dim; scaling every
// drawn color down instead works identically on the physical panel, the WPF
// simulator, and the native build, with no hardware change needed.
constexpr float SLEEP_DIM_FACTOR = 0.35f;

// Forwards every draw call to another IDisplay with r/g/b scaled down by a
// fixed factor — a decorator, not a real display of its own. width/height/
// present pass straight through since they don't carry color.
class DimmingDisplay : public IDisplay {
public:
    DimmingDisplay(IDisplay& inner, float factor) : _inner(inner), _factor(factor) {}

    int width() const override { return _inner.width(); }
    int height() const override { return _inner.height(); }

    void clear(uint8_t r, uint8_t g, uint8_t b) override { _inner.clear(dim(r), dim(g), dim(b)); }
    void drawPixel(int x, int y, uint8_t r, uint8_t g, uint8_t b) override { _inner.drawPixel(x, y, dim(r), dim(g), dim(b)); }
    void drawRect(int x, int y, int w, int h, uint8_t r, uint8_t g, uint8_t b) override { _inner.drawRect(x, y, w, h, dim(r), dim(g), dim(b)); }
    void fillRect(int x, int y, int w, int h, uint8_t r, uint8_t g, uint8_t b) override { _inner.fillRect(x, y, w, h, dim(r), dim(g), dim(b)); }
    void drawRoundedRect(int x, int y, int w, int h, int radius, uint8_t r, uint8_t g, uint8_t b) override { _inner.drawRoundedRect(x, y, w, h, radius, dim(r), dim(g), dim(b)); }
    void drawText(const char* text, int x, int y, uint8_t r, uint8_t g, uint8_t b) override { _inner.drawText(text, x, y, dim(r), dim(g), dim(b)); }
    void present() override { _inner.present(); }

private:
    IDisplay& _inner;
    float _factor;

    uint8_t dim(uint8_t c) const { return (uint8_t)((float)c * _factor); }
};

constexpr uint8_t MATRIX_R = 40, MATRIX_G = 255, MATRIX_B = 90;

// Forwards every draw call to another IDisplay, replacing any non-black
// color with a fixed one — used by MATRIX (see Face::render) to reskin
// every shape green without touching the dozen functions below that draw
// them. Pure black (0,0,0) passes through unchanged rather than also
// becoming green: every "cut a gap" background-colored punch-out (rounded
// eye corners, the book's spine gap, the gamepad's buttons, ...) relies on
// that gap staying the actual background color, or the cutout effect
// disappears and the shape reads as solid instead of hollow.
class RecoloringDisplay : public IDisplay {
public:
    RecoloringDisplay(IDisplay& inner, uint8_t r, uint8_t g, uint8_t b) : _inner(inner), _r(r), _g(g), _b(b) {}

    int width() const override { return _inner.width(); }
    int height() const override { return _inner.height(); }

    void clear(uint8_t r, uint8_t g, uint8_t b) override { _inner.clear(r, g, b); } // background clear stays whatever it was
    void drawPixel(int x, int y, uint8_t r, uint8_t g, uint8_t b) override { _inner.drawPixel(x, y, ro(r,g,b), go(r,g,b), bo(r,g,b)); }
    void drawRect(int x, int y, int w, int h, uint8_t r, uint8_t g, uint8_t b) override { _inner.drawRect(x, y, w, h, ro(r,g,b), go(r,g,b), bo(r,g,b)); }
    void fillRect(int x, int y, int w, int h, uint8_t r, uint8_t g, uint8_t b) override { _inner.fillRect(x, y, w, h, ro(r,g,b), go(r,g,b), bo(r,g,b)); }
    void drawRoundedRect(int x, int y, int w, int h, int radius, uint8_t r, uint8_t g, uint8_t b) override { _inner.drawRoundedRect(x, y, w, h, radius, ro(r,g,b), go(r,g,b), bo(r,g,b)); }
    void drawText(const char* text, int x, int y, uint8_t r, uint8_t g, uint8_t b) override { _inner.drawText(text, x, y, ro(r,g,b), go(r,g,b), bo(r,g,b)); }
    void present() override { _inner.present(); }

private:
    IDisplay& _inner;
    uint8_t _r, _g, _b;

    bool isBackground(uint8_t r, uint8_t g, uint8_t b) const { return r == 0 && g == 0 && b == 0; }
    uint8_t ro(uint8_t r, uint8_t g, uint8_t b) const { return isBackground(r, g, b) ? 0 : _r; }
    uint8_t go(uint8_t r, uint8_t g, uint8_t b) const { return isBackground(r, g, b) ? 0 : _g; }
    uint8_t bo(uint8_t r, uint8_t g, uint8_t b) const { return isBackground(r, g, b) ? 0 : _b; }
};

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

// COFFEE overrides the normal centered eye geometry entirely: smaller-than-
// normal eyes, pinned to the left edge instead of centered, so the coffee
// cup (see drawCoffeeCup, COFFEE_CUP_X=116) has the right side of the frame
// to itself. Sized to leave a clear ~25px gap before the cup/saucer at
// COFFEE_EYES_X=10 — right eye now ends at 10+32+10+32=84, well short of
// the cup's saucer at 113 (COFFEE_CUP_X - 3).
constexpr int COFFEE_EYE_SIZE = 32;
constexpr int COFFEE_EYE_GAP = 10;
// EYE_Y (24) was tuned for the full 42px eyes; at the smaller COFFEE size
// that top-anchored position reads as sitting too high, especially next to
// the cup (COFFEE_CUP_Y=58). Shifted down so the eyes' vertical center
// roughly lines up with the cup's top edge instead of floating near the
// weather/clock badge strip.
constexpr int COFFEE_EYE_Y = 42;
constexpr int COFFEE_EYES_X = 10;

// MATRIX pins the (centered) eyes to the bottom of the frame instead of the
// usual upper-middle spot, freeing up the top of the screen for the console
// log (see below). Margin keeps them clear of the very bottom edge, same
// reasoning BOOT_FALL_START_OFFSET_Y_PX's comment in Personality.cpp gives
// for the top edge. Sized ~36% smaller than the normal EYE_SIZE/EYE_GAP (two
// successive ~20% reductions) — at full size, pinned to the bottom edge,
// they read as too large/heavy for this theme.
constexpr int MATRIX_EYE_SIZE = 27;
constexpr int MATRIX_EYE_GAP = 10;
constexpr int MATRIX_EYE_BOTTOM_MARGIN = 8;
// The weather/clock badges occupy a fixed strip at the very top of the frame
// (see TOP_BADGE_MARGIN's comment) and stay on in MATRIX same as CLASSIC —
// the log starts below that strip (matches CORNER_ICON_Y_SHIFT, the same
// clearance the expression icons use) instead of overlapping it.
constexpr int MATRIX_LOG_TOP_Y = 14;
// How much clearance the log's last visible line keeps above the (pinned)
// eyes — computed from MATRIX_EYE_SIZE/MATRIX_EYE_BOTTOM_MARGIN rather than
// the live, look-around-adjusted eyeY, so the log's own visible-line count
// doesn't flicker as the eyes glance around.
constexpr int MATRIX_LOG_BOTTOM_GAP = 2;
constexpr int MATRIX_LOG_X = 4;

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

// COFFEE: a mug (saucer + hollowed-out body + a handle stub, same
// "cut a gap from a filled block" trick as the book/gamepad above) sitting
// in the right side of the frame — mirrors where the eyes shrink and pin to
// the left (see Face::render) — with three steam wisps that rise and sway
// on a continuous loop, unlike every other icon's bounded bob/sway, since
// this one has to keep animating for as long as the message stays up
// instead of settling back to a resting position.
constexpr int COFFEE_CUP_X = 116;
constexpr int COFFEE_CUP_Y = 58;
constexpr int COFFEE_CUP_WIDTH = 20;
constexpr int COFFEE_CUP_HEIGHT = 16;
constexpr int COFFEE_STEAM_COUNT = 3;
constexpr unsigned long COFFEE_STEAM_CYCLE_MS = 1400; // one wisp's full rise-and-loop
constexpr int COFFEE_STEAM_RISE_PX = 18;

void drawCoffeeCup(IDisplay& display, unsigned long nowMs) {
    // Saucer, then the cup body, hollowed out on top so it reads as an open
    // mug rather than a solid block.
    display.fillRect(COFFEE_CUP_X - 3, COFFEE_CUP_Y + COFFEE_CUP_HEIGHT, COFFEE_CUP_WIDTH + 6, 2, EYE_R, EYE_G, EYE_B);
    display.fillRect(COFFEE_CUP_X, COFFEE_CUP_Y, COFFEE_CUP_WIDTH, COFFEE_CUP_HEIGHT, EYE_R, EYE_G, EYE_B);
    display.fillRect(COFFEE_CUP_X + 2, COFFEE_CUP_Y + 3, COFFEE_CUP_WIDTH - 4, COFFEE_CUP_HEIGHT - 4, BG_R, BG_G, BG_B);

    // Handle: a small hooked stub on the cup's right edge.
    display.fillRect(COFFEE_CUP_X + COFFEE_CUP_WIDTH, COFFEE_CUP_Y + 3, 4, 8, EYE_R, EYE_G, EYE_B);
    display.fillRect(COFFEE_CUP_X + COFFEE_CUP_WIDTH + 1, COFFEE_CUP_Y + 5, 3, 4, BG_R, BG_G, BG_B);

    // Three wisps, evenly staggered in phase so they never all rise/fade in
    // lockstep, each drifting up and gently side to side as it climbs.
    for (int i = 0; i < COFFEE_STEAM_COUNT; i++) {
        unsigned long phaseOffset = (unsigned long)i * (COFFEE_STEAM_CYCLE_MS / COFFEE_STEAM_COUNT);
        unsigned long t = (nowMs + phaseOffset) % COFFEE_STEAM_CYCLE_MS;
        float progress = (float)t / (float)COFFEE_STEAM_CYCLE_MS; // 0..1 rise, then loops
        int riseY = (int)(progress * (float)COFFEE_STEAM_RISE_PX);
        int sway = (int)(sin(progress * 6.2832f * 2.0f) * 2.0f);
        int sx = COFFEE_CUP_X + 5 + i * 6 + sway;
        int sy = COFFEE_CUP_Y - 3 - riseY;
        display.fillRect(sx, sy, 2, 2, EYE_R, EYE_G, EYE_B);
    }
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

// THINKING's rain: while MATRIX is active and Core is THINKING, the console
// log's region is swapped for a "digital rain" of characters rising through
// it instead — messages come back the instant THINKING clears, since
// resolveExpression falls through to whatever's next (see Personality.cpp)
// and Face::render just goes back to calling drawMatrixLog every frame from
// then on, same as any other foreground expression's icon disappearing.
// There's no animation-start/stop state to track here at all: like
// drawEyeGlitch, this is driven purely off nowMs.
constexpr int MATRIX_RAIN_COLUMN_SPACING_PX = 12; // two glyph-widths apart, so columns read as distinct streams instead of smearing into a solid block
constexpr unsigned long MATRIX_RAIN_TICK_MS = 90; // base speed a column's head advances one row at — deliberately not GLITCH_INTERVAL_MS, so the eyes and the rain don't move in lockstep
constexpr int MATRIX_RAIN_TRAIL_LEN = 3; // characters drawn per column below the head, standing in for a fading trail — this display can't dim per-glyph once RecoloringDisplay has flattened every color to the same green
const char MATRIX_RAIN_CHARS[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
constexpr int MATRIX_RAIN_CHAR_COUNT = sizeof(MATRIX_RAIN_CHARS) - 1; // - the string's own null terminator

// Same small hash shape as drawEyeGlitch's glitchHash, just taking two seeds
// instead of three — kept separate rather than reused so the rain's timing
// doesn't accidentally correlate with the eyes' glitch bands.
unsigned long matrixRainHash(unsigned long a, unsigned long b) {
    unsigned long h = a * 2654435761UL + b * 40503UL;
    h ^= (h >> 13);
    h *= 2246822519UL;
    h ^= (h >> 16);
    return h;
}

// topY/bottomY bound the same region drawMatrixLog would otherwise draw
// into (see Face::render's call site) — the rain replaces the log there,
// it doesn't add a new region of its own.
void drawMatrixRain(IDisplay& display, unsigned long nowMs, int topY, int bottomY) {
    int rowCount = (bottomY - topY) / MESSAGE_LINE_HEIGHT;
    if (rowCount < 1) {
        return;
    }

    // A trailing MATRIX_RAIN_TRAIL_LEN rows of run-in space below the
    // visible bottom (and, symmetrically, run-out space above the visible
    // top once headRow goes negative) is what makes each column's trail
    // enter from the bottom edge and exit off the top instead of just
    // popping in/out at the region's boundary.
    unsigned long cycle = (unsigned long)(rowCount + MATRIX_RAIN_TRAIL_LEN);

    char glyph[2] = {0, 0};
    int columnCount = display.width() / MATRIX_RAIN_COLUMN_SPACING_PX;
    for (int col = 0; col < columnCount; col++) {
        unsigned long colSeed = matrixRainHash((unsigned long)col, 0);
        // Each column ticks at its own speed and phase (both hashed from
        // the column index) so columns don't all rise in lockstep.
        unsigned long tickMs = MATRIX_RAIN_TICK_MS + (colSeed % MATRIX_RAIN_TICK_MS);
        unsigned long tick = nowMs / tickMs;
        unsigned long phase = (tick + matrixRainHash((unsigned long)col, 1)) % cycle;
        // phase counts up over time; headRow counts down from it, so the
        // head rises (decreasing row = higher on screen) as time passes,
        // then wraps back below the bottom edge once phase resets to 0.
        int headRow = (int)cycle - 1 - (int)phase;

        for (int t = 0; t < MATRIX_RAIN_TRAIL_LEN; t++) {
            int row = headRow - t;
            if (row < 0 || row >= rowCount) {
                continue;
            }
            unsigned long charHash = matrixRainHash((unsigned long)col, phase - (unsigned long)t);
            glyph[0] = MATRIX_RAIN_CHARS[charHash % MATRIX_RAIN_CHAR_COUNT];
            int x = col * MATRIX_RAIN_COLUMN_SPACING_PX;
            int y = topY + row * MESSAGE_LINE_HEIGHT;
            display.drawText(glyph, x, y, MSG_R, MSG_G, MSG_B);
        }
    }
}

// "> " prompt prefix — only the first physical line of each raw log entry
// gets one; a wrapped continuation of the same entry is still the same
// message, not a new prompt, so it draws flush left with no prefix.
constexpr int MATRIX_LOG_LINE_PREFIX_CHARS = 2;

// Upper bound on physical (wrapped) lines a single raw entry can produce —
// MATRIX_LOG_LINE_CAPACITY chars can never wrap into more than this many
// lines at any width this display could plausibly have (sized for its
// intended 3, plus headroom for a pathological run of short space-separated
// words forcing extra early breaks), so a fixed-size local array (no heap,
// no dynamic growth — same constraint every other buffer in this file works
// under) is safe.
constexpr int MATRIX_LOG_MAX_WRAPPED_PER_ENTRY = 5;
constexpr int MATRIX_LOG_MAX_WRAPPED_LINES = MATRIX_LOG_LINES * MATRIX_LOG_MAX_WRAPPED_PER_ENTRY;

// MATRIX's scrolling console log — each raw entry from Personality's _log
// (capped at MATRIX_LOG_LINE_CAPACITY chars there) is greedy word-wrapped
// here to fit the screen width, same approach drawWrappedMessage already
// uses for the normal message box: this used to draw one raw entry per
// line unwrapped, which ran text for any message longer than a screen-width
// off the right edge — invisible, not just cut off — instead of showing it.
// Wrapping is purely a rendering concern (Personality doesn't need to know
// about it), so it happens here rather than changing what gets stored.
// Physical lines are top-anchored and oldest-first same as before; once
// there isn't room for all of them above maxBottomY (the eyes' pinned
// position), the oldest ones scroll off — same "oldest line drops off"
// idea drawWrappedMessage uses for a single message, just applied to the
// whole log instead of one entry.
void drawMatrixLog(IDisplay& display, const FaceState& state, int maxBottomY) {
    int maxCharsPerLine = (display.width() - 2 * MATRIX_LOG_X) / CHAR_ADVANCE_PX;
    if (maxCharsPerLine > (int)MATRIX_LOG_LINE_CAPACITY) {
        maxCharsPerLine = (int)MATRIX_LOG_LINE_CAPACITY;
    }
    if (maxCharsPerLine <= MATRIX_LOG_LINE_PREFIX_CHARS) {
        return;
    }

    const char* wrapEntry[MATRIX_LOG_MAX_WRAPPED_LINES];
    int wrapStart[MATRIX_LOG_MAX_WRAPPED_LINES];
    int wrapLen[MATRIX_LOG_MAX_WRAPPED_LINES];
    bool wrapIsFirst[MATRIX_LOG_MAX_WRAPPED_LINES];
    int wrapCount = 0;

    for (int e = 0; e < state.logLineCount; e++) {
        const char* entry = state.logLines[e];
        int len = (int)strlen(entry);
        int pos = 0;
        bool isFirst = true;
        while (pos < len && wrapCount < MATRIX_LOG_MAX_WRAPPED_LINES) {
            int budget = isFirst ? (maxCharsPerLine - MATRIX_LOG_LINE_PREFIX_CHARS) : maxCharsPerLine;
            int remaining = len - pos;
            int take = (remaining <= budget) ? remaining : budget;

            if (take < remaining) {
                // Forced to break mid-window: back up to the last space in
                // it so a word doesn't get cut in half.
                int breakAt = -1;
                for (int k = take - 1; k >= 0; k--) {
                    if (entry[pos + k] == ' ') {
                        breakAt = k;
                        break;
                    }
                }
                if (breakAt > 0) {
                    take = breakAt;
                }
            }

            wrapEntry[wrapCount] = entry;
            wrapStart[wrapCount] = pos;
            wrapLen[wrapCount] = take;
            wrapIsFirst[wrapCount] = isFirst;
            wrapCount++;

            pos += take;
            while (pos < len && entry[pos] == ' ') {
                pos++;
            }
            isFirst = false;
        }
    }

    int maxVisibleLines = (maxBottomY - MATRIX_LOG_TOP_Y) / MESSAGE_LINE_HEIGHT;
    if (maxVisibleLines < 1) {
        maxVisibleLines = 1;
    }
    int visibleStart = (wrapCount > maxVisibleLines) ? (wrapCount - maxVisibleLines) : 0;

    char lineBuffer[MATRIX_LOG_LINE_CAPACITY + MATRIX_LOG_LINE_PREFIX_CHARS + 1];
    int y = MATRIX_LOG_TOP_Y;
    for (int i = visibleStart; i < wrapCount; i++) {
        int offset = 0;
        if (wrapIsFirst[i]) {
            lineBuffer[0] = '>';
            lineBuffer[1] = ' ';
            offset = MATRIX_LOG_LINE_PREFIX_CHARS;
        }
        memcpy(lineBuffer + offset, wrapEntry[i] + wrapStart[i], wrapLen[i]);
        lineBuffer[offset + wrapLen[i]] = '\0';
        display.drawText(lineBuffer, MATRIX_LOG_X, y, MSG_R, MSG_G, MSG_B);
        y += MESSAGE_LINE_HEIGHT;
    }
}

} // namespace

void Face::render(IDisplay& rawDisplay, const FaceState& state) {
    // Everything below draws through `display`, composed from whichever of
    // these two decorators actually apply — recolor first (innermost), so a
    // simultaneous SLEEPING dims the theme's own green rather than dimming
    // straight past it back to the normal teal.
    bool isMatrix = state.theme == Theme::MATRIX;
    RecoloringDisplay recolored(rawDisplay, MATRIX_R, MATRIX_G, MATRIX_B);
    IDisplay& themed = isMatrix ? static_cast<IDisplay&>(recolored) : rawDisplay;
    DimmingDisplay dimmed(themed, SLEEP_DIM_FACTOR);
    IDisplay& display = (state.expression == Expression::SLEEPING) ? static_cast<IDisplay&>(dimmed) : themed;

    bool hasMessage = state.message != nullptr && state.message[0] != '\0' && !isMatrix;

    bool isCoffee = state.expression == Expression::COFFEE;

    int eyeSize = isCoffee ? COFFEE_EYE_SIZE : (isMatrix ? MATRIX_EYE_SIZE : EYE_SIZE);
    int eyeGap = isCoffee ? COFFEE_EYE_GAP : (isMatrix ? MATRIX_EYE_GAP : EYE_GAP);
    int eyeY = isCoffee ? COFFEE_EYE_Y
        : (isMatrix ? (display.height() - eyeSize - MATRIX_EYE_BOTTOM_MARGIN) : EYE_Y);

    ExpressionShape shape = shapeFor(state.expression);
    eyeGap += shape.gapDelta;
    eyeY += shape.verticalShift + state.lookOffsetY;

    int totalWidth = eyeSize * 2 + eyeGap;
    int leftX = isCoffee ? COFFEE_EYES_X : (display.width() - totalWidth) / 2 + state.lookOffsetX;
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

    // MATRIX drops every corner icon EXCEPT the coffee cup — their info
    // already lives in the console log below (see
    // Personality::update/onMessageCommand), and the icons' usual Y range
    // (roughly 14-70px down) would otherwise collide with the log's own
    // top-anchored lines. COFFEE is the odd one out: unlike every other
    // expression, it already repositions the eyes themselves (see
    // isCoffee above, which takes precedence over Matrix's own bottom-pin
    // regardless of theme) specifically to clear room for this cup — hiding
    // just the cup while still applying that repositioning left the eyes
    // shrunk into the top-left corner for no visible reason, a real bug,
    // fixed once. The weather/clock badges are a separate fixed strip above
    // this range and stay on below, same as CLASSIC.
    if (!isMatrix || state.expression == Expression::COFFEE) {
        if (state.expression == Expression::SLEEPING) {
            drawSleepZzz(display, rightX, eyeSize, eyeTop, state.nowMs);
        } else if (state.expression == Expression::MUSIC) {
            drawMusicNote(display, state.nowMs);
        } else if (state.expression == Expression::WATCHING) {
            drawPlayIcon(display, state.nowMs);
        } else if (state.expression == Expression::READING) {
            drawBookIcon(display, state.nowMs);
        } else if (state.expression == Expression::PLAYING) {
            drawGamepadIcon(display, state.nowMs);
        } else if (state.expression == Expression::COFFEE) {
            drawCoffeeCup(display, state.nowMs);
        }
    }

    if (hasMessage) {
        drawMessageBox(display);
        drawWrappedMessage(display, state.message, MSG_R, MSG_G, MSG_B);
    }

    // Persistent overlays: drawn last (on top), always in the same two
    // corners regardless of expression, message, or theme — see the badge
    // functions' comment above for why they don't share the expression
    // icons' spot. Unlike those icons, MATRIX doesn't suppress these: Hora/
    // Clima stay fixed at the top exactly as in CLASSIC.
    if (state.hasWeather) {
        drawWeatherBadge(display, state.weatherTempC, state.weatherCondition, state.timeText);
    }
    if (state.timeText != nullptr && state.timeText[0] != '\0') {
        drawClockBadge(display, state.timeText);
    }

    // COFFEE's own eyes+cup layout (see isCoffee above) sits well inside the
    // log's usual region (roughly x:4-156, y:14-86) rather than tucked below
    // it like every other expression, so — same as THINKING swapping the
    // log for the rain — the log is skipped entirely here instead of
    // drawing on top of (or getting drawn over by) the cup; it comes back
    // on its own the instant COFFEE clears, no extra state needed, exactly
    // like THINKING's rain.
    if (isMatrix && state.expression != Expression::COFFEE) {
        int logBottomY = display.height() - MATRIX_EYE_SIZE - MATRIX_EYE_BOTTOM_MARGIN - MATRIX_LOG_BOTTOM_GAP;
        if (state.expression == Expression::THINKING) {
            drawMatrixRain(display, state.nowMs, MATRIX_LOG_TOP_Y, logBottomY);
        } else {
            drawMatrixLog(display, state, logBottomY);
        }
    }
}

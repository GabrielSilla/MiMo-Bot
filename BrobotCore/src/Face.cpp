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
    void drawText(const char* text, int x, int y, uint8_t r, uint8_t g, uint8_t b, TextFont font = TextFont::LATIN) override { _inner.drawText(text, x, y, dim(r), dim(g), dim(b), font); }
    void present() override { _inner.present(); }

private:
    IDisplay& _inner;
    float _factor;

    uint8_t dim(uint8_t c) const { return (uint8_t)((float)c * _factor); }
};

constexpr uint8_t MATRIX_R = 40, MATRIX_G = 255, MATRIX_B = 90;

// MI2MO2 renders the whole frame as a close-up of R2D2's dome plate rather
// than a face on black: an off-white plate covering the screen, navy inset
// panels, the big black photoreceptor lens, and — off to its right — the
// pink logic display and a silver vent, laid out after a reference photo of
// the real dome. This is why it needs a palette of its own rather than
// CLASSIC's single EYE_R/G/B: nothing here is "the eye color".
constexpr uint8_t MI2MO2_PLATE_R = 226, MI2MO2_PLATE_G = 227, MI2MO2_PLATE_B = 231;
constexpr uint8_t MI2MO2_SEAM_R = 198, MI2MO2_SEAM_G = 200, MI2MO2_SEAM_B = 206;
constexpr uint8_t MI2MO2_NAVY_R = 26, MI2MO2_NAVY_G = 42, MI2MO2_NAVY_B = 96;
constexpr uint8_t MI2MO2_LENS_R = 12, MI2MO2_LENS_G = 12, MI2MO2_LENS_B = 16;
constexpr uint8_t MI2MO2_GLINT_R = 245, MI2MO2_GLINT_G = 245, MI2MO2_GLINT_B = 250;
constexpr uint8_t MI2MO2_SILVER_R = 150, MI2MO2_SILVER_G = 152, MI2MO2_SILVER_B = 158;
constexpr uint8_t MI2MO2_SLAT_R = 74, MI2MO2_SLAT_G = 78, MI2MO2_SLAT_B = 86;

// The logic display — the small round lamp right of the lens. In MI2MO2
// this, not the eye, is what carries every expression: the real R2 emotes
// by flashing its logic panels, not by moving its (fixed, black) eye, and
// following that turned out to look far more like the character than a
// glowing red cyclops eye did. The ordinary blink lives here too — the
// lamp switches off and back on, since the lens itself is a fixed black
// disc with no light to close. FINISHED/FAILED swap the color (see below);
// THINKING/SLEEPING drive brightness instead.
constexpr uint8_t MI2MO2_LOGIC_R = 235, MI2MO2_LOGIC_G = 20, MI2MO2_LOGIC_B = 20;
// FINISHED turns the lamp green — the one expression that reads instantly
// without needing the flash pattern FAILED uses to distinguish itself from
// the (also red) resting lamp.
constexpr uint8_t MI2MO2_DONE_R = 40, MI2MO2_DONE_G = 215, MI2MO2_DONE_B = 75;
// Message text while still in its Aurebesh phase (see
// drawWrappedMessageMi2Mo2): red, turning white as each character resolves
// into Latin. Lifted a little off the lamp's own MI2MO2_LOGIC red — at 5x7
// against the dark message box, the lamp's deeper red loses too much of
// the thin strokes.
constexpr uint8_t MI2MO2_MSG_ALIEN_R = 240, MI2MO2_MSG_ALIEN_G = 55, MI2MO2_MSG_ALIEN_B = 50;

// FAILED flashes a more vivid red than the resting lamp, three times, then
// holds lit (see mi2Mo2ErrorDim).
constexpr uint8_t MI2MO2_ERROR_R = 255, MI2MO2_ERROR_G = 40, MI2MO2_ERROR_B = 35;

// Weather/clock badges: navy, since MI2MO2's plate is light — CLASSIC's
// teal (and the red an earlier version of this theme used) both wash out
// against it.
constexpr uint8_t MI2MO2_BADGE_R = 26, MI2MO2_BADGE_G = 42, MI2MO2_BADGE_B = 96;

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
    void drawText(const char* text, int x, int y, uint8_t r, uint8_t g, uint8_t b, TextFont font = TextFont::LATIN) override { _inner.drawText(text, x, y, ro(r,g,b), go(r,g,b), bo(r,g,b), font); }
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

// MI2MO2's dome-plate layout, laid out after a reference photo of R2D2's
// dome (see the palette above for why this theme is a plate rather than a
// face). Everything lives below MI2MO2_CONTENT_TOP_Y so the weather/clock
// badges keep their usual strip at the very top of the frame, and above
// the message box's own top edge (y=89), so neither ever overlaps.
constexpr int MI2MO2_CONTENT_TOP_Y = 14;

// Left column: a stack of navy inset panels, the plainest part of the
// reference crop and what stops the frame reading as one big empty plate.
constexpr int MI2MO2_LEFT_PANEL_X = 4;
constexpr int MI2MO2_LEFT_PANEL_W = 13;
constexpr int MI2MO2_LEFT_SMALL_X = 21;
constexpr int MI2MO2_LEFT_SMALL_W = 10;

// The big navy panel the photoreceptor lens is set into.
constexpr int MI2MO2_EYE_PANEL_X = 36;
constexpr int MI2MO2_EYE_PANEL_Y = MI2MO2_CONTENT_TOP_Y;
constexpr int MI2MO2_EYE_PANEL_W = 64;
constexpr int MI2MO2_EYE_PANEL_H = 72;

// The lens itself: a black disc centered in that panel. MI2MO2_EYE_SIZE is
// its full diameter — blinking squashes this vertically (see Face::render),
// revealing the navy panel behind, since a black lens has no light of its
// own to switch off the way the old red eye did.
constexpr int MI2MO2_EYE_SIZE = 54;
constexpr int MI2MO2_EYE_CX = MI2MO2_EYE_PANEL_X + MI2MO2_EYE_PANEL_W / 2;
constexpr int MI2MO2_EYE_CY = MI2MO2_EYE_PANEL_Y + MI2MO2_EYE_PANEL_H / 2;

// The white glint on the lens. It sits up and left of center at rest and
// slides with the look-around offset — a reflection travelling across the
// glass is what sells "the lens just turned", since the lens is otherwise
// featureless black.
constexpr int MI2MO2_GLINT_SIZE = 4;
constexpr int MI2MO2_GLINT_REST_DX = -12;
constexpr int MI2MO2_GLINT_REST_DY = -16;
constexpr float MI2MO2_GLINT_SHIFT_SCALE = 0.45f;

// The logic display (expression lamp) and the silver vent, side by side to
// the right of the lens panel.
constexpr int MI2MO2_LOGIC_CX = 118;
constexpr int MI2MO2_LOGIC_CY = 38;
constexpr int MI2MO2_LOGIC_RADIUS = 10;
constexpr int MI2MO2_VENT_CX = 140;
constexpr int MI2MO2_VENT_CY = 40;
constexpr int MI2MO2_VENT_R = 11;

// Navy strips filling the plate below the lamp/vent pair.
constexpr int MI2MO2_STRIP_X = 106;

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

// MATRIX shows the media/activity icons beside the (bottom-pinned) eyes
// rather than in the top-left corner the other themes use — that corner is
// the console log's here. The eyes are centered and 27px wide with a 10px
// gap, so their right edge sits at x=112; this leaves a clear gap after it,
// and centers the icons on the eyes' own vertical middle.
constexpr int MATRIX_ICON_CENTER_X = 132;
constexpr int MATRIX_ICON_CENTER_Y = 106;

// Each icon draws a different extent around the origin it's handed — the
// music note hangs its stem *above* the origin, the play triangle builds
// entirely *below* it, and the book/gamepad straddle it. Handing them all
// one shared origin therefore does NOT line them up: it left the play
// triangle sitting ~6px lower than the others. So each icon's own drawn
// box is described here (offset of its top edge from the origin, and its
// height), and its origin is derived so that box lands centered on
// MATRIX_ICON_CENTER_X/Y. Only one icon is ever on screen at a time, so
// what this really buys is each of them being centered on the same spot
// relative to the eyes, rather than agreeing with each other.
constexpr int MATRIX_ICON_MUSIC_TOP = -14, MATRIX_ICON_MUSIC_H = 18;
constexpr int MATRIX_ICON_PLAY_TOP = 0,    MATRIX_ICON_PLAY_H = 18;
constexpr int MATRIX_ICON_BOOK_TOP = -1,   MATRIX_ICON_BOOK_H = 10;
constexpr int MATRIX_ICON_GAMEPAD_TOP = 0, MATRIX_ICON_GAMEPAD_H = 8;

constexpr int MATRIX_ICON_MUSIC_LEFT = 0,  MATRIX_ICON_MUSIC_W = 10;
constexpr int MATRIX_ICON_PLAY_LEFT = 0,   MATRIX_ICON_PLAY_W = 8;
constexpr int MATRIX_ICON_BOOK_LEFT = 0,   MATRIX_ICON_BOOK_W = 13;
constexpr int MATRIX_ICON_GAMEPAD_LEFT = -2, MATRIX_ICON_GAMEPAD_W = 18;

// origin = center - (where the box's own middle sits relative to origin)
constexpr int matrixIconBaseY(int topOffset, int height) {
    return MATRIX_ICON_CENTER_Y - (topOffset + height / 2);
}
constexpr int matrixIconBaseX(int leftOffset, int width) {
    return MATRIX_ICON_CENTER_X - (leftOffset + width / 2);
}

// Instead of the corner bob, MATRIX's icons hold still and pulse in
// brightness — a slow breathing status lamp, matching this theme's
// terminal-readout feel rather than the friendlier bouncing icon.
constexpr unsigned long MATRIX_ICON_PULSE_PERIOD_MS = 2200;
constexpr float MATRIX_ICON_PULSE_MIN = 0.30f;

float matrixIconPulse(unsigned long nowMs) {
    float phase = (float)(nowMs % MATRIX_ICON_PULSE_PERIOD_MS) / (float)MATRIX_ICON_PULSE_PERIOD_MS;
    float wave = 0.5f + 0.5f * sin(phase * 6.2832f); // 0..1
    return MATRIX_ICON_PULSE_MIN + (1.0f - MATRIX_ICON_PULSE_MIN) * wave;
}

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

// MI2MO2's "translation" effect: how long a character stays drawn in
// AUREBESH after being revealed by the normal typewriter (see
// TYPING_CHAR_INTERVAL_MS in Face.h) before flipping to LATIN. ~16 char
// intervals, so a handful of trailing characters are always mid-"decode"
// right behind the typing cursor rather than the whole message flashing
// over to Latin in lockstep.
constexpr unsigned long AUREBESH_HOLD_MS = 650;

// The message sits inside a speech-bubble-style box: a fixed-size rounded
// rect (sized for the full 3-line window, not just however much text is
// currently showing) with the text inset inside it.
constexpr int MESSAGE_BOX_MARGIN_X = 4;
constexpr int MESSAGE_BOX_MARGIN_BOTTOM = 4;
constexpr int MESSAGE_BOX_PADDING_Y = 4;
constexpr int MESSAGE_BOX_HEIGHT = MESSAGE_VISIBLE_LINES * MESSAGE_LINE_HEIGHT + 2 * MESSAGE_BOX_PADDING_Y;
constexpr int MESSAGE_MARGIN_X = MESSAGE_BOX_MARGIN_X + 4; // text inset from the box's edge

// Game Mode grows the message box so each reading gets a line of its own,
// the way MATRIX's monitor tab already lists them, and so a game's name has
// room to wrap instead of being cut off mid-word.
//
// The two themes get different heights for a physical reason, not a stylistic
// one. CLASSIC is free to take the space because its eyes move out of the way
// (see GAME_EYE_* below). MI2MO2 can't: its lens is a fixed disc ending at
// y=77 (MI2MO2_EYE_CY + MI2MO2_EYE_SIZE/2), and a five-line box would start
// at y=71 and cover the bottom of R2's eye. Four lines start at 80 and clear
// it — that's the most this theme can grow without redrawing the plate.
constexpr int STATS_BOX_LINES_CLASSIC = 5;
constexpr int STATS_BOX_LINES_MI2MO2 = 4;
// The three stat rows always sit at the bottom of whichever box, so the
// numbers stay at fixed positions and the name gets whatever is left above.
constexpr int STATS_ROWS = 3;

// CLASSIC's Game Mode eyes: smaller, and pinned near the top instead of the
// usual EYE_Y, which is what frees the lower two thirds of the frame for the
// taller box. Below the weather/clock strip (see TOP_BADGE_MARGIN) so they
// never collide with it — the same reason CORNER_ICON_Y_SHIFT exists.
// Only CLASSIC does this: MATRIX has its own bottom-pinned layout and MI2MO2
// deliberately keeps its plate untouched.
constexpr int GAME_EYE_SIZE = 26;
constexpr int GAME_EYE_GAP = 12;
constexpr int GAME_EYE_Y = 18;

constexpr int MIN_EYE_HEIGHT = 2;

// Fakes a filled rounded rect by cutting a small background-colored
// staircase from each corner. IDisplay only offers a rounded-rect *outline*,
// not a filled one, so this composes what's already there instead of adding
// a new primitive just for this.
// bgR/G/B is what the corner staircase is cut with. Defaulted to black
// because that is the ground in every theme but MI2MO2, whose notification
// screen draws on R2's light plate — there the cut has to be the plate, or
// each eye picks up four dark specks at its corners instead of looking
// rounded. Same class of bug as the coffee cup's punch-outs, one level
// down, and it only surfaced once notifications put twin eyes on a light
// ground for the first time.
void fillRoundedRect(IDisplay& display, int x, int y, int w, int h, uint8_t r, uint8_t g, uint8_t b,
                     uint8_t bgR = BG_R, uint8_t bgG = BG_G, uint8_t bgB = BG_B) {
    display.fillRect(x, y, w, h, r, g, b);

    if (h < CORNER_MIN_SIZE || w < CORNER_MIN_SIZE) {
        return; // too thin to bother rounding
    }

    for (int row = 0; row < CORNER_ROWS; row++) {
        int cut = CORNER_CUT_WIDTH[row];
        display.fillRect(x, y + row, cut, 1, bgR, bgG, bgB);                   // top-left
        display.fillRect(x + w - cut, y + row, cut, 1, bgR, bgG, bgB);         // top-right
        display.fillRect(x, y + h - 1 - row, cut, 1, bgR, bgG, bgB);           // bottom-left
        display.fillRect(x + w - cut, y + h - 1 - row, cut, 1, bgR, bgG, bgB); // bottom-right
    }
}

// The speech-bubble box behind the message text. Fixed size — sized for the
// full 3-line window regardless of how much text is currently showing —
// rather than shrinking to fit, so it doesn't hop around as text types in.
// `lines` is how many text rows the box has to hold — normally
// MESSAGE_VISIBLE_LINES, more in Game Mode (see STATS_BOX_LINES_*). The box
// always grows upward, since its bottom edge is what's anchored to the frame.
void drawMessageBox(IDisplay& display, uint8_t r, uint8_t g, uint8_t b, int lines = MESSAGE_VISIBLE_LINES) {
    int boxHeight = lines * MESSAGE_LINE_HEIGHT + 2 * MESSAGE_BOX_PADDING_Y;
    int boxX = MESSAGE_BOX_MARGIN_X;
    int boxW = display.width() - 2 * MESSAGE_BOX_MARGIN_X;
    int boxY = display.height() - MESSAGE_BOX_MARGIN_BOTTOM - boxHeight;
    fillRoundedRect(display, boxX, boxY, boxW, boxHeight, r, g, b);
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

// MI2MO2's own message renderer: same greedy word-wrap/bottom-anchored/
// scroll-off-the-top layout as drawWrappedMessage above (duplicated rather
// than parameterized — the two draw loops diverge enough, one line at a
// time vs one glyph at a time, that sharing them would need more plumbing
// than just copying the wrap step), except the final draw happens one
// character at a time instead of one line at a time, since each character
// can be sitting at a different point in its own AUREBESH-to-LATIN "reveal
// age" (see AUREBESH_HOLD_MS) independent of its neighbors — and, since
// that same age also picks the character's color, at a different color
// from its neighbors too. lineStart[i]+k
// is that character's index into the *original* message string, which is
// exactly the index TYPING_CHAR_INTERVAL_MS-based reveal timing needs.
void drawWrappedMessageMi2Mo2(IDisplay& display, const char* message,
                              uint8_t alienR, uint8_t alienG, uint8_t alienB,
                              uint8_t latinR, uint8_t latinG, uint8_t latinB,
                              unsigned long typingStartedMs, unsigned long nowMs) {
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

    char glyph[2] = {0, 0};
    for (int i = 0; i < visibleCount; i++) {
        int lineIndex = visibleStart + i;
        int y = startY + i * MESSAGE_LINE_HEIGHT;
        int penX = MESSAGE_MARGIN_X;

        for (int k = 0; k < lineLength[lineIndex]; k++) {
            int globalIndex = lineStart[lineIndex] + k;
            unsigned long revealedAt = typingStartedMs + (unsigned long)globalIndex * TYPING_CHAR_INTERVAL_MS;
            unsigned long age = (nowMs >= revealedAt) ? (nowMs - revealedAt) : 0;
            bool stillAlien = age < AUREBESH_HOLD_MS;
            TextFont font = stillAlien ? TextFont::AUREBESH : TextFont::LATIN;

            // The color flips with the font, on the same per-character
            // clock: a character lands in red Aurebesh and turns white as
            // it resolves into Latin, so the color change reinforces the
            // decoding rather than being a separate effect to follow.
            glyph[0] = message[lineStart[lineIndex] + k];
            display.drawText(glyph, penX, y,
                             stillAlien ? alienR : latinR,
                             stillAlien ? alienG : latinG,
                             stillAlien ? alienB : latinB,
                             font);
            penX += CHAR_ADVANCE_PX;
        }
    }
}

void drawEye(IDisplay& display, int x, int y, int w, int h, uint8_t r, uint8_t g, uint8_t b,
             uint8_t bgR = BG_R, uint8_t bgG = BG_G, uint8_t bgB = BG_B) {
    fillRoundedRect(display, x, y, w, h, r, g, b, bgR, bgG, bgB);
}

// MI2MO2's eye shape: a plain filled ellipse (a circle whenever w == h,
// i.e. fully open/not blinking) instead of the usual rounded square — drawn
// as horizontal scanline strips, same "compose it from fillRect" approach
// every other shape in this file already uses, rather than a new IDisplay
// primitive just for one theme. w/h independent (not a single radius) so it
// still squashes into a proper blinking ellipse under the same eyeHeight
// math every other expression already goes through.
void drawEyeCircle(IDisplay& display, int x, int y, int w, int h, uint8_t r, uint8_t g, uint8_t b) {
    if (w <= 0 || h <= 0) {
        return;
    }
    float rx = w / 2.0f;
    float ry = h / 2.0f;
    float cy = ry - 0.5f;
    for (int row = 0; row < h; row++) {
        float dy = row - cy;
        float t = 1.0f - (dy * dy) / (ry * ry);
        if (t < 0.0f) {
            continue;
        }
        int halfWidth = (int)(rx * sqrtf(t));
        int stripX = x + (w / 2) - halfWidth;
        int stripW = halfWidth * 2;
        if (stripW < 1) {
            stripW = 1;
        }
        display.fillRect(stripX, y + row, stripW, 1, r, g, b);
    }
}

// MI2MO2's dome plate: the off-white background, its navy inset panels,
// and the silver vent — everything that never changes with expression,
// blink or look. Drawn before the lens and logic display (see Face::render)
// so those paint on top of it, the same layering drawMessageBox and
// drawWrappedMessage already use.
void drawMi2Mo2Plate(IDisplay& display) {
    display.clear(MI2MO2_PLATE_R, MI2MO2_PLATE_G, MI2MO2_PLATE_B);

    // Left column of navy panels.
    display.fillRect(MI2MO2_LEFT_PANEL_X, 16, MI2MO2_LEFT_PANEL_W, 30, MI2MO2_NAVY_R, MI2MO2_NAVY_G, MI2MO2_NAVY_B);
    display.fillRect(MI2MO2_LEFT_PANEL_X, 50, MI2MO2_LEFT_PANEL_W, 30, MI2MO2_NAVY_R, MI2MO2_NAVY_G, MI2MO2_NAVY_B);
    display.fillRect(MI2MO2_LEFT_SMALL_X, 16, MI2MO2_LEFT_SMALL_W, 14, MI2MO2_NAVY_R, MI2MO2_NAVY_G, MI2MO2_NAVY_B);
    display.fillRect(MI2MO2_LEFT_SMALL_X, 34, MI2MO2_LEFT_SMALL_W, 14, MI2MO2_NAVY_R, MI2MO2_NAVY_G, MI2MO2_NAVY_B);
    display.fillRect(MI2MO2_LEFT_SMALL_X, 52, MI2MO2_LEFT_SMALL_W, 28, MI2MO2_NAVY_R, MI2MO2_NAVY_G, MI2MO2_NAVY_B);

    // The lens's own navy panel, plus a seam down its right edge so it
    // reads as an inset plate rather than a floating rectangle.
    display.fillRect(MI2MO2_EYE_PANEL_X, MI2MO2_EYE_PANEL_Y, MI2MO2_EYE_PANEL_W, MI2MO2_EYE_PANEL_H, MI2MO2_NAVY_R, MI2MO2_NAVY_G, MI2MO2_NAVY_B);
    display.fillRect(MI2MO2_EYE_PANEL_X + MI2MO2_EYE_PANEL_W, MI2MO2_EYE_PANEL_Y + 6, 4, MI2MO2_EYE_PANEL_H - 12, MI2MO2_SEAM_R, MI2MO2_SEAM_G, MI2MO2_SEAM_B);

    // Silver vent: a disc with vertical slats cut across it.
    drawEyeCircle(display, MI2MO2_VENT_CX - MI2MO2_VENT_R, MI2MO2_VENT_CY - MI2MO2_VENT_R,
                  MI2MO2_VENT_R * 2, MI2MO2_VENT_R * 2, MI2MO2_SILVER_R, MI2MO2_SILVER_G, MI2MO2_SILVER_B);
    for (int k = -8; k <= 8; k += 3) {
        display.fillRect(MI2MO2_VENT_CX + k, MI2MO2_VENT_CY - 9, 1, 18, MI2MO2_SLAT_R, MI2MO2_SLAT_G, MI2MO2_SLAT_B);
    }

    // Navy strips filling the plate below the lamp/vent pair.
    display.fillRect(MI2MO2_STRIP_X, 58, 48, 9, MI2MO2_NAVY_R, MI2MO2_NAVY_G, MI2MO2_NAVY_B);
    display.fillRect(MI2MO2_STRIP_X, 71, 30, 9, MI2MO2_NAVY_R, MI2MO2_NAVY_G, MI2MO2_NAVY_B);
}

// The photoreceptor lens: a fixed black disc, plus the white glint
// travelling across it. The disc never changes size, shape or color — it
// doesn't blink and it doesn't squint. Blinking lives in the logic display
// instead (see drawMi2Mo2LogicDisplay), which matches the real R2: its eye
// is a static piece of glass, and everything expressive happens in the
// panels. So glintDx/Dy — a reflection sliding across otherwise
// featureless black glass — is the only thing that animates here, and the
// only cue that the lens has turned.
void drawMi2Mo2Lens(IDisplay& display, int glintDx, int glintDy) {
    drawEyeCircle(display, MI2MO2_EYE_CX - MI2MO2_EYE_SIZE / 2, MI2MO2_EYE_CY - MI2MO2_EYE_SIZE / 2,
                  MI2MO2_EYE_SIZE, MI2MO2_EYE_SIZE, MI2MO2_LENS_R, MI2MO2_LENS_G, MI2MO2_LENS_B);

    display.fillRect(MI2MO2_EYE_CX + MI2MO2_GLINT_REST_DX + glintDx,
                     MI2MO2_EYE_CY + MI2MO2_GLINT_REST_DY + glintDy,
                     MI2MO2_GLINT_SIZE, MI2MO2_GLINT_SIZE,
                     MI2MO2_GLINT_R, MI2MO2_GLINT_G, MI2MO2_GLINT_B);
}

// The logic display: MI2MO2's expression lamp (see the palette comment on
// MI2MO2_LOGIC_R). A silver bezel with the lamp inside it, the lamp's color
// and brightness both chosen per expression by Face::render.
void drawMi2Mo2LogicDisplay(IDisplay& display, uint8_t r, uint8_t g, uint8_t b, float dim) {
    drawEyeCircle(display, MI2MO2_LOGIC_CX - MI2MO2_LOGIC_RADIUS, MI2MO2_LOGIC_CY - MI2MO2_LOGIC_RADIUS,
                  MI2MO2_LOGIC_RADIUS * 2, MI2MO2_LOGIC_RADIUS * 2, MI2MO2_SEAM_R, MI2MO2_SEAM_G, MI2MO2_SEAM_B);

    int inner = MI2MO2_LOGIC_RADIUS - 2;
    drawEyeCircle(display, MI2MO2_LOGIC_CX - inner, MI2MO2_LOGIC_CY - inner, inner * 2, inner * 2,
                  (uint8_t)(r * dim), (uint8_t)(g * dim), (uint8_t)(b * dim));
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

void drawEyeX(IDisplay& display, int x, int y, int size, uint8_t r, uint8_t g, uint8_t b) {
    for (int i = 0; i + EYE_X_BLOCK <= size; i += EYE_X_STEP) {
        display.fillRect(x + i, y + i, EYE_X_BLOCK, EYE_X_BLOCK, r, g, b);                          // top-left to bottom-right
        display.fillRect(x + size - i - EYE_X_BLOCK, y + i, EYE_X_BLOCK, EYE_X_BLOCK, r, g, b);     // top-right to bottom-left
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

void drawEyeCaret(IDisplay& display, int x, int y, int size, uint8_t r, uint8_t g, uint8_t b) {
    int halfWidth = size / 2;
    int caretTop = y + (size - EYE_CARET_HEIGHT) / 2;

    for (int dx = 0; dx <= halfWidth; dx += EYE_CARET_STEP) {
        int dy = (dx * EYE_CARET_HEIGHT) / halfWidth;
        display.fillRect(x + halfWidth - dx - EYE_CARET_BLOCK / 2, caretTop + dy, EYE_CARET_BLOCK, EYE_CARET_BLOCK, r, g, b); // left stroke
        display.fillRect(x + halfWidth + dx - EYE_CARET_BLOCK / 2, caretTop + dy, EYE_CARET_BLOCK, EYE_CARET_BLOCK, r, g, b); // right stroke
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

void drawEyeGlitch(IDisplay& display, int x, int y, int w, int h, unsigned long nowMs, int eyeIndex, uint8_t r, uint8_t g, uint8_t b) {
    unsigned long tick = nowMs / GLITCH_INTERVAL_MS;
    int bandHeight = (h + GLITCH_BANDS - 1) / GLITCH_BANDS; // ceil, so only the last band is short
    int drawn = 0;

    for (int band = 0; band < GLITCH_BANDS && drawn < h; band++) {
        int bh = bandHeight;
        if (drawn + bh > h) {
            bh = h - drawn;
        }

        int offset = (int)(glitchHash(tick, band, eyeIndex) % (2 * GLITCH_MAX_OFFSET_PX + 1)) - GLITCH_MAX_OFFSET_PX;
        display.fillRect(x + offset, y + drawn, w, bh, r, g, b);
        drawn += bh;
    }
}

// THINKING in MI2MO2 replaces the glitch-band eyes above (which lean on
// having two eyes to desync against each other anyway) with an irregular
// stutter of the single eye's own light — R2D2 chattering to itself while
// it works something out. Returns the same 0..1 brightness multiplier
// drawMi2Mo2LogicDisplay's dim takes, so THINKING just feeds the lamp a
// flicker where the normal eased blink would go.
//
// Two nested time scales keep it from settling into one mechanical rhythm:
// a coarse "burst" picks the flicker rate, and within that burst each slot
// hashes to lit / nearly-out / half-lit. Same "no persistent state, derived
// purely from nowMs via a small hash" approach drawEyeGlitch just above and
// drawMatrixRain already use — there's no animation clock to start or stop
// as THINKING comes and goes.
constexpr unsigned long MI2MO2_THINK_BURST_MS = 700;

float mi2Mo2ThinkingDim(unsigned long nowMs) {
    unsigned long burst = nowMs / MI2MO2_THINK_BURST_MS;
    unsigned long slotMs = 60 + (glitchHash(burst, 0, 7) % 90); // 60..149ms
    unsigned long pick = glitchHash(nowMs / slotMs, 1, 7) % 100;

    if (pick < 55) {
        return 1.0f;  // lit
    }
    if (pick < 80) {
        return 0.12f; // nearly out
    }
    return 0.5f;      // half-lit, so the stutter isn't a pure on/off square wave
}

// FAILED in MI2MO2: three hard on/off flashes, then hold lit. Unlike every
// other effect in this file — which loop off nowMs forever and so need no
// start time — this one has to run a fixed number of times and stop, hence
// FaceState::expressionStartedMs (see its comment). Holding lit afterward
// rather than going dark matters because FAILED outlives the flashes:
// Personality keeps it on screen for FACE_OVERRIDE_DURATION_MS (4s), and a
// dark eye for the remainder would read as "asleep", not "error".
constexpr int MI2MO2_ERROR_FLASH_COUNT = 3;
constexpr unsigned long MI2MO2_ERROR_FLASH_PERIOD_MS = 260; // one off+on cycle

float mi2Mo2ErrorDim(unsigned long elapsedMs) {
    if (elapsedMs >= MI2MO2_ERROR_FLASH_COUNT * MI2MO2_ERROR_FLASH_PERIOD_MS) {
        return 1.0f;
    }
    // Starts dark so the very first frame of FAILED is already a visible
    // change — leading with the lit half would waste the first flash on
    // whatever was already showing.
    unsigned long phase = elapsedMs % MI2MO2_ERROR_FLASH_PERIOD_MS;
    return (phase < MI2MO2_ERROR_FLASH_PERIOD_MS / 2) ? 0.0f : 1.0f;
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

// The expression icons take an explicit origin rather than hardcoding one,
// because two themes place them very differently: CLASSIC/MI2MO2 put them
// in the top-left corner with a gentle bob, MATRIX parks them beside the
// eyes, static (see MATRIX_ICON_X/Y). Their own relative offsets are kept
// at the call sites via CORNER_ICON_*_DY so the icons stay aligned with
// each other wherever the group as a whole is placed.
constexpr int CORNER_ICON_X = 8;
constexpr int CORNER_ICON_BOOK_X = 7;
constexpr int CORNER_ICON_GAMEPAD_X = 7;
constexpr int CORNER_ICON_MUSIC_DY = 18;
constexpr int CORNER_ICON_PLAY_DY = 10;
constexpr int CORNER_ICON_BOOK_DY = 13;
constexpr int CORNER_ICON_GAMEPAD_DY = 11;

// The gentle up-down bob CLASSIC/MI2MO2 give their corner icons. MATRIX
// deliberately doesn't use it — there the icons are steady status lamps
// that pulse in brightness instead (see matrixIconPulse).
int cornerIconBob(unsigned long nowMs, float periodMs, float amplitude) {
    return (int)(sin((float)nowMs / periodMs) * amplitude);
}

void drawMusicNote(IDisplay& display, int baseX, int baseY, uint8_t r, uint8_t g, uint8_t b) {
    display.fillRect(baseX, baseY, 5, 4, r, g, b);          // notehead
    display.fillRect(baseX + 4, baseY - 14, 2, 16, r, g, b); // stem
    display.fillRect(baseX + 6, baseY - 14, 4, 3, r, g, b);  // flag
}

// A right-pointing play triangle in the same corner spot as the music note —
// same "staircase" scanline trick as the eye corners, just built out to a
// full triangle: each row's width traces a flat left edge and a point on the
// right, widest at the vertical middle.
constexpr int PLAY_ROWS = 9;
constexpr int PLAY_ROW_HEIGHT = 2;
constexpr int PLAY_ROW_WIDTH[PLAY_ROWS] = {1, 2, 4, 6, 8, 6, 4, 2, 1};

void drawPlayIcon(IDisplay& display, int baseX, int baseY, uint8_t r, uint8_t g, uint8_t b) {
    for (int row = 0; row < PLAY_ROWS; row++) {
        display.fillRect(baseX, baseY + row * PLAY_ROW_HEIGHT, PLAY_ROW_WIDTH[row], PLAY_ROW_HEIGHT, r, g, b);
    }
}

// A small open book bobbing in the corner while READING — two "pages" with
// a thin background-colored cut down the middle for the spine, the same
// cut-a-gap trick the rounded eye corners use.
void drawBookIcon(IDisplay& display, int baseX, int baseY, uint8_t r, uint8_t g, uint8_t b) {
    display.fillRect(baseX, baseY, 6, 8, r, g, b);      // left page
    display.fillRect(baseX + 7, baseY, 6, 8, r, g, b);  // right page
    display.fillRect(baseX + 6, baseY - 1, 1, 10, BG_R, BG_G, BG_B); // spine gap
}

// A small game controller bobbing in the corner while PLAYING — one solid
// body block with a d-pad cross and two face buttons cut out of it in
// background color, the same "cut a gap from a filled block" trick the eye
// corners and the book's spine gap use, instead of adding new shapes drawn
// on top.
void drawGamepadIcon(IDisplay& display, int baseX, int baseY, uint8_t r, uint8_t g, uint8_t b) {
    display.fillRect(baseX, baseY, 14, 8, r, g, b);       // body
    display.fillRect(baseX - 2, baseY + 4, 3, 3, r, g, b); // left grip
    display.fillRect(baseX + 13, baseY + 4, 3, 3, r, g, b); // right grip

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

// Geometry is parameterized rather than reading COFFEE_CUP_* directly
// because the notification screen draws this same cup at roughly double
// size in the middle of the frame (see drawCoffeeNotification). The corner
// call site passes exactly the constants this used to hardcode, so the
// in-frame icon is unchanged. Proportions that read as "a mug" — the rim
// inset, the handle, the wisp spacing — scale off the passed width/height
// instead of being fixed pixel counts.
// bgR/G/B is the color the hollow punch-outs are filled with. It's a
// parameter rather than BG_R/G/B because MI2MO2 doesn't draw on black: its
// ground is R2's off-white plate, and punching the mug's interior and the
// handle's gap in black there left two black holes in the cup instead of an
// open mug. A real bug, fixed once. Every other theme passes the black it
// already had.
void drawCoffeeCupAt(IDisplay& display, unsigned long nowMs, int cupX, int cupY, int cupW, int cupH,
                     int steamRisePx, uint8_t r, uint8_t g, uint8_t b,
                     uint8_t bgR, uint8_t bgG, uint8_t bgB) {
    int wall = (cupW >= 32) ? 4 : 2;        // rim/wall thickness
    int saucerH = (cupH >= 28) ? 3 : 2;
    int handleW = (cupW >= 32) ? 7 : 4;
    int handleH = (cupH >= 28) ? 16 : 8;

    // Saucer, then the cup body, hollowed out on top so it reads as an open
    // mug rather than a solid block.
    display.fillRect(cupX - wall, cupY + cupH, cupW + 2 * wall, saucerH, r, g, b);
    display.fillRect(cupX, cupY, cupW, cupH, r, g, b);
    display.fillRect(cupX + wall / 2, cupY + wall - 1, cupW - wall, cupH - wall, bgR, bgG, bgB);

    // Handle: a closed loop on the cup's right edge, hollowed out from the
    // top, bottom and right but left open where it meets the body.
    //
    // The inner cut used to start at cupX + cupW + 1 with width handleW - 1,
    // which put its right edge exactly on the outer block's right edge --
    // erasing the whole right wall, so the "handle" was really just a top
    // and a bottom prong with nothing joining them. A real bug, fixed once:
    // the cut is now inset by handleT on that side too, which is what
    // actually closes the loop.
    int handleT = (cupW >= 32) ? 3 : 2; // stroke thickness of the loop
    display.fillRect(cupX + cupW, cupY + wall - 1, handleW, handleH, r, g, b);
    display.fillRect(cupX + cupW, cupY + wall - 1 + handleT,
                     handleW - handleT, handleH - 2 * handleT, bgR, bgG, bgB);

    // Three wisps, evenly staggered in phase so they never all rise/fade in
    // lockstep, each drifting up and gently side to side as it climbs.
    int wispSize = (cupW >= 32) ? 3 : 2;
    int wispSpacing = cupW / (COFFEE_STEAM_COUNT + 1);
    for (int i = 0; i < COFFEE_STEAM_COUNT; i++) {
        unsigned long phaseOffset = (unsigned long)i * (COFFEE_STEAM_CYCLE_MS / COFFEE_STEAM_COUNT);
        unsigned long t = (nowMs + phaseOffset) % COFFEE_STEAM_CYCLE_MS;
        float progress = (float)t / (float)COFFEE_STEAM_CYCLE_MS; // 0..1 rise, then loops
        int riseY = (int)(progress * (float)steamRisePx);
        int sway = (int)(sin(progress * 6.2832f * 2.0f) * (cupW >= 32 ? 3.0f : 2.0f));
        int sx = cupX + wispSpacing * (i + 1) - wispSize / 2 + sway;
        int sy = cupY - 3 - riseY;
        display.fillRect(sx, sy, wispSize, wispSize, r, g, b);
    }
}

void drawCoffeeCup(IDisplay& display, unsigned long nowMs, uint8_t r, uint8_t g, uint8_t b) {
    drawCoffeeCupAt(display, nowMs, COFFEE_CUP_X, COFFEE_CUP_Y, COFFEE_CUP_WIDTH, COFFEE_CUP_HEIGHT,
                    COFFEE_STEAM_RISE_PX, r, g, b, BG_R, BG_G, BG_B);
}


// Persistent top-corner badges (weather top-left, clock top-right) — unlike
// every icon above, these don't depend on state.expression at all, so they
// live in their own fixed strip (roughly y=0-13) rather than the corner
// spot the expression icons use (which starts at CORNER_ICON_Y_SHIFT to
// stay clear of this strip). See PROTOCOL.md's WEATHER/TIME commands.
constexpr int TOP_BADGE_MARGIN = 2;

// Small cloud silhouette shared by every condition except CLEAR (no cloud —
// just a sun/moon) and FOG (just haze lines, no cloud outline).
void drawWeatherCloud(IDisplay& display, int x, int y, uint8_t r, uint8_t g, uint8_t b) {
    display.fillRect(x, y + 3, 10, 4, r, g, b);
    display.fillRect(x + 2, y + 1, 6, 3, r, g, b);
}

// CLEAR at night swaps the sun disc for a crescent: same filled disc, then a
// second disc cut out of it in background color, offset up-right so what's
// left reads as a crescent rather than a smaller sun.
void drawWeatherMoon(IDisplay& display, int x, int y, uint8_t r, uint8_t g, uint8_t b) {
    fillRoundedRect(display, x + 1, y + 1, 8, 8, r, g, b);
    fillRoundedRect(display, x + 3, y - 1, 8, 8, BG_R, BG_G, BG_B);
    // The cut leaves a sharp 90-degree reflex corner where the crescent's
    // two arms meet, reading as a blocky "L" rather than a curve — filling
    // the notch right at that corner (not floating in the cut's middle)
    // rounds it into a proper crescent silhouette.
    display.fillRect(x + 3, y + 5, 2, 2, r, g, b);
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

void drawWeatherIcon(IDisplay& display, int x, int y, WeatherCondition condition, bool isNight, uint8_t r, uint8_t g, uint8_t b) {
    switch (condition) {
        case WeatherCondition::CLEAR:
            if (isNight) {
                drawWeatherMoon(display, x, y, r, g, b);
            } else {
                fillRoundedRect(display, x + 1, y + 1, 8, 8, r, g, b);
            }
            break;

        case WeatherCondition::CLOUDY:
            drawWeatherCloud(display, x, y + 1, r, g, b);
            break;

        case WeatherCondition::RAIN:
            drawWeatherCloud(display, x, y, r, g, b);
            display.fillRect(x + 1, y + 8, 1, 2, r, g, b);
            display.fillRect(x + 4, y + 8, 1, 2, r, g, b);
            display.fillRect(x + 7, y + 8, 1, 2, r, g, b);
            break;

        case WeatherCondition::STORM:
            drawWeatherCloud(display, x, y, r, g, b);
            display.fillRect(x + 3, y + 7, 2, 2, r, g, b);
            display.fillRect(x + 5, y + 9, 2, 2, r, g, b);
            break;

        case WeatherCondition::SNOW:
            drawWeatherCloud(display, x, y, r, g, b);
            display.fillRect(x + 1, y + 8, 2, 2, r, g, b);
            display.fillRect(x + 5, y + 9, 2, 2, r, g, b);
            display.fillRect(x + 8, y + 8, 2, 2, r, g, b);
            break;

        case WeatherCondition::FOG:
            display.fillRect(x, y + 1, 10, 2, r, g, b);
            display.fillRect(x + 1, y + 4, 8, 2, r, g, b);
            display.fillRect(x, y + 7, 10, 2, r, g, b);
            break;
    }
}

void drawWeatherBadge(IDisplay& display, int tempC, WeatherCondition condition, const char* timeText, uint8_t r, uint8_t g, uint8_t b) {
    drawWeatherIcon(display, TOP_BADGE_MARGIN, TOP_BADGE_MARGIN, condition, isNightHour(timeText), r, g, b);

    char buf[8];
    snprintf(buf, sizeof(buf), "%dC", tempC);
    display.drawText(buf, TOP_BADGE_MARGIN + 13, TOP_BADGE_MARGIN + 2, r, g, b);
}

void drawClockBadge(IDisplay& display, const char* timeText, uint8_t r, uint8_t g, uint8_t b) {
    int textWidth = (int)strlen(timeText) * CHAR_ADVANCE_PX;
    int x = display.width() - TOP_BADGE_MARGIN - textWidth;
    display.drawText(timeText, x, TOP_BADGE_MARGIN + 2, r, g, b);
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
void drawSleepZzz(IDisplay& display, int rightEyeX, int eyeSize, int eyeTop, unsigned long nowMs, uint8_t r, uint8_t g, uint8_t b) {
    constexpr int ZZZ_COUNT = 3;
    constexpr int ZZZ_STEP_X = 8;
    constexpr int ZZZ_STEP_Y = 8;

    for (int i = 0; i < ZZZ_COUNT; i++) {
        float phase = (float)nowMs / 300.0f + (float)i * 1.4f;
        int bob = (int)(sin(phase) * 2.0f);
        int zx = rightEyeX + eyeSize - 4 + i * ZZZ_STEP_X;
        int zy = eyeTop - 12 - i * ZZZ_STEP_Y + bob;
        display.drawText("Z", zx, zy, r, g, b);
    }
}

// THINKING/SLEEPING's rain: while MATRIX is active and Core is THINKING or
// SLEEPING, the console log's region is swapped for a "digital rain" of
// characters rising through it instead — messages come back the instant
// either one clears, since resolveExpression falls through to whatever's
// next (see Personality.cpp) and Face::render just goes back to calling
// drawMatrixLog every frame from then on, same as any other foreground
// expression's icon disappearing. There's no animation-start/stop state to
// track here at all: like drawEyeGlitch, this is driven purely off nowMs.
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
// MATRIX's two log tabs (see Personality's _foregroundLog/_backgroundLog).
// The header names both and brackets whichever is live, so it's obvious at a
// glance that the other one still exists and is holding its own history —
// with only the active tab's lines on screen, a bare switch would otherwise
// look like the log had simply been wiped. Costs one of the log's visible
// lines, which is why MATRIX_LOG_FIRST_LINE_Y (not MATRIX_LOG_TOP_Y) is
// where the entries themselves start.
// Three tabs at 6px per character: the longest of these is 20 chars = 120px,
// comfortably inside the 160px frame.
constexpr const char* MATRIX_TAB_AI_ACTIVE = "[IA] MIDIA MONITOR";
constexpr const char* MATRIX_TAB_MEDIA_ACTIVE = "IA [MIDIA] MONITOR";
constexpr const char* MATRIX_TAB_MONITOR_ACTIVE = "IA MIDIA [MONITOR]";
constexpr int MATRIX_LOG_FIRST_LINE_Y = MATRIX_LOG_TOP_Y + MESSAGE_LINE_HEIGHT;

void drawMatrixTabHeader(IDisplay& display, LogTab tab) {
    const char* header = MATRIX_TAB_AI_ACTIVE;
    if (tab == LogTab::MEDIA) {
        header = MATRIX_TAB_MEDIA_ACTIVE;
    } else if (tab == LogTab::MONITOR) {
        header = MATRIX_TAB_MONITOR_ACTIVE;
    }
    display.drawText(header, MATRIX_LOG_X, MATRIX_LOG_TOP_Y, MSG_R, MSG_G, MSG_B);
}

// Game Mode's machine stats, laid out as two lines that fit the 160px frame
// at 6px per character: "CPU  25%  53C" is 13 chars = 78px, so even the
// widest realistic reading leaves room to spare.
//
// A field the PC app couldn't source arrives as -1 (see FaceState::hasStats)
// and prints as "--". That distinction is the whole point of carrying -1
// instead of 0: a CPU sitting at 0 C is a reading nobody should believe,
// while a dash plainly says "no source for this", which on most machines is
// what the CPU temperature genuinely is (see SystemStatsMonitor on the PC
// side for why that one is uniquely hard to get).
constexpr int STATS_LINE_CAPACITY = 24;

void formatStatValue(char* out, size_t size, int value, char suffix) {
    if (value < 0) {
        snprintf(out, size, "--");
    } else {
        snprintf(out, size, "%d%c", value, suffix);
    }
}

// Fills `line` with one "LABEL load temp" row. withTemp = false omits the
// temperature column entirely, which is how RAM — which has no temperature to
// show at all, as opposed to one that couldn't be read — shares this
// formatter with CPU and GPU. That's a different thing from a -1 temperature,
// which does print, as "--".
void formatStatsLine(char* line, size_t size, const char* label, int loadPercent, int tempC, bool withTemp) {
    char loadText[8];
    formatStatValue(loadText, sizeof(loadText), loadPercent, '%');

    if (!withTemp) {
        snprintf(line, size, "%s %s", label, loadText);
        return;
    }

    char tempText[8];
    formatStatValue(tempText, sizeof(tempText), tempC, 'C');
    snprintf(line, size, "%s %s %s", label, loadText, tempText);
}

// Game Mode's stats for the themes that have no console log to put them in
// (CLASSIC and MI2MO2): the normal message box, but holding the game's name
// and the machine's load instead of a typed message.
//
// Drawn directly rather than routed through the message system on purpose.
// A message there types itself in one character at a time and then expires,
// which is right for something said once and wrong for a readout replaced
// every couple of seconds — it would retype the whole thing on every STATS,
// and the numbers would never sit still long enough to read. Bypassing that
// is also what keeps it permanently open with no expiry to fight.
void drawStatsMessage(IDisplay& display, const FaceState& state, int boxLines) {
    drawMessageBox(display, MSG_BOX_R, MSG_BOX_G, MSG_BOX_B, boxLines);

    int boxBottom = display.height() - MESSAGE_BOX_MARGIN_BOTTOM - MESSAGE_BOX_PADDING_Y;
    // The readings are bottom-anchored so they never move as the game's name
    // wraps to a different height; the name fills whatever is left above them.
    int statsTop = boxBottom - STATS_ROWS * MESSAGE_LINE_HEIGHT;
    int nameLines = boxLines - STATS_ROWS;

    int maxChars = (display.width() - 2 * MESSAGE_MARGIN_X) / CHAR_ADVANCE_PX;
    if (maxChars > MESSAGE_MAX_LINE_CHARS) {
        maxChars = MESSAGE_MAX_LINE_CHARS;
    }

    // The game's name arrives as the background tier's own message
    // ("Jogando X"), so nothing extra had to be plumbed through for it. Same
    // greedy word-wrap the message box and the MATRIX log already use, capped
    // at the rows available above the numbers.
    char buffer[MESSAGE_MAX_LINE_CHARS + 1];
    if (state.message != nullptr && state.message[0] != '\0' && maxChars > 0) {
        int len = (int)strlen(state.message);
        int pos = 0;
        int y = boxBottom - boxLines * MESSAGE_LINE_HEIGHT;

        for (int line = 0; line < nameLines && pos < len; line++) {
            int remaining = len - pos;
            int take = (remaining <= maxChars) ? remaining : maxChars;

            if (take < remaining) {
                // Back up to the last space in the window so a word isn't cut
                // in half — unless there isn't one, in which case a hard break
                // beats dropping the rest of the name.
                int breakAt = -1;
                for (int k = take - 1; k >= 0; k--) {
                    if (state.message[pos + k] == ' ') {
                        breakAt = k;
                        break;
                    }
                }
                if (breakAt > 0) {
                    take = breakAt;
                }
            }

            memcpy(buffer, state.message + pos, take);
            buffer[take] = '\0';
            display.drawText(buffer, MESSAGE_MARGIN_X, y, MSG_R, MSG_G, MSG_B);
            y += MESSAGE_LINE_HEIGHT;

            pos += take;
            while (pos < len && state.message[pos] == ' ') {
                pos++;
            }
        }
    }

    // One reading per row, matching how MATRIX's monitor tab lists them.
    char line[STATS_LINE_CAPACITY];
    for (int i = 0; i < STATS_ROWS; i++) {
        if (i == 0) {
            formatStatsLine(line, sizeof(line), "CPU", state.statsCpuLoad, state.statsCpuTempC, true);
        } else if (i == 1) {
            formatStatsLine(line, sizeof(line), "GPU", state.statsGpuLoad, state.statsGpuTempC, true);
        } else {
            formatStatsLine(line, sizeof(line), "RAM", state.statsRamLoad, 0, false);
        }
        display.drawText(line, MESSAGE_MARGIN_X, statsTop + i * MESSAGE_LINE_HEIGHT, MSG_R, MSG_G, MSG_B);
    }
}

// Returns the y the next line would occupy, so a caller can carry on below
// whatever the log actually drew — the monitor tab needs that because a
// game's name wraps to a height nobody knows in advance.
//
// Geometry and color are parameters rather than baked-in MATRIX_* constants
// because MI84 renders the very same log (same FaceState::logLines, same
// "> " prompt, same wrap-and-scroll rules) at its own position in its own
// amber. Every MATRIX caller passes exactly the constants this used to read
// directly, so that theme's output is unchanged.
int drawMatrixLog(IDisplay& display, const FaceState& state, int firstLineY, int maxBottomY,
                  int logX, uint8_t r, uint8_t g, uint8_t b) {
    int maxCharsPerLine = (display.width() - 2 * logX) / CHAR_ADVANCE_PX;
    if (maxCharsPerLine > (int)MATRIX_LOG_LINE_CAPACITY) {
        maxCharsPerLine = (int)MATRIX_LOG_LINE_CAPACITY;
    }
    if (maxCharsPerLine <= MATRIX_LOG_LINE_PREFIX_CHARS) {
        return firstLineY;
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

    int maxVisibleLines = (maxBottomY - firstLineY) / MESSAGE_LINE_HEIGHT;
    if (maxVisibleLines < 1) {
        maxVisibleLines = 1;
    }
    int visibleStart = (wrapCount > maxVisibleLines) ? (wrapCount - maxVisibleLines) : 0;

    char lineBuffer[MATRIX_LOG_LINE_CAPACITY + MATRIX_LOG_LINE_PREFIX_CHARS + 1];
    int y = firstLineY;
    for (int i = visibleStart; i < wrapCount; i++) {
        int offset = 0;
        if (wrapIsFirst[i]) {
            lineBuffer[0] = '>';
            lineBuffer[1] = ' ';
            offset = MATRIX_LOG_LINE_PREFIX_CHARS;
        }
        memcpy(lineBuffer + offset, wrapEntry[i] + wrapStart[i], wrapLen[i]);
        lineBuffer[offset + wrapLen[i]] = '\0';
        display.drawText(lineBuffer, logX, y, r, g, b);
        y += MESSAGE_LINE_HEIGHT;
    }

    return y;
}

// The MONITOR tab: the game's name (drawn by the log above, so it keeps the
// same "> " prompt and wrapping every other entry gets) with the machine's
// load listed underneath it. Stats are drawn only as far as the room left
// above the eyes allows, so a long game name costs stat lines rather than
// overlapping them.
void drawMatrixMonitor(IDisplay& display, const FaceState& state, int maxBottomY) {
    int y = drawMatrixLog(display, state, MATRIX_LOG_FIRST_LINE_Y, maxBottomY,
                          MATRIX_LOG_X, MSG_R, MSG_G, MSG_B);

    char line[STATS_LINE_CAPACITY];
    for (int i = 0; i < 3; i++) {
        if (y + MESSAGE_LINE_HEIGHT > maxBottomY) {
            return;
        }

        if (i == 0) {
            formatStatsLine(line, sizeof(line), "CPU", state.statsCpuLoad, state.statsCpuTempC, true);
        } else if (i == 1) {
            formatStatsLine(line, sizeof(line), "GPU", state.statsGpuLoad, state.statsGpuTempC, true);
        } else {
            formatStatsLine(line, sizeof(line), "RAM", state.statsRamLoad, 0, false);
        }

        display.drawText(line, MATRIX_LOG_X, y, MSG_R, MSG_G, MSG_B);
        y += MESSAGE_LINE_HEIGHT;
    }
}

// ===========================================================================
// MI84 — a 1984 amber-CRT terminal.
//
// Where MATRIX reskins a face, MI84 reskins the *frame*: a fixed text
// chrome (header, status line, tab bar, rules) fills the top two thirds and
// MATRIX's own bottom-pinned eyes sit below it. It deliberately reuses
// MATRIX's entire log/tab machinery rather than growing a parallel one —
// same FaceState::logLines, same LogTab, same Personality-side push — so
// "which tab is live" and "what an AI message does to the log" are answered
// once, in Personality::currentState, for both themes.
//
// Unlike MATRIX it does *not* use RecoloringDisplay. That decorator
// flattens every non-black color to one value, and this theme needs two
// levels — bright ink for live content, dim for chrome (rules, labels,
// unlit bar cells) — so colors are passed explicitly, MI2MO2-style. The
// physical display's CRT post-FX (see ST7735PhysicalDisplay::present) does
// the rest; its warm phosphor tint happens to push amber exactly the right
// way, and its scanline dimming is deliberately light, which matters more
// here than anywhere else since this theme is almost entirely 7px text.
constexpr uint8_t MI84_INK_R = 255, MI84_INK_G = 176, MI84_INK_B = 0;
constexpr uint8_t MI84_DIM_R = 132, MI84_DIM_G = 90, MI84_DIM_B = 0;

// Vertical budget, in a 128px frame at MESSAGE_LINE_HEIGHT (9px) per row.
// The eyes reuse MATRIX_EYE_SIZE/GAP/BOTTOM_MARGIN, so they occupy y=93..120
// and the chrome plus content has to live above that. There's deliberately
// no rule drawn under the content: a full upward look-around lifts the eyes
// by LOOK_OFFSET_Y_PX (16px), which would carry them across a line drawn
// there — the eyes themselves close the region well enough.
constexpr int MI84_X = 4;
constexpr int MI84_HEADER_Y = 1;
constexpr int MI84_STATUS_Y = 11;
constexpr int MI84_RULE_TOP_Y = 21;
constexpr int MI84_TAB_Y = 25;
constexpr int MI84_RULE_MID_Y = 35;
constexpr int MI84_CONTENT_TOP_Y = 39;
constexpr int MI84_CONTENT_BOTTOM_Y = 84; // 5 content rows: 39,48,57,66,75
constexpr const char* MI84_HEADER_TEXT = "MIMO SYSTEM v2.6";

void drawMi84Rule(IDisplay& display, int y) {
    display.fillRect(MI84_X, y, display.width() - 2 * MI84_X, 1, MI84_DIM_R, MI84_DIM_G, MI84_DIM_B);
}

// Deliberately the same words as the wire protocol's own WEATHER tokens
// (see PROTOCOL.md): Core has no condition *names* anywhere else — every
// other theme draws a pictogram straight from the enum — so rather than
// invent a second vocabulary that could drift from the commands being sent,
// the terminal prints the token.
const char* mi84WeatherName(WeatherCondition condition) {
    switch (condition) {
        case WeatherCondition::CLOUDY: return "CLOUDY";
        case WeatherCondition::RAIN:   return "RAIN";
        case WeatherCondition::STORM:  return "STORM";
        case WeatherCondition::SNOW:   return "SNOW";
        case WeatherCondition::FOG:    return "FOG";
        case WeatherCondition::CLEAR:
        default:                       return "CLEAR";
    }
}

// Hora and Clima share one row instead of getting a line each — at 6px per
// character "TIME 17:42  RAIN 18C" is 20 chars (120px) and fits easily,
// while two rows would cost a content line for no gain. Either half is
// simply omitted when its own card is switched off, exactly as the badges
// they replace already do. No degree symbol: Font5x7 has none, and
// drawWeatherBadge already prints a bare "18C" for the same reason.
void drawMi84StatusLine(IDisplay& display, const FaceState& state) {
    char line[40];
    int len = 0;
    line[0] = '\0';

    if (state.timeText != nullptr && state.timeText[0] != '\0') {
        len = snprintf(line, sizeof(line), "TIME %s", state.timeText);
        if (len < 0 || len >= (int)sizeof(line)) {
            len = (int)sizeof(line) - 1;
        }
    }
    if (state.hasWeather) {
        int written = snprintf(line + len, sizeof(line) - (size_t)len, "%s%s %dC",
                               len > 0 ? "  " : "",
                               mi84WeatherName(state.weatherCondition), state.weatherTempC);
        if (written > 0) {
            len += written;
        }
    }

    if (line[0] != '\0') {
        display.drawText(line, MI84_X, MI84_STATUS_Y, MI84_INK_R, MI84_INK_G, MI84_INK_B);
    }
}

// Reuses MATRIX's own tab strings (18 chars, 108px) rather than the SDD's
// original "[ IA ] [ MIDIA ] [ MONITOR ]", which is 28 chars — 168px, wider
// than the whole 160px frame.
void drawMi84TabHeader(IDisplay& display, LogTab tab) {
    const char* header = MATRIX_TAB_AI_ACTIVE;
    if (tab == LogTab::MEDIA) {
        header = MATRIX_TAB_MEDIA_ACTIVE;
    } else if (tab == LogTab::MONITOR) {
        header = MATRIX_TAB_MONITOR_ACTIVE;
    }
    display.drawText(header, MI84_X, MI84_TAB_Y, MI84_INK_R, MI84_INK_G, MI84_INK_B);
}

// A terminal prompt row that types its text out one character at a time,
// holds it, erases it and starts over, with a cursor blinking on its own
// clock. Driven purely off nowMs with no state to start or stop — the same
// approach drawMatrixRain and drawEyeGlitch already use — so it simply
// stops existing the frame its expression clears.
//
// Written once and shared by the two expressions that use it, THINKING and
// the sleep states, which differ only in their text and their pacing: the
// AI is busy and types briskly, a sleeping machine drawls. Both occupy
// only the *last* content row, with the log still drawn above. For THINKING
// that's what keeps the PreToolUse tool labels ("Executando comando...",
// see AiThoughtsListener) visible — they keep scrolling in the log the way
// they do in MATRIX, while the prompt line underneath shows the machine is
// still working. Letting the animation claim the whole region would have
// hidden them.
constexpr unsigned long MI84_CURSOR_HALF_PERIOD_MS = 450;
// Bounds the local buffer: '>' + text + '_' + NUL.
constexpr int MI84_PROMPT_TEXT_MAX = 24;

void drawMi84TypedPrompt(IDisplay& display, unsigned long nowMs, int y, const char* text,
                         unsigned long charMs, unsigned long holdMs, unsigned long blankMs) {
    int textLen = (int)strlen(text);
    if (textLen > MI84_PROMPT_TEXT_MAX) {
        textLen = MI84_PROMPT_TEXT_MAX;
    }
    // One extra char-slot so the run actually reaches the full text before
    // the hold begins — without it the last character would never be shown.
    unsigned long typeMs = (unsigned long)(textLen + 1) * charMs;
    unsigned long cycle = typeMs + holdMs + blankMs;
    unsigned long t = nowMs % cycle;

    int revealed;
    if (t < typeMs) {
        revealed = (int)(t / charMs);
        if (revealed > textLen) {
            revealed = textLen;
        }
    } else if (t < typeMs + holdMs) {
        revealed = textLen;
    } else {
        revealed = 0; // erased, just before the next pass starts
    }

    char buffer[MI84_PROMPT_TEXT_MAX + 3];
    int len = 0;
    buffer[len++] = '>';
    memcpy(buffer + len, text, (size_t)revealed);
    len += revealed;
    if (((nowMs / MI84_CURSOR_HALF_PERIOD_MS) % 2) == 0) {
        buffer[len++] = '_';
    }
    buffer[len] = '\0';

    display.drawText(buffer, MI84_X, y, MI84_INK_R, MI84_INK_G, MI84_INK_B);
}

constexpr const char* MI84_THINKING_TEXT = "THINKING";
constexpr unsigned long MI84_THINK_CHAR_MS = 110;
constexpr unsigned long MI84_THINK_HOLD_MS = 700;
constexpr unsigned long MI84_THINK_BLANK_MS = 300;

// The sleep counterpart. Deliberately much slower than THINKING's — the
// whole point of the effect is that the machine has gone drowsy, and typing
// "Z..Z..Z...Z.." at the AI's brisk pace would read as busy, not asleep.
// The uneven run of dots is the text itself rather than variable timing:
// pauses of different lengths fall out of it for free, and a single string
// keeps this on exactly the same code path as THINKING.
constexpr const char* MI84_SLEEP_TEXT = "Z..Z..Z...Z..";
constexpr unsigned long MI84_SLEEP_CHAR_MS = 210;
constexpr unsigned long MI84_SLEEP_HOLD_MS = 1200;
constexpr unsigned long MI84_SLEEP_BLANK_MS = 600;

void drawMi84ThinkingPrompt(IDisplay& display, unsigned long nowMs, int y) {
    drawMi84TypedPrompt(display, nowMs, y, MI84_THINKING_TEXT,
                        MI84_THINK_CHAR_MS, MI84_THINK_HOLD_MS, MI84_THINK_BLANK_MS);
}

void drawMi84SleepPrompt(IDisplay& display, unsigned long nowMs, int y) {
    drawMi84TypedPrompt(display, nowMs, y, MI84_SLEEP_TEXT,
                        MI84_SLEEP_CHAR_MS, MI84_SLEEP_HOLD_MS, MI84_SLEEP_BLANK_MS);
}

// The MEDIA tab. With playback status and track position deliberately out
// of scope (neither is on the wire — WindowsMediaMonitor sends one
// "artist - title" MSG and nothing else), this is the label plus whatever
// the media log holds, drawn by the shared log renderer.
void drawMi84Media(IDisplay& display, const FaceState& state) {
    display.drawText("NOW PLAYING", MI84_X, MI84_CONTENT_TOP_Y, MI84_DIM_R, MI84_DIM_G, MI84_DIM_B);
    drawMatrixLog(display, state, MI84_CONTENT_TOP_Y + MESSAGE_LINE_HEIGHT * 2,
                  MI84_CONTENT_BOTTOM_Y, MI84_X, MI84_INK_R, MI84_INK_G, MI84_INK_B);
}

// Bar cells are fillRects rather than block characters: Font5x7 has no
// block glyph, and at this size a drawn rectangle is both sharper and
// cheaper than a glyph lookup would be. Ten cells of 5px on a 1px gap span
// exactly 60px, ending well clear of the value column.
constexpr int MI84_BAR_X = 28;
constexpr int MI84_BAR_H = 5;
constexpr int MI84_BAR_CELLS = 10;
constexpr int MI84_BAR_CELL_W = 5;
constexpr int MI84_VALUE_X = 94;
constexpr int MI84_TEMP_X = 124;
constexpr int MI84_MONITOR_ROWS = 3;

void drawMi84Bar(IDisplay& display, int y, int percent) {
    // A field with no source arrives as -1 (see FaceState::hasStats) and
    // simply lights no cells — the "--" printed beside it is what says why.
    int filled = (percent > 0) ? ((percent * MI84_BAR_CELLS + 50) / 100) : 0;
    if (filled > MI84_BAR_CELLS) {
        filled = MI84_BAR_CELLS;
    }

    for (int i = 0; i < MI84_BAR_CELLS; i++) {
        int x = MI84_BAR_X + i * (MI84_BAR_CELL_W + 1);
        if (i < filled) {
            display.fillRect(x, y, MI84_BAR_CELL_W, MI84_BAR_H, MI84_INK_R, MI84_INK_G, MI84_INK_B);
        } else {
            // An unlit cell is a dim baseline, not a hollow outline: at 5px
            // square an outlined box and a filled one read almost alike.
            display.fillRect(x, y + MI84_BAR_H - 1, MI84_BAR_CELL_W, 1, MI84_DIM_R, MI84_DIM_G, MI84_DIM_B);
        }
    }
}

// The MONITOR tab: the game's name (via the shared log renderer, so it
// keeps the same "> " prompt and wrapping) with one bar per reading under
// it. The rows sit at a fixed y rather than continuing from wherever the
// name stopped wrapping — same reasoning drawStatsMessage bottom-anchors
// its own numbers: a readout replaced every couple of seconds has to hold
// still to be readable, so a name that wraps to two lines must not shift
// the figures down.
void drawMi84Monitor(IDisplay& display, const FaceState& state) {
    int rowY = MI84_CONTENT_TOP_Y + MESSAGE_LINE_HEIGHT * 2;
    drawMatrixLog(display, state, MI84_CONTENT_TOP_Y, rowY, MI84_X,
                  MI84_INK_R, MI84_INK_G, MI84_INK_B);

    for (int i = 0; i < MI84_MONITOR_ROWS; i++) {
        const char* label = "RAM";
        int load = state.statsRamLoad;
        int tempC = -1;
        bool withTemp = false;

        if (i == 0) {
            label = "CPU"; load = state.statsCpuLoad; tempC = state.statsCpuTempC; withTemp = true;
        } else if (i == 1) {
            label = "GPU"; load = state.statsGpuLoad; tempC = state.statsGpuTempC; withTemp = true;
        }

        display.drawText(label, MI84_X, rowY, MI84_INK_R, MI84_INK_G, MI84_INK_B);
        drawMi84Bar(display, rowY + 1, load);

        char value[8];
        formatStatValue(value, sizeof(value), load, '%');
        display.drawText(value, MI84_VALUE_X, rowY, MI84_INK_R, MI84_INK_G, MI84_INK_B);

        if (withTemp) {
            char temp[8];
            formatStatValue(temp, sizeof(temp), tempC, 'C');
            display.drawText(temp, MI84_TEMP_X, rowY, MI84_INK_R, MI84_INK_G, MI84_INK_B);
        }

        rowY += MESSAGE_LINE_HEIGHT;
    }
}

// SLEEPY (the clock-driven bedtime nudge, not the idle SLEEPING) gets the
// terminal's own way of saying it: a system notice. The actual phrase is
// still Personality's — one of its ten PT-BR BEDTIME_MESSAGES, which
// already reaches the AI log through pushLogLine — so it's drawn by the
// same shared log renderer as everything else rather than plumbed
// separately. English chrome, Portuguese content, the same split the rest
// of this theme uses.
void drawMi84SleepNotice(IDisplay& display, const FaceState& state) {
    int y = MI84_CONTENT_TOP_Y;
    display.drawText("SYSTEM NOTICE", MI84_X, y, MI84_DIM_R, MI84_DIM_G, MI84_DIM_B);
    y += MESSAGE_LINE_HEIGHT;
    display.drawText("HUMAN ACTIVITY: LOW", MI84_X, y, MI84_INK_R, MI84_INK_G, MI84_INK_B);
    y += MESSAGE_LINE_HEIGHT;

    // The bedtime phrase gets the rows between the header and the prompt.
    // "RECOMMENDATION:" used to sit here as a third chrome line, and was
    // dropped to buy the room: the phrase underneath already reads as the
    // recommendation, so the label was spending a scarce row restating the
    // line below it.
    int promptY = MI84_CONTENT_BOTTOM_Y - MESSAGE_LINE_HEIGHT;
    drawMatrixLog(display, state, y, promptY, MI84_X,
                  MI84_INK_R, MI84_INK_G, MI84_INK_B);
    drawMi84SleepPrompt(display, state.nowMs, promptY);
}

void drawMi84Chrome(IDisplay& display, const FaceState& state) {
    display.drawText(MI84_HEADER_TEXT, MI84_X, MI84_HEADER_Y, MI84_INK_R, MI84_INK_G, MI84_INK_B);
    drawMi84StatusLine(display, state);
    drawMi84Rule(display, MI84_RULE_TOP_Y);
    drawMi84TabHeader(display, state.logTab);
    drawMi84Rule(display, MI84_RULE_MID_Y);
}

// Which of the three tabs is live is decided in Personality::currentState
// and arrives as state.logTab — identical to MATRIX, and the reason "AI
// activity always wins the tab" needed no new logic in either theme:
// foreground beating background in resolveExpression already produces it.
void drawMi84Content(IDisplay& display, const FaceState& state) {
    if (state.expression == Expression::SLEEPY) {
        drawMi84SleepNotice(display, state);
        return;
    }
    if (state.logTab == LogTab::MONITOR && state.hasStats) {
        drawMi84Monitor(display, state);
        return;
    }
    if (state.logTab == LogTab::MEDIA) {
        drawMi84Media(display, state);
        return;
    }

    // AI tab. THINKING and SLEEPING each claim the bottom row for their own
    // prompt animation, leaving the log one row less; everything else gets
    // the whole content area. SLEEPING is the idle deep sleep (10 min with
    // nothing happening), as opposed to SLEEPY's clock-driven bedtime
    // notice above — both type a prompt, so this theme says "asleep" the
    // same way in either state, and the whole frame is already dimmed for
    // SLEEPING by the DimmingDisplay wrapping `display`.
    bool thinking = state.expression == Expression::THINKING;
    bool sleeping = state.expression == Expression::SLEEPING;
    bool hasPrompt = thinking || sleeping;
    int promptY = MI84_CONTENT_BOTTOM_Y - MESSAGE_LINE_HEIGHT;
    drawMatrixLog(display, state, MI84_CONTENT_TOP_Y,
                  hasPrompt ? promptY : MI84_CONTENT_BOTTOM_Y, MI84_X,
                  MI84_INK_R, MI84_INK_G, MI84_INK_B);
    if (thinking) {
        drawMi84ThinkingPrompt(display, state.nowMs, promptY);
    } else if (sleeping) {
        drawMi84SleepPrompt(display, state.nowMs, promptY);
    }
}

// --- Boot sequence ---------------------------------------------------------
//
// Played once per THEME command rather than once per power-on, and that is
// the point: Core has no "a PC app just connected" signal to hang this on,
// but Brobot.Sender announces the theme as the first thing it sends over a
// fresh link and re-announces it on every reconnect (see
// Personality::onThemeCommand for the full reasoning). So the sequence runs
// exactly when MiMo comes back into contact with the PC.
//
// Bounded, unlike almost every other animation in this file — it runs once
// and stops instead of looping off nowMs — which is why it measures from
// FaceState::themeStartedMs. Same class of animation as MI2MO2's
// three-flash FAILED, and the second one to need such an anchor.
constexpr const char* MI84_BOOT_CHECKS[] = {
    "MEMORY ........ OK",
    "DISPLAY ....... OK",
    "AI CORE ....... OK",
    "AUDIO ......... OK",
};
constexpr int MI84_BOOT_CHECK_COUNT = (int)(sizeof(MI84_BOOT_CHECKS) / sizeof(MI84_BOOT_CHECKS[0]));
constexpr int MI84_BOOT_TOP_Y = 6;
constexpr unsigned long MI84_BOOT_FIRST_CHECK_MS = 320;
constexpr unsigned long MI84_BOOT_CHECK_INTERVAL_MS = 430; // slow enough to read as a machine testing itself
constexpr unsigned long MI84_BOOT_READY_MS =
    MI84_BOOT_FIRST_CHECK_MS + MI84_BOOT_CHECK_INTERVAL_MS * MI84_BOOT_CHECK_COUNT + 260;
constexpr unsigned long MI84_BOOT_BANNER_MS = MI84_BOOT_READY_MS + 900; // POST log gives way to the banner
constexpr unsigned long MI84_BOOT_END_MS = MI84_BOOT_BANNER_MS + 950;   // banner gives way to the interface

void drawMi84Boot(IDisplay& display, unsigned long elapsed) {
    if (elapsed >= MI84_BOOT_BANNER_MS) {
        int textWidth = (int)strlen(MI84_HEADER_TEXT) * CHAR_ADVANCE_PX;
        display.drawText(MI84_HEADER_TEXT, (display.width() - textWidth) / 2,
                         (display.height() - MESSAGE_LINE_HEIGHT) / 2,
                         MI84_INK_R, MI84_INK_G, MI84_INK_B);
        return;
    }

    int y = MI84_BOOT_TOP_Y;
    display.drawText("MIMO-84 BIOS", MI84_X, y, MI84_INK_R, MI84_INK_G, MI84_INK_B);
    y += MESSAGE_LINE_HEIGHT * 2;

    for (int i = 0; i < MI84_BOOT_CHECK_COUNT; i++) {
        if (elapsed < MI84_BOOT_FIRST_CHECK_MS + MI84_BOOT_CHECK_INTERVAL_MS * (unsigned long)i) {
            break;
        }
        display.drawText(MI84_BOOT_CHECKS[i], MI84_X, y, MI84_INK_R, MI84_INK_G, MI84_INK_B);
        y += MESSAGE_LINE_HEIGHT;
    }

    if (elapsed >= MI84_BOOT_READY_MS) {
        display.drawText("SYSTEM READY", MI84_X, y + MESSAGE_LINE_HEIGHT,
                         MI84_INK_R, MI84_INK_G, MI84_INK_B);
    }
}

// The eyes striking like an old lamp once the boot text clears: dark, two
// failed strikes that flare and die back, then a climb to full brightness.
// A breakpoint table rather than a curve on purpose — the misfires *are*
// the effect, and no easing function expresses "it nearly caught, then
// dropped". Same declarative-table shape BEDTIME_MESSAGES and Buzzer's own
// SoundSegment arrays already use.
struct Mi84LampStep {
    unsigned long atMs;
    float level;
};
constexpr Mi84LampStep MI84_LAMP_STEPS[] = {
    {0,    0.00f}, {90,   0.85f}, {170,  0.04f},
    {340,  0.00f}, {410,  0.70f}, {480,  0.10f},
    {570,  0.38f}, {640,  0.08f},
    {770,  0.58f}, {840,  0.32f},
    {960,  0.78f}, {1030, 0.52f},
    {1160, 0.92f}, {1240, 0.72f},
    {1350, 1.00f},
};
constexpr int MI84_LAMP_STEP_COUNT = (int)(sizeof(MI84_LAMP_STEPS) / sizeof(MI84_LAMP_STEPS[0]));
constexpr unsigned long MI84_LAMP_WARMUP_MS = 1350;

float mi84LampLevel(unsigned long sinceMs) {
    if (sinceMs >= MI84_LAMP_WARMUP_MS) {
        return 1.0f;
    }
    float level = 0.0f;
    for (int i = 0; i < MI84_LAMP_STEP_COUNT; i++) {
        if (sinceMs < MI84_LAMP_STEPS[i].atMs) {
            break;
        }
        level = MI84_LAMP_STEPS[i].level;
    }
    return level;
}

// ===========================================================================
// Notification screens.
//
// A notification is the top priority tier (see Personality::Tier) and the
// one thing that takes the entire frame: no eyes, no badges, no log, no
// message box. Face::render short-circuits to here and returns, so there is
// nothing to hide or restore — when the tier expires the very next frame
// draws whatever was underneath, untouched.
//
// Drawn straight onto the raw display rather than through the theme
// decorators. MATRIX's RecoloringDisplay flattens every non-black color to
// one value, and these screens want a distinct ink and text color, so each
// theme's palette is resolved explicitly instead — the same reason MI84 and
// MI2MO2 already thread their colors through by hand.
struct NotificationPalette {
    uint8_t bgR, bgG, bgB;
    uint8_t inkR, inkG, inkB;    // the artwork
    uint8_t textR, textG, textB; // the message
};

NotificationPalette notificationPalette(Theme theme) {
    switch (theme) {
        case Theme::MATRIX:
            return {BG_R, BG_G, BG_B, MATRIX_R, MATRIX_G, MATRIX_B, MATRIX_R, MATRIX_G, MATRIX_B};
        case Theme::MI84:
            return {BG_R, BG_G, BG_B, MI84_INK_R, MI84_INK_G, MI84_INK_B,
                    MI84_INK_R, MI84_INK_G, MI84_INK_B};
        case Theme::MI2MO2:
            // The one theme whose ground isn't black: R2's plate fills the
            // frame everywhere else, so a notification dropping to black
            // would read as the screen having switched off rather than as
            // MiMo saying something.
            return {MI2MO2_PLATE_R, MI2MO2_PLATE_G, MI2MO2_PLATE_B,
                    MI2MO2_NAVY_R, MI2MO2_NAVY_G, MI2MO2_NAVY_B,
                    MI2MO2_NAVY_R, MI2MO2_NAVY_G, MI2MO2_NAVY_B};
        case Theme::CLASSIC:
        default:
            return {BG_R, BG_G, BG_B, EYE_R, EYE_G, EYE_B, MSG_R, MSG_G, MSG_B};
    }
}

constexpr int NOTIFICATION_TEXT_LINES = 3;
constexpr int NOTIFICATION_TEXT_TOP_Y = 95; // bottom third; the face owns everything above
constexpr int NOTIFICATION_TEXT_MAX_CHARS = 26; // 160px / CHAR_ADVANCE_PX

// Greedy word-wrap, centered per line. Centering is what makes this its own
// function rather than a call to drawWrappedMessage: that one is left-
// aligned and bottom-anchored inside the message box, which is right for a
// speech bubble and wrong for a full-screen announcement.
void drawNotificationText(IDisplay& display, const char* text, uint8_t r, uint8_t g, uint8_t b) {
    if (text == nullptr || text[0] == '\0') {
        return;
    }

    int len = (int)strlen(text);
    int pos = 0;
    int y = NOTIFICATION_TEXT_TOP_Y;
    char buffer[NOTIFICATION_TEXT_MAX_CHARS + 1];

    for (int line = 0; line < NOTIFICATION_TEXT_LINES && pos < len; line++) {
        int remaining = len - pos;
        int take = (remaining <= NOTIFICATION_TEXT_MAX_CHARS) ? remaining : NOTIFICATION_TEXT_MAX_CHARS;

        if (take < remaining) {
            // Back up to the last space in the window so a word isn't cut in
            // half — unless there isn't one, in which case a hard break beats
            // dropping the rest.
            int breakAt = -1;
            for (int k = take - 1; k >= 0; k--) {
                if (text[pos + k] == ' ') {
                    breakAt = k;
                    break;
                }
            }
            if (breakAt > 0) {
                take = breakAt;
            }
        }

        memcpy(buffer, text + pos, (size_t)take);
        buffer[take] = '\0';
        int x = (display.width() - take * CHAR_ADVANCE_PX) / 2;
        if (x < 0) {
            x = 0;
        }
        display.drawText(buffer, x, y, r, g, b);
        y += MESSAGE_LINE_HEIGHT;

        pos += take;
        while (pos < len && text[pos] == ' ') {
            pos++;
        }
    }
}

// MiMo's face stays on screen for *every* notification. An earlier version
// gave the whole frame to the artwork, which meant a notification with no
// dedicated illustration of its own fell back to an empty framed card —
// unreadable, because it wasn't meant to depict anything. Making the eyes
// the constant and the artwork the optional extra removed the need for a
// placeholder at all.
constexpr int NOTIF_EYE_SIZE = 40;
constexpr int NOTIF_EYE_GAP = 16;
constexpr int NOTIF_EYE_Y = 30;

void drawNotificationEyes(IDisplay& display, int centerX, int topY, int size, int gap,
                          float openFactor, uint8_t r, uint8_t g, uint8_t b,
                          uint8_t bgR, uint8_t bgG, uint8_t bgB) {
    int h = (int)(size * openFactor);
    if (h < MIN_EYE_HEIGHT) {
        h = MIN_EYE_HEIGHT;
    }
    int totalWidth = size * 2 + gap;
    int leftX = centerX - totalWidth / 2;
    // Grows and shrinks about its own middle, so a blink closes toward the
    // centre line instead of sliding the eye up the screen.
    int top = topY + (size - h) / 2;
    drawEye(display, leftX, top, size, h, r, g, b, bgR, bgG, bgB);
    drawEye(display, leftX + size + gap, top, size, h, r, g, b, bgR, bgG, bgB);
}

// SLEEPY's animation, and the reason this notification needs no icon: the
// eyes *are* the message. They sag slowly shut as if nodding off, hang
// nearly closed for a moment, snap wide open with a start, and then blink
// three times quickly before it all begins again.
//
// Measured from FaceState::notificationStartedMs rather than raw nowMs so
// the cycle always begins at "wide awake" when the notification appears —
// off a free-running clock it could open mid-droop, which reads as a
// glitch rather than as falling asleep.
constexpr unsigned long SLEEPY_NOTIF_CYCLE_MS = 3600;
constexpr unsigned long SLEEPY_NOTIF_DROOP_END_MS = 1800; // lids sagging shut
constexpr unsigned long SLEEPY_NOTIF_NOD_END_MS = 2100;   // held nearly closed
constexpr unsigned long SLEEPY_NOTIF_SNAP_END_MS = 2200;  // startled awake
constexpr float SLEEPY_NOTIF_NEARLY_SHUT = 0.12f;
constexpr float SLEEPY_NOTIF_STARTLED_OPEN = 1.25f; // overshoots past normal, eyes wide
constexpr float SLEEPY_NOTIF_ALERT_OPEN = 1.10f;    // still a little wide from the fright
// Three quick blinks after the start, as {from, to} windows inside the cycle.
constexpr int SLEEPY_NOTIF_BLINK_COUNT = 3;
constexpr unsigned long SLEEPY_NOTIF_BLINKS[SLEEPY_NOTIF_BLINK_COUNT][2] = {
    {2380, 2540}, {2700, 2860}, {3020, 3180},
};

float sleepyNotificationOpen(unsigned long sinceStartMs) {
    unsigned long t = sinceStartMs % SLEEPY_NOTIF_CYCLE_MS;

    if (t < SLEEPY_NOTIF_DROOP_END_MS) {
        // Squared rather than linear: the lids barely move at first and then
        // give way, which is what dozing off actually looks like.
        float k = (float)t / (float)SLEEPY_NOTIF_DROOP_END_MS;
        return 1.0f - (k * k) * (1.0f - SLEEPY_NOTIF_NEARLY_SHUT);
    }
    if (t < SLEEPY_NOTIF_NOD_END_MS) {
        return SLEEPY_NOTIF_NEARLY_SHUT;
    }
    if (t < SLEEPY_NOTIF_SNAP_END_MS) {
        float k = (float)(t - SLEEPY_NOTIF_NOD_END_MS)
                / (float)(SLEEPY_NOTIF_SNAP_END_MS - SLEEPY_NOTIF_NOD_END_MS);
        return SLEEPY_NOTIF_NEARLY_SHUT + k * (SLEEPY_NOTIF_STARTLED_OPEN - SLEEPY_NOTIF_NEARLY_SHUT);
    }

    for (int i = 0; i < SLEEPY_NOTIF_BLINK_COUNT; i++) {
        unsigned long from = SLEEPY_NOTIF_BLINKS[i][0];
        unsigned long to = SLEEPY_NOTIF_BLINKS[i][1];
        if (t >= from && t < to) {
            // Shut and open again inside the window: 1 -> 0 -> 1.
            float k = (float)(t - from) / (float)(to - from);
            float triangle = 1.0f - 2.0f * ((k < 0.5f) ? k : (1.0f - k));
            return 0.08f + triangle * 0.92f;
        }
    }
    return SLEEPY_NOTIF_ALERT_OPEN;
}

// Pausa's break reminder: eyes pinned left, cup on the right. Same division
// of the frame CLASSIC's own COFFEE expression already makes (see
// COFFEE_EYES_X), just at notification scale — it's a layout that was
// already proven to read well with a cup beside a face.
constexpr int NOTIF_COFFEE_EYE_SIZE = 28;
constexpr int NOTIF_COFFEE_EYE_GAP = 10;
// The eyes sit above the cup's resting line rather than level with it, so
// the cup has somewhere to travel *up to* when it lifts -- the raise only
// reads as drinking if it ends near the face.
constexpr int NOTIF_COFFEE_EYE_Y = 28;
constexpr int NOTIF_COFFEE_EYES_CENTER_X = 46;
constexpr int NOTIF_COFFEE_CUP_X = 96;
constexpr int NOTIF_COFFEE_CUP_W = 36;
constexpr int NOTIF_COFFEE_CUP_H = 28;
constexpr int NOTIF_COFFEE_CUP_Y = 48;
constexpr int NOTIF_COFFEE_STEAM_RISE_PX = 26;

// MiMo taking a sip: the cup rises and drifts toward the face, is held
// there for a beat, and comes back down to the saucer line. Purely
// positional -- there is no rotation available at this resolution, and a
// tilted mug composed of fillRects reads as a broken mug rather than a
// tipped one, so the travel itself carries the gesture.
//
// Driven off nowMs like the steam it moves with, not off the notification's
// start: unlike SLEEPY's doze, there is no "wrong" phase to open on, and
// keeping it on the free clock means the sip and the wisps stay in the same
// relationship no matter when the notification appears.
constexpr unsigned long COFFEE_SIP_CYCLE_MS = 2800;
constexpr unsigned long COFFEE_SIP_RAISE_END_MS = 600;
constexpr unsigned long COFFEE_SIP_HOLD_END_MS = 1250;
constexpr unsigned long COFFEE_SIP_LOWER_END_MS = 1850;
constexpr int COFFEE_SIP_LIFT_PX = 17; // up toward the eyes
constexpr int COFFEE_SIP_DRAW_PX = 11; // and inward, toward the face

// Smoothstep, hand-written for the same reason Personality.cpp writes its
// own easing: this file also has to compile against the native build's
// minimal Arduino.h, which wires up no <math.h> helpers beyond sin.
float coffeeSipEase(float k) {
    return k * k * (3.0f - 2.0f * k);
}

void coffeeSipOffset(unsigned long nowMs, int* outDx, int* outDy) {
    unsigned long t = nowMs % COFFEE_SIP_CYCLE_MS;
    float lift;

    if (t < COFFEE_SIP_RAISE_END_MS) {
        lift = coffeeSipEase((float)t / (float)COFFEE_SIP_RAISE_END_MS);
    } else if (t < COFFEE_SIP_HOLD_END_MS) {
        lift = 1.0f;
    } else if (t < COFFEE_SIP_LOWER_END_MS) {
        float k = (float)(t - COFFEE_SIP_HOLD_END_MS)
                / (float)(COFFEE_SIP_LOWER_END_MS - COFFEE_SIP_HOLD_END_MS);
        lift = 1.0f - coffeeSipEase(k);
    } else {
        lift = 0.0f; // resting on the saucer between sips
    }

    *outDx = -(int)(lift * (float)COFFEE_SIP_DRAW_PX);
    *outDy = -(int)(lift * (float)COFFEE_SIP_LIFT_PX);
}

void drawCoffeeNotification(IDisplay& display, const FaceState& state, const NotificationPalette& p,
                            float openFactor) {
    drawNotificationEyes(display, NOTIF_COFFEE_EYES_CENTER_X, NOTIF_COFFEE_EYE_Y,
                         NOTIF_COFFEE_EYE_SIZE, NOTIF_COFFEE_EYE_GAP, openFactor,
                         p.inkR, p.inkG, p.inkB, p.bgR, p.bgG, p.bgB);

    int sipDx = 0, sipDy = 0;
    coffeeSipOffset(state.nowMs, &sipDx, &sipDy);
    drawCoffeeCupAt(display, state.nowMs,
                    NOTIF_COFFEE_CUP_X + sipDx, NOTIF_COFFEE_CUP_Y + sipDy,
                    NOTIF_COFFEE_CUP_W, NOTIF_COFFEE_CUP_H, NOTIF_COFFEE_STEAM_RISE_PX,
                    p.inkR, p.inkG, p.inkB, p.bgR, p.bgG, p.bgB);
}

// Weather alerts. One notification screen serves every condition: the token
// on the wire is just WEATHER, and which artwork gets drawn comes from
// FaceState::weatherCondition — the same value the WEATHER command already
// keeps current for the top-left badge. That keeps the alert and the badge
// incapable of disagreeing, and means a new condition is a `case` here
// rather than a new command.
//
// MiMo stands to the left of an umbrella with rain falling behind both. The
// rain is drawn first so the canopy and the eyes paint over it: drops that
// would fall through the umbrella simply never show, which reads as the
// umbrella sheltering him without any per-drop collision test.
constexpr int NOTIF_RAIN_COLUMNS = 18;
constexpr int NOTIF_RAIN_TOP_Y = 4;
constexpr int NOTIF_RAIN_BOTTOM_Y = 90;
constexpr int NOTIF_RAIN_DROP_H = 6;
constexpr unsigned long NOTIF_RAIN_BASE_FALL_MS = 820;
constexpr unsigned long NOTIF_RAIN_FALL_SPREAD_MS = 420; // per-column speed variation

void drawRainfall(IDisplay& display, unsigned long nowMs, uint8_t r, uint8_t g, uint8_t b) {
    int span = NOTIF_RAIN_BOTTOM_Y - NOTIF_RAIN_TOP_Y;
    int colSpacing = display.width() / NOTIF_RAIN_COLUMNS;
    if (colSpacing < 1) {
        return;
    }

    for (int i = 0; i < NOTIF_RAIN_COLUMNS; i++) {
        // Same stateless approach the Matrix rain and the eye glitch use: a
        // hash of the column index stands in for stored per-drop state, so
        // every column gets its own x jitter, speed and phase without
        // Face::render having to remember anything between frames.
        unsigned long h = glitchHash((unsigned long)i, 3, 11);
        int x = i * colSpacing + (int)(h % (unsigned long)colSpacing);
        unsigned long fallMs = NOTIF_RAIN_BASE_FALL_MS + (h % NOTIF_RAIN_FALL_SPREAD_MS);
        unsigned long t = (nowMs + (h % fallMs)) % fallMs;

        int travel = span + NOTIF_RAIN_DROP_H;
        int y = NOTIF_RAIN_TOP_Y + (int)((unsigned long)travel * t / fallMs) - NOTIF_RAIN_DROP_H;

        int top = y < NOTIF_RAIN_TOP_Y ? NOTIF_RAIN_TOP_Y : y;
        int bottom = y + NOTIF_RAIN_DROP_H;
        if (bottom > NOTIF_RAIN_BOTTOM_Y) {
            bottom = NOTIF_RAIN_BOTTOM_Y;
        }
        if (bottom > top) {
            display.fillRect(x, top, 1, bottom - top, r, g, b);
        }
    }
}

// The umbrella: a stepped dome, a shaft, and a hooked handle. Built from
// stacked fillRects like every other shape here rather than a real arc —
// at this size the steps read as a curve, and it keeps the same
// composition style as the coffee cup and the eyes.
constexpr int NOTIF_UMBRELLA_CX = 114;
constexpr int NOTIF_UMBRELLA_TOP_Y = 28;
constexpr int NOTIF_UMBRELLA_SHAFT_BOTTOM_Y = 74;
constexpr int NOTIF_UMBRELLA_ROWS = 7;
// Half-widths, top row first — doubled when drawn, so the dome stays
// symmetric about NOTIF_UMBRELLA_CX no matter what these are tuned to.
constexpr int NOTIF_UMBRELLA_HALF_W[NOTIF_UMBRELLA_ROWS] = {4, 8, 13, 17, 20, 22, 23};
constexpr int NOTIF_UMBRELLA_ROW_H = 3;

void drawUmbrella(IDisplay& display, uint8_t r, uint8_t g, uint8_t b) {
    int y = NOTIF_UMBRELLA_TOP_Y;
    for (int row = 0; row < NOTIF_UMBRELLA_ROWS; row++) {
        int halfW = NOTIF_UMBRELLA_HALF_W[row];
        display.fillRect(NOTIF_UMBRELLA_CX - halfW, y, halfW * 2, NOTIF_UMBRELLA_ROW_H, r, g, b);
        y += NOTIF_UMBRELLA_ROW_H;
    }

    // Shaft, from just under the canopy down to the handle.
    int shaftTop = y - NOTIF_UMBRELLA_ROW_H;
    display.fillRect(NOTIF_UMBRELLA_CX - 1, shaftTop, 2,
                     NOTIF_UMBRELLA_SHAFT_BOTTOM_Y - shaftTop, r, g, b);

    // Hooked handle: a "J" curling left off the bottom of the shaft.
    display.fillRect(NOTIF_UMBRELLA_CX - 8, NOTIF_UMBRELLA_SHAFT_BOTTOM_Y - 2, 9, 2, r, g, b);
    display.fillRect(NOTIF_UMBRELLA_CX - 8, NOTIF_UMBRELLA_SHAFT_BOTTOM_Y - 6, 2, 5, r, g, b);
}

// CLEAR's artwork: a small sun turning slowly in the top-right corner.
//
// Only sinf is used, with the cosine taken as sin(theta + pi/2), because
// this file also compiles against BrobotCore/native's minimal Arduino.h,
// which never wires up <math.h> — sin already reaches it transitively on
// the MSVC build, cos is not worth betting on. Same caution that made
// Personality.cpp hand-write its own easing curves.
constexpr int SUN_CX = 134;
constexpr int SUN_CY = 26;
constexpr int SUN_DISC_R = 8;
// Half-width of the disc for each |dy| from 0 to SUN_DISC_R, so the body is
// a stepped circle rather than a square — same composition style as the
// umbrella's dome, mirrored on both axes instead of one.
constexpr int SUN_DISC_HALF_W[SUN_DISC_R + 1] = {8, 8, 8, 7, 7, 6, 5, 4, 2};

constexpr int SUN_RAY_COUNT = 8;
constexpr int SUN_RAY_INNER_R = 12;   // first block clears the disc
constexpr int SUN_RAY_STEP_PX = 4;
constexpr int SUN_RAY_BLOCKS = 3;
constexpr int SUN_RAY_BLOCK_PX = 2;
constexpr unsigned long SUN_SPIN_PERIOD_MS = 6000; // one full turn
constexpr float SUN_TWO_PI = 6.2832f;
constexpr float SUN_HALF_PI = 1.5708f;

void drawSun(IDisplay& display, unsigned long nowMs, uint8_t r, uint8_t g, uint8_t b) {
    for (int dy = -SUN_DISC_R; dy <= SUN_DISC_R; dy++) {
        int halfW = SUN_DISC_HALF_W[dy < 0 ? -dy : dy];
        display.fillRect(SUN_CX - halfW, SUN_CY + dy, halfW * 2, 1, r, g, b);
    }

    // Rays are blocks stepping outward along each angle rather than drawn
    // lines: every primitive here is axis-aligned, so a diagonal has to be
    // approximated, and three spaced blocks read as a tapering ray while a
    // dense run of them would just read as a fat wedge.
    float spin = SUN_TWO_PI * (float)(nowMs % SUN_SPIN_PERIOD_MS) / (float)SUN_SPIN_PERIOD_MS;
    for (int i = 0; i < SUN_RAY_COUNT; i++) {
        float theta = spin + SUN_TWO_PI * (float)i / (float)SUN_RAY_COUNT;
        float sinT = sin(theta);
        float cosT = sin(theta + SUN_HALF_PI);

        for (int k = 0; k < SUN_RAY_BLOCKS; k++) {
            float radius = (float)(SUN_RAY_INNER_R + k * SUN_RAY_STEP_PX);
            int x = SUN_CX + (int)(radius * cosT) - SUN_RAY_BLOCK_PX / 2;
            int y = SUN_CY + (int)(radius * sinT) - SUN_RAY_BLOCK_PX / 2;
            display.fillRect(x, y, SUN_RAY_BLOCK_PX, SUN_RAY_BLOCK_PX, r, g, b);
        }
    }
}

constexpr int NOTIF_WEATHER_EYE_SIZE = 28;
constexpr int NOTIF_WEATHER_EYE_GAP = 10;
constexpr int NOTIF_WEATHER_EYE_Y = 44;
constexpr int NOTIF_WEATHER_EYES_CENTER_X = 44;
// CLEAR's sun only claims the top-right corner, not a whole column, so he
// shifts left far less than the umbrella asks for — just enough to leave a
// readable gap before the outermost ray.
constexpr int NOTIF_WEATHER_SUNNY_EYES_CENTER_X = 70;

void drawWeatherNotification(IDisplay& display, const FaceState& state,
                             const NotificationPalette& p, float openFactor) {
    // Each condition brings its own scenery; the ones with none yet fall
    // back to MiMo on his own with the message below, which is the honest
    // thing to show — an umbrella standing next to a "clear skies" alert
    // would actively contradict it.
    bool wet = state.weatherCondition == WeatherCondition::RAIN
            || state.weatherCondition == WeatherCondition::STORM;
    bool sunny = state.weatherCondition == WeatherCondition::CLEAR;

    // Rain first, so the canopy and the eyes paint over it.
    if (wet) {
        drawRainfall(display, state.nowMs, p.inkR, p.inkG, p.inkB);
    }

    // Pushed left whenever there is scenery to make room for. CLEAR needs
    // it too, though less: centred, his right eye ends at x=113 and the
    // sun's leftmost ray starts at 114 — technically not overlapping, but
    // one pixel apart reads as cramped on the real panel.
    int eyesCenterX = display.width() / 2;
    if (wet) {
        eyesCenterX = NOTIF_WEATHER_EYES_CENTER_X;
    } else if (sunny) {
        eyesCenterX = NOTIF_WEATHER_SUNNY_EYES_CENTER_X;
    }
    drawNotificationEyes(display, eyesCenterX, NOTIF_WEATHER_EYE_Y,
                         NOTIF_WEATHER_EYE_SIZE, NOTIF_WEATHER_EYE_GAP, openFactor,
                         p.inkR, p.inkG, p.inkB, p.bgR, p.bgG, p.bgB);

    if (wet) {
        drawUmbrella(display, p.inkR, p.inkG, p.inkB);
    } else if (sunny) {
        drawSun(display, state.nowMs, p.inkR, p.inkG, p.inkB);
    }
}

void drawNotificationScreen(IDisplay& display, const FaceState& state) {
    NotificationPalette p = notificationPalette(state.theme);
    display.clear(p.bgR, p.bgG, p.bgB);

    unsigned long sinceStart = state.nowMs - state.notificationStartedMs;

    if (state.expression == Expression::COFFEE) {
        // Ordinary blinking, straight off Personality's own blink clock —
        // no separate animation needed for a face that's simply awake.
        drawCoffeeNotification(display, state, p, 1.0f - state.blinkAmount);
    } else if (state.expression == Expression::WEATHER) {
        drawWeatherNotification(display, state, p, 1.0f - state.blinkAmount);
    } else if (state.expression == Expression::SLEEPY) {
        drawNotificationEyes(display, display.width() / 2, NOTIF_EYE_Y,
                             NOTIF_EYE_SIZE, NOTIF_EYE_GAP,
                             sleepyNotificationOpen(sinceStart),
                             p.inkR, p.inkG, p.inkB, p.bgR, p.bgG, p.bgB);
    } else {
        // Any notification without artwork of its own: just MiMo, blinking,
        // with the message below. There is deliberately no placeholder
        // graphic — an empty frame said nothing and only raised the
        // question of what it was supposed to be.
        drawNotificationEyes(display, display.width() / 2, NOTIF_EYE_Y,
                             NOTIF_EYE_SIZE, NOTIF_EYE_GAP, 1.0f - state.blinkAmount,
                             p.inkR, p.inkG, p.inkB, p.bgR, p.bgG, p.bgB);
    }

    drawNotificationText(display, state.message, p.textR, p.textG, p.textB);
}

} // namespace

void Face::render(IDisplay& rawDisplay, const FaceState& state) {
    // Everything below draws through `display`, composed from whichever of
    // these two decorators actually apply — recolor first (innermost), so a
    // simultaneous SLEEPING dims the theme's own green rather than dimming
    // straight past it back to the normal teal.
    bool isMatrix = state.theme == Theme::MATRIX;
    bool isMi2Mo2 = state.theme == Theme::MI2MO2;
    bool isMi84 = state.theme == Theme::MI84;

    // A notification owns the entire frame and outranks everything, MI84's
    // boot sequence included — it is the top tier by definition (see
    // Personality::Tier), so it short-circuits before any of the normal
    // face/theme composition below. Nothing needs restoring afterwards:
    // no lower tier was disturbed to make room for it, so the frame after
    // it expires simply draws what was always there.
    if (state.isNotification) {
        drawNotificationScreen(rawDisplay, state);
        return;
    }

    // MI84's boot sequence owns the whole frame while it runs, so it
    // short-circuits everything below rather than being layered over it.
    // Drawn on rawDisplay: none of the decorators apply to a POST screen.
    unsigned long mi84Elapsed = state.nowMs - state.themeStartedMs;
    if (isMi84 && mi84Elapsed < MI84_BOOT_END_MS) {
        drawMi84Boot(rawDisplay, mi84Elapsed);
        return;
    }

    RecoloringDisplay recolored(rawDisplay, MATRIX_R, MATRIX_G, MATRIX_B);
    IDisplay& themed = isMatrix ? static_cast<IDisplay&>(recolored) : rawDisplay;
    // MI2MO2 opts out of the whole-frame sleep dimming: there, SLEEPING is
    // specifically "the eye's light goes out" (see mi2mo2BlinkDim below)
    // with the blue plate, badges and message staying at full strength —
    // dimming everything would blur that into a generic faded frame.
    DimmingDisplay dimmed(themed, SLEEP_DIM_FACTOR);
    IDisplay& display = (state.expression == Expression::SLEEPING && !isMi2Mo2) ? static_cast<IDisplay&>(dimmed) : themed;

    // MI2MO2 doesn't get a global RecoloringDisplay like MATRIX — only the
    // eyes and the message text change color, everything else (badges,
    // corner icons, message box) stays exactly CLASSIC — so the eye color
    // is just a plain variable threaded into the eye-drawing calls below.
    uint8_t eyeR = isMi2Mo2 ? MI2MO2_BADGE_R : (isMi84 ? MI84_INK_R : EYE_R);
    uint8_t eyeG = isMi2Mo2 ? MI2MO2_BADGE_G : (isMi84 ? MI84_INK_G : EYE_G);
    uint8_t eyeB = isMi2Mo2 ? MI2MO2_BADGE_B : (isMi84 ? MI84_INK_B : EYE_B);

    // MI84 keeps MATRIX's eye geometry and shapes but carries two things in
    // *brightness* that the other themes express some other way: the lamp
    // striking to life in the moments after the boot sequence, and - in
    // place of MATRIX's glitch bands - an irregular THINKING flicker
    // borrowed wholesale from MI2MO2's logic-display lamp. Reusing
    // mi2Mo2ThinkingDim rather than writing a second stutter is deliberate:
    // it isn't really MI2MO2-specific, just the one place that needed it
    // first, and both themes want exactly the same restless rhythm.
    if (isMi84) {
        // Safe unsigned arithmetic: the boot early-return above guarantees
        // mi84Elapsed >= MI84_BOOT_END_MS by the time execution gets here.
        float lamp = (state.expression == Expression::THINKING)
            ? mi2Mo2ThinkingDim(state.nowMs)
            : mi84LampLevel(mi84Elapsed - MI84_BOOT_END_MS);
        eyeR = (uint8_t)(eyeR * lamp);
        eyeG = (uint8_t)(eyeG * lamp);
        eyeB = (uint8_t)(eyeB * lamp);
    }

    // The expression corner icons (music note, play triangle, book,
    // gamepad, coffee cup, sleeping "Z Z Z") get their own color rather
    // than sharing the badges' — in MI2MO2 the weather/clock badges are
    // navy so they read as printed onto the plate, while these icons are
    // transient status lights and stay the theme's red, matching the logic
    // display. Every other theme uses one color for both, as before.
    // MI84 suppresses every one of these except COFFEE's cup (see the icon
    // block further down), so in practice this only colors that cup - but
    // it has to be the theme's amber all the same: left on CLASSIC's teal,
    // the one icon this theme does draw came out as the single non-amber
    // thing on an otherwise monochrome terminal.
    uint8_t iconR = isMi2Mo2 ? MI2MO2_LOGIC_R : (isMi84 ? MI84_INK_R : EYE_R);
    uint8_t iconG = isMi2Mo2 ? MI2MO2_LOGIC_G : (isMi84 ? MI84_INK_G : EYE_G);
    uint8_t iconB = isMi2Mo2 ? MI2MO2_LOGIC_B : (isMi84 ? MI84_INK_B : EYE_B);

    bool hasMessage = state.message != nullptr && state.message[0] != '\0' && !isMatrix && !isMi84;

    bool isCoffee = state.expression == Expression::COFFEE;

    // Game Mode: the stats panel replaces the message box in the themes with
    // no console log of their own. MATRIX doesn't come through here — its
    // MONITOR tab already shows the same figures (see drawMatrixMonitor).
    // Resolved before the geometry below because CLASSIC's eyes have to move
    // out of the way of the taller box, so both need the same answer.
    // MI84 is excluded for the same reason MATRIX is: its MONITOR tab
    // already shows these figures, as bars (see drawMi84Monitor).
    bool showStatsBox = state.hasStats && state.expression == Expression::PLAYING && !isMatrix && !isMi84;
    // Only CLASSIC rearranges: MI2MO2 keeps its plate exactly as it is and
    // simply gets a shorter box that clears the lens (see STATS_BOX_LINES_*).
    bool gameEyes = showStatsBox && !isMi2Mo2;

    // MI84 shares MATRIX's small bottom-pinned eyes exactly - same
    // constants, not a copy - which is also why Personality filters its
    // look-around pool with the same LOOK_DIRECTIONS_NO_DOWN.
    bool bottomPinnedEyes = isMatrix || isMi84;
    int eyeSize = isCoffee ? COFFEE_EYE_SIZE
        : (bottomPinnedEyes ? MATRIX_EYE_SIZE : (gameEyes ? GAME_EYE_SIZE : EYE_SIZE));
    int eyeGap = isCoffee ? COFFEE_EYE_GAP
        : (bottomPinnedEyes ? MATRIX_EYE_GAP : (gameEyes ? GAME_EYE_GAP : EYE_GAP));
    int eyeY = isCoffee ? COFFEE_EYE_Y
        : (bottomPinnedEyes ? (display.height() - eyeSize - MATRIX_EYE_BOTTOM_MARGIN)
                            : (gameEyes ? GAME_EYE_Y : EYE_Y));

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

    // MI2MO2's lens and logic display. Unlike every other theme, the eye
    // here is a fixed black lens that carries no expression at all: it only
    // blinks (squashing vertically, revealing the navy panel behind) and
    // shows a travelling glint for look-around. Everything expressive —
    // THINKING, FAILED, FINISHED, SLEEPING — lives in the logic display
    // lamp beside it, which is how the real R2 emotes. Computed here rather
    // than in the dispatch below because the values are read in two places.
    //
    // The glint slides with the look-around offset, at a fraction of the
    // normal throw (see Personality.cpp's LOOK_OFFSET_X/Y_PX) — the full
    // throw is tuned for sliding whole eyes across a 160x128 frame and
    // would carry the reflection clean off the lens.
    int mi2mo2GlintDx = (int)(state.lookOffsetX * MI2MO2_GLINT_SHIFT_SCALE);
    int mi2mo2GlintDy = (int)(state.lookOffsetY * MI2MO2_GLINT_SHIFT_SCALE);

    //   THINKING  lamp stutters irregularly (mi2Mo2ThinkingDim)
    //   FAILED    three vivid-red flashes, then holds lit (mi2Mo2ErrorDim)
    //   FINISHED  lamp turns green
    //   SLEEPING  lamp off
    //   everything else: resting red, blinking off and back on
    // The ordinary blink is the lamp's baseline brightness, since the lens
    // is a fixed black disc that can't close (see drawMi2Mo2Lens). THINKING,
    // FAILED and SLEEPING each replace that baseline outright rather than
    // multiplying into it — their own timing is the whole point, and a
    // blink cutting across it would just read as a dropped frame.
    float mi2mo2LampDim = 1.0f - state.blinkAmount;
    uint8_t mi2mo2LampR = MI2MO2_LOGIC_R, mi2mo2LampG = MI2MO2_LOGIC_G, mi2mo2LampB = MI2MO2_LOGIC_B;

    if (state.expression == Expression::THINKING) {
        mi2mo2LampDim = mi2Mo2ThinkingDim(state.nowMs);
    } else if (state.expression == Expression::FAILED) {
        mi2mo2LampDim = mi2Mo2ErrorDim(state.nowMs - state.expressionStartedMs);
        mi2mo2LampR = MI2MO2_ERROR_R; mi2mo2LampG = MI2MO2_ERROR_G; mi2mo2LampB = MI2MO2_ERROR_B;
    } else if (state.expression == Expression::FINISHED) {
        mi2mo2LampR = MI2MO2_DONE_R; mi2mo2LampG = MI2MO2_DONE_G; mi2mo2LampB = MI2MO2_DONE_B;
    } else if (state.expression == Expression::SLEEPING) {
        mi2mo2LampDim = 0.0f;
    }

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

    if (isMi2Mo2) {
        // MI2MO2 draws the same three things for every expression — plate,
        // lens, logic display — in that back-to-front order. There's
        // deliberately no per-expression eye *shape* here (no X, no caret,
        // no glitch bands): those all assume a pair of eyes, and this theme
        // has one fixed black lens. What varies per expression is only the
        // logic display's color and brightness, resolved above. None of the
        // twin-eye leftX/rightX/eyeTop geometry is touched by this branch.
        drawMi2Mo2Plate(display);
        drawMi2Mo2Lens(display, mi2mo2GlintDx, mi2mo2GlintDy);
        drawMi2Mo2LogicDisplay(display, mi2mo2LampR, mi2mo2LampG, mi2mo2LampB, mi2mo2LampDim);
    } else if (state.expression == Expression::FAILED) {
        drawEyeX(display, leftX, eyeTop, eyeSize, eyeR, eyeG, eyeB);
        drawEyeX(display, rightX, eyeTop, eyeSize, eyeR, eyeG, eyeB);
    } else if (state.expression == Expression::FINISHED) {
        drawEyeCaret(display, leftX, eyeTop, eyeSize, eyeR, eyeG, eyeB);
        drawEyeCaret(display, rightX, eyeTop, eyeSize, eyeR, eyeG, eyeB);
    } else if (state.expression == Expression::THINKING && !isMi84) {
        // MI84 deliberately falls through to the plain drawEye below: there,
        // THINKING is carried by the eyes' flicker (see the lamp block
        // above) and the ">THINKING_" prompt line, so glitch bands on top
        // would be a third simultaneous signal for one state.
        drawEyeGlitch(display, leftX, eyeTop, eyeSize, eyeHeight, state.nowMs, 0, eyeR, eyeG, eyeB);
        drawEyeGlitch(display, rightX, eyeTop, eyeSize, eyeHeight, state.nowMs, 1, eyeR, eyeG, eyeB);
    } else {
        drawEye(display, leftX, eyeTop, eyeSize, eyeHeight, eyeR, eyeG, eyeB);
        drawEye(display, rightX, eyeTop, eyeSize, eyeHeight, eyeR, eyeG, eyeB);
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
    // MI2MO2 drops two of these icons: SLEEPING's "Z Z Z" (there, sleeping
    // is just the eye's light going out — a snoring cartoon on top of that
    // fights the "powered down" read) and COFFEE's cup (a break reminder
    // there is message-only, no animation). Both per an explicit design
    // call, not a layout constraint. Its remaining icons — music note, play
    // triangle, book, gamepad — still draw, in the theme's red.
    bool mi2mo2SuppressesIcon = isMi2Mo2
        && (state.expression == Expression::SLEEPING || state.expression == Expression::COFFEE);

    // MI84 suppresses these on the same grounds MATRIX does - the
    // information is already in its log, and the icons' Y range collides
    // with the content area - with the same COFFEE exception, for the same
    // reason: COFFEE repositions the eyes for its cup regardless of theme.
    if (((!isMatrix && !isMi84) || state.expression == Expression::COFFEE) && !mi2mo2SuppressesIcon) {
        if (state.expression == Expression::SLEEPING) {
            drawSleepZzz(display, rightX, eyeSize, eyeTop, state.nowMs, iconR, iconG, iconB);
        } else if (state.expression == Expression::MUSIC) {
            drawMusicNote(display, CORNER_ICON_X,
                          CORNER_ICON_MUSIC_DY + CORNER_ICON_Y_SHIFT + cornerIconBob(state.nowMs, 400.0f, 2.0f),
                          iconR, iconG, iconB);
        } else if (state.expression == Expression::WATCHING) {
            drawPlayIcon(display, CORNER_ICON_X,
                         CORNER_ICON_PLAY_DY + CORNER_ICON_Y_SHIFT + cornerIconBob(state.nowMs, 400.0f, 2.0f),
                         iconR, iconG, iconB);
        } else if (state.expression == Expression::READING) {
            drawBookIcon(display, CORNER_ICON_BOOK_X,
                         CORNER_ICON_BOOK_DY + CORNER_ICON_Y_SHIFT + cornerIconBob(state.nowMs, 500.0f, 1.5f),
                         iconR, iconG, iconB);
        } else if (state.expression == Expression::PLAYING) {
            drawGamepadIcon(display, CORNER_ICON_GAMEPAD_X,
                            CORNER_ICON_GAMEPAD_DY + CORNER_ICON_Y_SHIFT + cornerIconBob(state.nowMs, 400.0f, 2.0f),
                            iconR, iconG, iconB);
        } else if (state.expression == Expression::COFFEE) {
            drawCoffeeCup(display, state.nowMs, iconR, iconG, iconB);
        }
    }

    if (showStatsBox) {
        drawStatsMessage(display, state, isMi2Mo2 ? STATS_BOX_LINES_MI2MO2 : STATS_BOX_LINES_CLASSIC);
    } else if (hasMessage) {
        if (isMi2Mo2) {
            // Same dark box / light text as CLASSIC: MI2MO2's plate is
            // already light, so the earlier near-white bubble blended
            // straight into it. Only the Aurebesh typing effect is
            // MI2MO2's own here (see drawWrappedMessageMi2Mo2).
            drawMessageBox(display, MSG_BOX_R, MSG_BOX_G, MSG_BOX_B);
            drawWrappedMessageMi2Mo2(display, state.message,
                                     MI2MO2_MSG_ALIEN_R, MI2MO2_MSG_ALIEN_G, MI2MO2_MSG_ALIEN_B,
                                     MSG_R, MSG_G, MSG_B,
                                     state.messageTypingStartedMs, state.nowMs);
        } else {
            drawMessageBox(display, MSG_BOX_R, MSG_BOX_G, MSG_BOX_B);
            drawWrappedMessage(display, state.message, MSG_R, MSG_G, MSG_B);
        }
    }

    // MATRIX used to drop these icons entirely; they're back, but beside
    // the bottom-pinned eyes instead of in the top-left corner (which
    // belongs to the console log here), holding still and pulsing in
    // brightness rather than bobbing (see MATRIX_ICON_X/Y, matrixIconPulse).
    //
    // Drawn on rawDisplay, deliberately bypassing the RecoloringDisplay
    // every other MATRIX draw goes through: that decorator flattens every
    // non-black color to one fixed green, so a dimmed green handed to it
    // comes out full green again and the pulse would vanish. Supplying the
    // already-pulsed green straight to the raw display is what makes
    // per-element brightness possible at all in this theme. Black cut-outs
    // (the book's spine, the gamepad's d-pad) still read correctly, since
    // they're background-colored either way.
    if (isMatrix && state.expression != Expression::COFFEE) {
        float pulse = matrixIconPulse(state.nowMs);
        uint8_t pulseR = (uint8_t)(MATRIX_R * pulse);
        uint8_t pulseG = (uint8_t)(MATRIX_G * pulse);
        uint8_t pulseB = (uint8_t)(MATRIX_B * pulse);

        if (state.expression == Expression::MUSIC) {
            drawMusicNote(rawDisplay,
                          matrixIconBaseX(MATRIX_ICON_MUSIC_LEFT, MATRIX_ICON_MUSIC_W),
                          matrixIconBaseY(MATRIX_ICON_MUSIC_TOP, MATRIX_ICON_MUSIC_H),
                          pulseR, pulseG, pulseB);
        } else if (state.expression == Expression::WATCHING) {
            drawPlayIcon(rawDisplay,
                         matrixIconBaseX(MATRIX_ICON_PLAY_LEFT, MATRIX_ICON_PLAY_W),
                         matrixIconBaseY(MATRIX_ICON_PLAY_TOP, MATRIX_ICON_PLAY_H),
                         pulseR, pulseG, pulseB);
        } else if (state.expression == Expression::READING) {
            drawBookIcon(rawDisplay,
                         matrixIconBaseX(MATRIX_ICON_BOOK_LEFT, MATRIX_ICON_BOOK_W),
                         matrixIconBaseY(MATRIX_ICON_BOOK_TOP, MATRIX_ICON_BOOK_H),
                         pulseR, pulseG, pulseB);
        } else if (state.expression == Expression::PLAYING) {
            drawGamepadIcon(rawDisplay,
                            matrixIconBaseX(MATRIX_ICON_GAMEPAD_LEFT, MATRIX_ICON_GAMEPAD_W),
                            matrixIconBaseY(MATRIX_ICON_GAMEPAD_TOP, MATRIX_ICON_GAMEPAD_H),
                            pulseR, pulseG, pulseB);
        }
    }

    // Persistent overlays: drawn last (on top), always in the same two
    // corners regardless of expression, message, or theme — see the badge
    // functions' comment above for why they don't share the expression
    // icons' spot. Unlike those icons, MATRIX doesn't suppress these: Hora/
    // Clima stay fixed at the top exactly as in CLASSIC.
    // MI84 is the one theme that suppresses both: it prints the same two
    // readings as a text status row instead (see drawMi84StatusLine), which
    // is what a terminal would do, and drawing the pictogram badges as well
    // would double them up in a strip its own header already occupies.
    if (!isMi84) {
        if (state.hasWeather) {
            drawWeatherBadge(display, state.weatherTempC, state.weatherCondition, state.timeText, eyeR, eyeG, eyeB);
        }
        if (state.timeText != nullptr && state.timeText[0] != '\0') {
            drawClockBadge(display, state.timeText, eyeR, eyeG, eyeB);
        }
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
        // SLEEPING gets the same rain as THINKING — already dimmed for free
        // by the DimmingDisplay decorator wrapping `display` above, so it
        // reads as the same digital rain just faded down for sleep instead
        // of a second animation to maintain.
        if (state.expression == Expression::THINKING || state.expression == Expression::SLEEPING) {
            // The rain takes over the whole region, header included: it
            // replaces the log outright rather than scrolling inside it,
            // and a tab header sitting above an animation with no list
            // under it would be labelling nothing.
            drawMatrixRain(display, state.nowMs, MATRIX_LOG_TOP_Y, logBottomY);
        } else {
            drawMatrixTabHeader(display, state.logTab);
            if (state.logTab == LogTab::MONITOR && state.hasStats) {
                drawMatrixMonitor(display, state, logBottomY);
            } else {
                drawMatrixLog(display, state, MATRIX_LOG_FIRST_LINE_Y, logBottomY,
                              MATRIX_LOG_X, MSG_R, MSG_G, MSG_B);
            }
        }
    }

    // MI84's terminal chrome and its tab content, drawn last (on top) for
    // the same reason MATRIX's log is. COFFEE is the one expression that
    // skips the content area: its own eyes+cup layout (see isCoffee above)
    // takes precedence over the bottom-pinned eyes regardless of theme and
    // sits right across that region, so - exactly as MATRIX does with its
    // log - the content is left out for as long as the cup is up and comes
    // back on its own the instant COFFEE clears, with no state to track.
    // The header/status/tab rows sit above the cup and stay on throughout.
    if (isMi84) {
        drawMi84Chrome(display, state);
        if (state.expression != Expression::COFFEE) {
            drawMi84Content(display, state);
        }
    }
}

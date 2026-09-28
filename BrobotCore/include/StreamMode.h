#pragma once

#include "Config.h"

// STREAM/FRAME video mode (see PROTOCOL.md and specs/sender-gba.md) — a
// third exclusive mode alongside PongGame/RpgBattle: while isActive() is
// true, main.cpp stops updating/drawing Personality/Face entirely, same
// bypass those two already use. Unlike Pong/RPG, this class never decides
// what to draw on its own — every pixel comes from the PC over the wire —
// so there's no render(IDisplay&) here. Protocol::poll hands completed rows
// to onRowReceived() as it parses the binary FRAME payload; main.cpp drains
// them straight into the physical canvas every loop() iteration via
// ST7735PhysicalDisplay::writeStreamRow, independent of the FRAME_INTERVAL_MS
// render/present cadence, so rows land with as little latency as possible.
class StreamMode {
public:
    // Parses one STREAM command's arguments (everything after "STREAM "):
    // "START" or "STOP".
    void onCommand(const char* args, unsigned long now);

    // Auto-stops if no FRAME row has arrived for STREAM_TIMEOUT_MS — see
    // Config.h's comment on that constant. No-op while !isActive().
    void update(unsigned long now);

    bool isActive() const { return _active; }

    // Called by Protocol once it has parsed a full 160-pixel row off the
    // wire (already byte-swapped to host order). Queued for main.cpp to
    // apply to the display; silently dropped if the queue is full — a slow
    // consumer losing one stale row is far better than blocking the
    // protocol parser or growing the queue without bound.
    void onRowReceived(unsigned long now, uint8_t row, const uint16_t pixels[LOGICAL_WIDTH]);

    bool hasPendingRow() const { return _queueCount > 0; }
    // Pops the oldest pending row. Caller must check hasPendingRow() first;
    // the returned pixel pointer is only valid until the next
    // onRowReceived()/takePendingRow() call.
    void takePendingRow(uint8_t& row, const uint16_t*& pixels);

    // Edge-triggered, same "true once, consumed on read" idiom as
    // PongGame::justEnded() — true exactly once, right after STREAM START,
    // so main.cpp knows to clear the canvas to black a single time before
    // any rows arrive (see PROTOCOL.md: "cleared once, then only receives
    // stream data").
    bool consumeNeedsCanvasClear();

private:
    // Generous enough to absorb a burst of rows arriving in one TCP read
    // without growing to a full duplicate framebuffer's worth of memory —
    // Phase 0's own measurement (see specs/sender-gba.md) is what tells us
    // whether this needs to move.
    static constexpr int ROW_QUEUE_DEPTH = 64;

    bool _active = false;
    bool _needsCanvasClear = false;
    unsigned long _lastFrameAt = 0;

    uint8_t _queueRow[ROW_QUEUE_DEPTH];
    uint16_t _queuePixels[ROW_QUEUE_DEPTH][LOGICAL_WIDTH];
    int _queueHead = 0;
    int _queueCount = 0;

    void stop();
};

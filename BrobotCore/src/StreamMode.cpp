#include "StreamMode.h"

#include <string.h>

void StreamMode::onCommand(const char* args, unsigned long now) {
    if (strncmp(args, "START", 5) == 0) {
        _active = true;
        _needsCanvasClear = true;
        _lastFrameAt = now;
        _queueHead = 0;
        _queueCount = 0;
    } else if (strncmp(args, "STOP", 4) == 0) {
        stop();
    }
    // Anything else is ignored, same leniency Protocol::dispatch already
    // applies to unrecognized commands.
}

void StreamMode::stop() {
    _active = false;
    _queueHead = 0;
    _queueCount = 0;
}

void StreamMode::update(unsigned long now) {
    if (!_active) {
        return;
    }
    if (now - _lastFrameAt > STREAM_TIMEOUT_MS) {
        stop();
    }
}

void StreamMode::onRowReceived(unsigned long now, uint8_t row, const uint16_t pixels[LOGICAL_WIDTH]) {
    if (!_active) {
        return;
    }
    _lastFrameAt = now;

    if (_queueCount >= ROW_QUEUE_DEPTH) {
        _droppedRows++;
        return;
    }

    int idx = (_queueHead + _queueCount) % ROW_QUEUE_DEPTH;
    _queueRow[idx] = row;
    memcpy(_queuePixels[idx], pixels, LOGICAL_WIDTH * sizeof(uint16_t));
    _queueCount++;
}

void StreamMode::takePendingRow(uint8_t& row, const uint16_t*& pixels) {
    row = _queueRow[_queueHead];
    pixels = _queuePixels[_queueHead];
    _queueHead = (_queueHead + 1) % ROW_QUEUE_DEPTH;
    _queueCount--;
}

bool StreamMode::consumeNeedsCanvasClear() {
    if (!_needsCanvasClear) {
        return false;
    }
    _needsCanvasClear = false;
    return true;
}

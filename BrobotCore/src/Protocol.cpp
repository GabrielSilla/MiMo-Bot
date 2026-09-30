#include "Protocol.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// Answer to PING. The trailing number is the protocol revision, so a future
// PC app can tell an old board from a new one without a second round trip;
// bump it only for changes a client would actually need to branch on. Bumped
// to 2 for STREAM/FRAME, and to 3 when the row record grew a header (see
// Config.h's STREAM_ROW_* comment) to support RLE, and to 4 when Core started
// listening for commands over USB (Serial) too, with `HOST USB`.
static const char* IDENTITY_REPLY = "PEEMO 4";

void Protocol::poll(Stream& serial, unsigned long now) {
    // Snapshotted once per call rather than re-checked every byte in the
    // loop condition below: on WiFiClient (ESP32), available() issues a
    // fresh lwip_ioctl(FIONREAD) syscall every single time it's called.
    // That's free for a 20-byte FACE/MSG line, but a FRAME payload is
    // thousands of bytes — re-querying it per byte turned into thousands of
    // syscalls per frame and was the dominant cost behind a measured ~9fps
    // in Phase 0 testing (see specs/sender-gba.md), far below what the
    // actual data volumes or SPI push need. Bytes that arrive mid-call
    // simply wait for the next poll() — loop() calls this continuously, so
    // that's a sub-millisecond delay, not a correctness issue.
    int available = serial.available();
    while (available-- > 0) {
#if !VSCREEN
        // Only meaningful where main.cpp drains the row queue every loop()
        // (the physical display) — see StreamMode::isQueueFull.
        if (_streamMode.isQueueFull()) {
            break;
        }
#endif
        if (_inFrame && (now - _lastByteAt) > LINE_STALE_TIMEOUT_MS) {
            // The PC app streaming this frame stalled or died mid-frame —
            // abandon it (no FRAMEOK follows) rather than wait forever for
            // bytes that may never come. The PC's one-frame-in-flight
            // pacing just times out and moves on.
            _inFrame = false;
            _abandonedFrames++;
            _rowParseState = RowParseState::HEADER;
            _rowHeaderLen = 0;
            _rowPayloadLen = 0;
        } else if (!_inFrame && _length > 0 && (now - _lastByteAt) > LINE_STALE_TIMEOUT_MS) {
            _length = 0;
        }
        _lastByteAt = now;

        if (_inFrame) {
            uint8_t b = (uint8_t)serial.read();
            _frameBytesRemaining--;

            if (_rowParseState == RowParseState::HEADER) {
                _rowHeaderBuf[_rowHeaderLen++] = b;
                if (_rowHeaderLen == STREAM_ROW_HEADER_BYTES) {
                    _currentRow = _rowHeaderBuf[0];
                    _currentEncoding = _rowHeaderBuf[1];
                    _rowPayloadNeeded = ((size_t)_rowHeaderBuf[2] << 8) | _rowHeaderBuf[3];
                    _rowHeaderLen = 0;
                    _rowPayloadLen = 0;
                    if (!beginRowPayload()) {
                        // Declared length is 0 or bigger than this board
                        // will ever accept — a corrupt/malicious length
                        // must never be trusted to index _rowPayloadBuf.
                        // Abandon the frame rather than risk an overrun.
                        _inFrame = false;
                        _abandonedFrames++;
                    } else {
                        _rowParseState = RowParseState::PAYLOAD;
                    }
                }
            } else {
                _rowPayloadBuf[_rowPayloadLen++] = b;
                if (_rowPayloadLen == _rowPayloadNeeded) {
                    applyRowBuf(now);
                    _rowParseState = RowParseState::HEADER;
                }
            }

            if (_frameBytesRemaining == 0) {
                // Anything still sitting in the header/payload buffer here
                // belongs to a malformed final record — discarded rather
                // than treated as an error, same leniency as an
                // unrecognized command.
                _inFrame = false;
                _rowParseState = RowParseState::HEADER;
                char overLine[24];
                snprintf(overLine, sizeof(overLine), "FRAMEOK %u", (unsigned)_frameSeq);
                serial.println(overLine);
                serial.println("PRESENT");
            }
            continue;
        }

        char c = (char)serial.read();

        if (c == '\r') {
            continue;
        }

        if (c == '\n') {
            _line[_length] = '\0';
            dispatch(serial, _line, now);
            _length = 0;
            continue;
        }

        if (_length < LINE_CAPACITY - 1) {
            _line[_length++] = c;
        }
        // Overflow bytes beyond LINE_CAPACITY are silently dropped; the
        // line is still processed (truncated) once '\n' arrives.
    }
}

void Protocol::beginFrame(const char* args) {
    if (_inFrame) {
        // A FRAME header should never arrive while another is still being
        // consumed — Protocol only ever lets one be in flight at a time.
        // Ignore rather than corrupt the in-progress parse.
        return;
    }

    char* end = nullptr;
    unsigned long seq = strtoul(args, &end, 10);
    if (end == args) {
        return;
    }
    while (*end == ' ') {
        end++;
    }
    unsigned long bytes = strtoul(end, &end, 10);
    if (bytes == 0) {
        return;
    }

    _inFrame = true;
    _frameSeq = (uint16_t)seq;
    _frameBytesRemaining = bytes;
    _rowParseState = RowParseState::HEADER;
    _rowHeaderLen = 0;
    _rowPayloadLen = 0;
}

bool Protocol::beginRowPayload() {
    return _rowPayloadNeeded > 0 && _rowPayloadNeeded <= STREAM_ROW_MAX_PAYLOAD_BYTES;
}

void Protocol::applyRowBuf(unsigned long now) {
    uint16_t pixels[LOGICAL_WIDTH];

    if (_currentEncoding == 0) {
        // Raw: LOGICAL_WIDTH big-endian pixel pairs. A short/malformed
        // payload (not an exact multiple of 2, or fewer than LOGICAL_WIDTH
        // pixels) fills whatever whole pixels fit and zeroes the rest,
        // rather than reading past what actually arrived.
        size_t pixelCount = _rowPayloadLen / 2;
        if (pixelCount > (size_t)LOGICAL_WIDTH) {
            pixelCount = LOGICAL_WIDTH;
        }
        for (size_t i = 0; i < pixelCount; i++) {
            uint8_t hi = _rowPayloadBuf[i * 2];
            uint8_t lo = _rowPayloadBuf[i * 2 + 1];
            pixels[i] = (uint16_t)((hi << 8) | lo);
        }
        for (size_t i = pixelCount; i < (size_t)LOGICAL_WIDTH; i++) {
            pixels[i] = 0;
        }
    } else {
        // Run-length: [runLength][pixel, big-endian] tuples (3 bytes each),
        // each expanding to runLength copies of that pixel, until
        // LOGICAL_WIDTH pixels have been produced or the payload runs out
        // — whichever comes first, so a truncated/corrupt tuple stream
        // still leaves a fully-initialized (if incomplete-looking) row
        // instead of reading past _rowPayloadBuf.
        size_t outIdx = 0;
        size_t inIdx = 0;
        while (outIdx < (size_t)LOGICAL_WIDTH && inIdx + 3 <= _rowPayloadLen) {
            uint8_t runLength = _rowPayloadBuf[inIdx];
            uint16_t pixel = (uint16_t)((_rowPayloadBuf[inIdx + 1] << 8) | _rowPayloadBuf[inIdx + 2]);
            inIdx += 3;
            for (uint8_t r = 0; r < runLength && outIdx < (size_t)LOGICAL_WIDTH; r++) {
                pixels[outIdx++] = pixel;
            }
        }
        for (; outIdx < (size_t)LOGICAL_WIDTH; outIdx++) {
            pixels[outIdx] = 0;
        }
    }

    _streamMode.onRowReceived(now, _currentRow, pixels);
}

void Protocol::dispatch(Stream& serial, char* line, unsigned long now) {
    if (line[0] == '\0' || line[0] == '#') {
        return;
    }

    char* space = strchr(line, ' ');
    size_t commandLength = space ? (size_t)(space - line) : strlen(line);
    char* args = space ? space + 1 : line + strlen(line);

    if (commandLength == 4 && strncmp(line, "FACE", 4) == 0) {
        _personality.onFaceCommand(args, now);
    } else if (commandLength == 3 && strncmp(line, "MSG", 3) == 0) {
        _personality.onMessageCommand(args, now);
    } else if (commandLength == 7 && strncmp(line, "WEATHER", 7) == 0) {
        _personality.onWeatherCommand(args, now);
    } else if (commandLength == 4 && strncmp(line, "TIME", 4) == 0) {
        _personality.onTimeCommand(args, now);
    } else if (commandLength == 5 && strncmp(line, "THEME", 5) == 0) {
        _personality.onThemeCommand(args, now);
    } else if (commandLength == 12 && strncmp(line, "CLASSICCOLOR", 12) == 0) {
        _personality.onClassicColorCommand(args);
    } else if (commandLength == 5 && strncmp(line, "SOUND", 5) == 0) {
        _deviceSettings.onSoundCommand(args);
    } else if (commandLength == 9 && strncmp(line, "SCANLINES", 9) == 0) {
        _deviceSettings.onScanlinesCommand(args);
    } else if (commandLength == 6 && strncmp(line, "NOTIFY", 6) == 0) {
        _personality.onNotifyCommand(args, now);
    } else if (commandLength == 11 && strncmp(line, "ACHIEVEMENT", 11) == 0) {
        _personality.onAchievementCommand(args, now);
    } else if (commandLength == 5 && strncmp(line, "STATS", 5) == 0) {
        _personality.onStatsCommand(args, now);
    } else if (commandLength == 7 && strncmp(line, "AISTATS", 7) == 0) {
        _personality.onAiStatsCommand(args, now);
    } else if (commandLength == 6 && strncmp(line, "REPORT", 6) == 0) {
        _personality.onReportCommand(args, now);
    } else if (commandLength == 4 && strncmp(line, "PONG", 4) == 0) {
        if (!_rpgBattle.isActive() && !_streamMode.isActive()) {
            _pongGame.onCommand(args, now);
        }
    } else if (commandLength == 3 && strncmp(line, "RPG", 3) == 0) {
        if (!_pongGame.isActive() && !_streamMode.isActive()) {
            _rpgBattle.onCommand(args, now);
        }
    } else if (commandLength == 6 && strncmp(line, "STREAM", 6) == 0) {
        if (!_pongGame.isActive() && !_rpgBattle.isActive()) {
            _streamMode.onCommand(args, now);
        }
    } else if (commandLength == 5 && strncmp(line, "FRAME", 5) == 0) {
        beginFrame(args);
    } else if (commandLength == 4 && strncmp(line, "BUZZ", 4) == 0) {
        // Test-only hook: fires a Buzzer cue straight from the wire, same
        // "reply/react directly, don't route through Personality" shape as
        // PING below — there's no Expression or minigame state to update,
        // just a sound. VICTORY is the only cue wired up so far (see
        // Buzzer::playRpgVictory); an unrecognized name is ignored.
        if (_deviceSettings.soundEnabled() && strcmp(args, "VICTORY") == 0) {
            _buzzer.playRpgVictory(now);
        }
    } else if (commandLength == 4 && strncmp(line, "HOST", 4) == 0) {
        // Only `HOST USB` exists so far — see takeHostUsbPing().
        if (strcmp(args, "USB") == 0) {
            _hostUsbPing = true;
        }
    } else if (commandLength == 4 && strncmp(line, "PING", 4) == 0) {
        // The only command Core answers. Exists so a PC app sweeping the
        // local network for Peemo's (DHCP-assigned, therefore moving) IP can
        // tell an actual Peemo apart from anything else that merely happens
        // to be listening on PROTOCOL_TCP_PORT — see Brobot.Connection's
        // PeemoDiscovery. Deliberately not routed through Personality: it
        // says nothing about Brobot, only about the link.
        serial.println(IDENTITY_REPLY);
    }
    // Unknown commands are ignored.
}

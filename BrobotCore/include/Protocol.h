#pragma once

#include <Arduino.h>
#include "Buzzer.h"
#include "Config.h"
#include "DeviceSettings.h"
#include "Personality.h"
#include "PongGame.h"
#include "RpgBattle.h"
#include "StreamMode.h"

// Reads control commands (FACE / MSG, see PROTOCOL.md) off a Stream one
// byte at a time and dispatches complete lines to a Personality, to a
// DeviceSettings for the handful of commands (SOUND/SCANLINES) that aren't
// about Brobot's expression/behavior state, or to a PongGame/RpgBattle/
// StreamMode for the three exclusive-mode command families (see
// PROTOCOL.md's Pong, RPG Battle and STREAM/FRAME sections). Never blocks —
// safe to call every loop() iteration.
//
// PING is the one command answered here rather than forwarded anywhere:
// it's about the link itself, not about Brobot, so there's no Personality
// or DeviceSettings state for it to touch.
//
// Also the one place that arbitrates between the three exclusive modes —
// PongGame, RpgBattle and StreamMode stay unaware of each other (same
// separation Personality and PongGame already have), so a PONG/RPG/STREAM
// START is simply ignored while another one is already active, rather than
// letting one clobber another's exclusive screen.
//
// FRAME <seq> <bytes> is the one command that isn't a text line: once its
// header is parsed, poll() switches to consuming exactly <bytes> of binary
// payload off the same Stream before returning to line-oriented parsing —
// see poll()'s own comment and PROTOCOL.md's STREAM/FRAME section.
//
// BUZZ <cue> triggers a Buzzer cue directly, bypassing Personality/
// Expression entirely — a manual hook for testing a cue (e.g. the RPG
// victory fanfare) without having to actually win a battle first. See
// Protocol.cpp's dispatch for the recognized cue names.
class Protocol {
public:
    Protocol(Personality& personality, DeviceSettings& deviceSettings, PongGame& pongGame, RpgBattle& rpgBattle,
             StreamMode& streamMode, Buzzer& buzzer)
        : _personality(personality), _deviceSettings(deviceSettings), _pongGame(pongGame), _rpgBattle(rpgBattle),
          _streamMode(streamMode), _buzzer(buzzer) {}

    void poll(Stream& serial, unsigned long now);

    // True from the moment a FRAME <seq> <bytes> header is parsed until the
    // last byte of its payload lands — i.e., exactly while StreamMode's
    // canvas holds a mix of old and new rows. main.cpp uses this to hold
    // off present() until a transfer finishes, rather than risk pushing
    // that half-updated canvas to the physical panel (visible as tearing —
    // part of the frame still showing the previous picture, part already
    // the new one).
    bool isReceivingFrame() const { return _inFrame; }

    // Diagnostics only (main.cpp's periodic DIAG line): FRAMEs given up on
    // mid-transfer — the PC never gets a FRAMEOK for these.
    uint32_t abandonedFrames() const { return _abandonedFrames; }

    // Edge-triggered: true once per `HOST USB` line received since the last
    // call. A PC app on the USB link sends that as a keepalive; main.cpp uses
    // it to keep WiFi switched off while USB is up (see PROTOCOL.md). Which
    // stream the line arrived on is main.cpp's business, not Protocol's.
    bool takeHostUsbPing() {
        bool seen = _hostUsbPing;
        _hostUsbPing = false;
        return seen;
    }

private:
    // Must comfortably fit "MSG " + the longest message Personality accepts.
    static constexpr size_t LINE_CAPACITY = 264;
    // If a gap this long passes without a completed line, whatever partial
    // bytes are buffered are stale (noise, a dropped newline, a client that
    // disconnected mid-command) and get discarded before the next byte is
    // appended — otherwise they'd silently corrupt the next real command.
    // Reused as-is for FRAME's binary payload: a gap this long mid-frame
    // means the sender stalled or died, so the partial frame is abandoned
    // the same way a partial line is.
    static constexpr unsigned long LINE_STALE_TIMEOUT_MS = 300;

    char _line[LINE_CAPACITY] = {0};
    size_t _length = 0;
    unsigned long _lastByteAt = 0;
    bool _hostUsbPing = false;
    uint32_t _abandonedFrames = 0;
    Personality& _personality;
    DeviceSettings& _deviceSettings;
    PongGame& _pongGame;
    RpgBattle& _rpgBattle;
    StreamMode& _streamMode;
    Buzzer& _buzzer;

    // Binary FRAME payload state — persists across poll() calls the same
    // way _line/_length do, since a frame's payload can easily span more
    // than one call. Each row record is [row][encoding][length, 16-bit BE]
    // followed by exactly <length> bytes of payload (see Config.h's
    // STREAM_ROW_* comment) — poll() alternates between accumulating a
    // fixed-size header and a length-driven payload, decoding and handing
    // each row to _streamMode as its payload completes, until
    // _frameBytesRemaining reaches 0.
    enum class RowParseState : uint8_t { HEADER, PAYLOAD };

    bool _inFrame = false;
    uint16_t _frameSeq = 0;
    uint32_t _frameBytesRemaining = 0;
    RowParseState _rowParseState = RowParseState::HEADER;
    uint8_t _rowHeaderBuf[STREAM_ROW_HEADER_BYTES];
    size_t _rowHeaderLen = 0;
    uint8_t _rowPayloadBuf[STREAM_ROW_MAX_PAYLOAD_BYTES];
    size_t _rowPayloadLen = 0;
    size_t _rowPayloadNeeded = 0;
    uint8_t _currentRow = 0;
    uint8_t _currentEncoding = 0;

    // Takes the Stream (rather than only poll() holding it) purely so PING
    // can write its reply back to whoever asked — every other command is
    // one-way PC->Core.
    void dispatch(Stream& serial, char* line, unsigned long now);

    // Parses "<seq> <bytes>" out of a FRAME line's args and, if well-formed,
    // switches poll() into binary mode. Ignored (no state change) if
    // malformed or if a frame is already in progress.
    void beginFrame(const char* args);

    // Called once a row record's header has been parsed, to sanity-check
    // and record _rowPayloadNeeded. Returns false (aborting the frame) if
    // the declared length is 0 or would overflow _rowPayloadBuf — a
    // malformed/corrupt length must not be trusted to index into it.
    bool beginRowPayload();

    // Called once _rowPayloadBuf holds all _rowPayloadNeeded bytes: decodes
    // it per _currentEncoding (raw RGB565 big-endian, or run-length —
    // see PROTOCOL.md's STREAM/FRAME section) into 160 host-order pixels
    // and hands the row to _streamMode.
    void applyRowBuf(unsigned long now);
};

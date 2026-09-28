#pragma once

// Minimal stand-in for <Arduino.h>, used only by the native (non-Arduino)
// build in BrobotCore/native — see native/README.md. Provides just enough
// of the Arduino API surface that Face.cpp/Personality.cpp/Protocol.cpp and
// the IDisplay/SerialVirtualDisplay headers compile unchanged on the host:
// the integer typedefs, Stream, F()/__FlashStringHelper, and random().
//
// This header is never seen by the real AVR/ESP32 builds — platformio.ini
// only adds native/include to the include path for env:native.

#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>

using std::int16_t;
using std::int32_t;
using std::int8_t;
using std::size_t;
using std::uint16_t;
using std::uint32_t;
using std::uint8_t;

// Arduino's F("literal") tags a string as flash-resident so Print can stream
// it specially. There's no flash/RAM split on the host, so F() is a no-op
// reinterpret — __FlashStringHelper is never defined, only ever used as a
// pointer tag.
class __FlashStringHelper;
#define F(literal) (reinterpret_cast<const __FlashStringHelper*>(literal))

// Just enough of Arduino's Stream/Print for the shared display/protocol code,
// which only ever talks to a Stream&. See native/include/TcpStream.h for the
// concrete implementation used here in place of the real Serial object.
class Stream {
public:
    virtual ~Stream() {}

    virtual int available() = 0;
    virtual int read() = 0;
    virtual size_t write(const char* data, size_t len) = 0;

    void print(char c) { write(&c, 1); }

    void print(int value) {
        char buf[16];
        int len = std::snprintf(buf, sizeof(buf), "%d", value);
        write(buf, static_cast<size_t>(len));
    }

    void print(const char* text) { write(text, std::strlen(text)); }

    void print(const __FlashStringHelper* text) {
        print(reinterpret_cast<const char*>(text));
    }

    void println(int value) {
        print(value);
        print('\n');
    }

    void println(const char* text) {
        print(text);
        print('\n');
    }

    void println(const __FlashStringHelper* text) {
        print(text);
        print('\n');
    }
};

// Arduino's random(min, max): a pseudo-random long in [min, max). Backed by
// the C runtime's rand() — fine for driving idle-animation timing in a dev
// tool, not meant to be high quality.
inline long random(long minValue, long maxValue) {
    if (minValue >= maxValue) {
        return minValue;
    }
    return minValue + (std::rand() % (maxValue - minValue));
}

// Arduino's tone()/noTone()/pinMode(): no-ops here. There's no speaker (or
// any GPIO at all) on a dev PC, but Buzzer.cpp (and Protocol.cpp's BUZZ
// command, which calls into it unconditionally) still need these symbols to
// link — this just silently drops the sound instead of producing it, the
// same "compiles unchanged, behaves like a no-op host stand-in" role every
// other shim in this file already plays.
constexpr uint8_t OUTPUT = 1;
inline void pinMode(uint8_t /*pin*/, uint8_t /*mode*/) {}
inline void tone(uint8_t /*pin*/, unsigned int /*frequency*/, unsigned long /*duration*/ = 0) {}
inline void noTone(uint8_t /*pin*/) {}

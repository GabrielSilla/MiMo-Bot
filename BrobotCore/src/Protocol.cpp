#include "Protocol.h"

#include <string.h>

void Protocol::poll(Stream& serial, unsigned long now) {
    while (serial.available() > 0) {
        if (_length > 0 && (now - _lastByteAt) > LINE_STALE_TIMEOUT_MS) {
            _length = 0;
        }
        _lastByteAt = now;

        char c = (char)serial.read();

        if (c == '\r') {
            continue;
        }

        if (c == '\n') {
            _line[_length] = '\0';
            dispatch(_line, now);
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

void Protocol::dispatch(char* line, unsigned long now) {
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
    } else if (commandLength == 5 && strncmp(line, "SOUND", 5) == 0) {
        _deviceSettings.onSoundCommand(args);
    } else if (commandLength == 9 && strncmp(line, "SCANLINES", 9) == 0) {
        _deviceSettings.onScanlinesCommand(args);
    }
    // Unknown commands are ignored.
}

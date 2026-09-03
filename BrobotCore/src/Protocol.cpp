#include "Protocol.h"

#include <string.h>

// Answer to PING. The trailing number is the protocol revision, so a future
// PC app can tell an old board from a new one without a second round trip;
// bump it only for changes a client would actually need to branch on.
static const char* IDENTITY_REPLY = "MIMO 1";

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
    } else if (commandLength == 5 && strncmp(line, "SOUND", 5) == 0) {
        _deviceSettings.onSoundCommand(args);
    } else if (commandLength == 9 && strncmp(line, "SCANLINES", 9) == 0) {
        _deviceSettings.onScanlinesCommand(args);
    } else if (commandLength == 6 && strncmp(line, "NOTIFY", 6) == 0) {
        _personality.onNotifyCommand(args, now);
    } else if (commandLength == 5 && strncmp(line, "STATS", 5) == 0) {
        _personality.onStatsCommand(args, now);
    } else if (commandLength == 7 && strncmp(line, "AISTATS", 7) == 0) {
        _personality.onAiStatsCommand(args, now);
    } else if (commandLength == 4 && strncmp(line, "PING", 4) == 0) {
        // The only command Core answers. Exists so a PC app sweeping the
        // local network for MiMo's (DHCP-assigned, therefore moving) IP can
        // tell an actual MiMo apart from anything else that merely happens
        // to be listening on PROTOCOL_TCP_PORT — see Brobot.Connection's
        // MimoDiscovery. Deliberately not routed through Personality: it
        // says nothing about Brobot, only about the link.
        serial.println(IDENTITY_REPLY);
    }
    // Unknown commands are ignored.
}

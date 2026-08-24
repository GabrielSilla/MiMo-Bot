// Native (non-Arduino) entry point for BrobotCore, used only for local
// development without an Arduino attached. See native/README.md. Mirrors
// src/main.cpp's setup()/loop() structure, but drives a TcpBroadcastStream
// instead of a real Serial, since there's no hardware UART on the host.
//
// Core listens on a TCP port — mirroring how a real device's COM port is
// the thing PC apps connect to — and accepts multiple simultaneous clients
// (e.g. Brobot Virtual Display watching draw commands, and a separate
// sender app pushing FACE/MSG control lines, both connected at once).
//
// Not part of the PlatformIO project — built separately by native/build.ps1
// with MSVC, so it never affects the uno/esp32dev firmware builds.

#include <winsock2.h>
#include <timeapi.h>
#pragma comment(lib, "winmm.lib")

#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <ctime>
#include <thread>

#include "Arduino.h"
#include "Config.h"
#include "DeviceSettings.h"
#include "Face.h"
#include "Personality.h"
#include "Protocol.h"
#include "SerialVirtualDisplay.h"
#include "TcpBroadcastStream.h"

namespace {

unsigned long nativeMillis() {
    using namespace std::chrono;
    static const steady_clock::time_point start = steady_clock::now();
    return static_cast<unsigned long>(duration_cast<milliseconds>(steady_clock::now() - start).count());
}

} // namespace

int main(int argc, char** argv) {
    unsigned short port = static_cast<unsigned short>(argc > 1 ? std::atoi(argv[1]) : 5555);

    WSADATA wsaData;
    if (WSAStartup(MAKEWORD(2, 2), &wsaData) != 0) {
        std::fprintf(stderr, "WSAStartup failed\n");
        return 1;
    }

    std::srand(static_cast<unsigned>(std::time(nullptr)));

    // Windows' default scheduler tick is ~15.6ms, so a 1ms sleep_for below
    // would actually often sleep closer to 15ms without this — silently
    // capping the frame loop well under FRAME_INTERVAL_MS's target rate no
    // matter how low that constant is set. Bumping the system timer
    // resolution to 1ms here (and undoing it on exit, since it's a
    // process-wide OS setting) is the standard fix.
    timeBeginPeriod(1);

    TcpBroadcastStream serial;
    if (!serial.listenOn(port)) {
        std::fprintf(stderr, "Failed to listen on 127.0.0.1:%u (already in use?)\n", port);
        timeEndPeriod(1);
        WSACleanup();
        return 1;
    }
    std::printf("Brobot Core (native) -- listening on 127.0.0.1:%u\n", port);
    std::printf("Connect Brobot Virtual Display and/or Brobot.Sender to it. Ctrl+C to quit.\n");

    SerialVirtualDisplay display(serial);
    Personality personality;
    DeviceSettings deviceSettings;
    Protocol protocol(personality, deviceSettings);

    personality.begin(nativeMillis());

    unsigned long lastFrameAt = 0;
    while (true) {
        serial.acceptPendingConnections();

        unsigned long now = nativeMillis();

        protocol.poll(serial, now);
        personality.update(now);

        if (now - lastFrameAt >= FRAME_INTERVAL_MS) {
            lastFrameAt = now;
            display.clear(0, 0, 0);
            Face::render(display, personality.currentState());
            display.present();
        }

        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }

    timeEndPeriod(1);
    WSACleanup();
    return 0;
}

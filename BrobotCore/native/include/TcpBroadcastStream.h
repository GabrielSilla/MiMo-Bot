#pragma once

#include <cstdint>
#include <vector>

#include "Arduino.h"

// Stream implementation for the native build: Core listens on a TCP port —
// mirroring how a real device's COM port is the thing PC apps connect to —
// and accepts multiple simultaneous clients. E.g. Brobot Virtual Display
// watching draw commands, and a separate sender app pushing FACE/MSG
// control lines, both connected at the same time. See native/README.md.
//
// Draw-command writes are broadcast to every connected client. Control-
// command bytes are merged from whichever connected client has data
// available into a single logical byte stream, since Protocol only knows
// how to read one Stream.
//
// Winsock must already be initialized (WSAStartup) by the caller before
// constructing this, and cleaned up (WSACleanup) after it's destroyed.
class TcpBroadcastStream : public Stream {
public:
    TcpBroadcastStream();
    ~TcpBroadcastStream() override;

    // Binds to 127.0.0.1:port and starts listening (non-blocking accept).
    bool listenOn(unsigned short port);

    // Accepts any pending incoming connections without blocking. Call once
    // per main-loop iteration.
    void acceptPendingConnections();

    int clientCount() const { return static_cast<int>(_clients.size()); }

    int available() override;
    int read() override;
    size_t write(const char* data, size_t len) override;

private:
    std::uintptr_t _listenSocket;
    std::vector<std::uintptr_t> _clients;

    // Index into _clients remembered between available() and read() so
    // read() pulls the byte from the same client available() found it on.
    std::size_t _readCursor;

    void closeClient(std::size_t index);
};

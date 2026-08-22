#include "TcpBroadcastStream.h"

#include <winsock2.h>
#include <ws2tcpip.h>

#include <cstdio>

namespace {
constexpr std::uintptr_t kInvalidSocket = static_cast<std::uintptr_t>(INVALID_SOCKET);
}

TcpBroadcastStream::TcpBroadcastStream() : _listenSocket(kInvalidSocket), _readCursor(0) {}

TcpBroadcastStream::~TcpBroadcastStream() {
    for (std::uintptr_t sock : _clients) {
        closesocket(static_cast<SOCKET>(sock));
    }
    if (_listenSocket != kInvalidSocket) {
        closesocket(static_cast<SOCKET>(_listenSocket));
    }
}

bool TcpBroadcastStream::listenOn(unsigned short port) {
    SOCKET sock = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (sock == INVALID_SOCKET) {
        return false;
    }

    BOOL reuse = TRUE;
    setsockopt(sock, SOL_SOCKET, SO_REUSEADDR, reinterpret_cast<const char*>(&reuse), sizeof(reuse));

    sockaddr_in addr = {};
    addr.sin_family = AF_INET;
    addr.sin_port = htons(port);
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK); // dev tool only -- never listen beyond localhost

    if (bind(sock, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) == SOCKET_ERROR) {
        closesocket(sock);
        return false;
    }

    if (listen(sock, SOMAXCONN) == SOCKET_ERROR) {
        closesocket(sock);
        return false;
    }

    u_long nonBlocking = 1;
    ioctlsocket(sock, FIONBIO, &nonBlocking);

    _listenSocket = static_cast<std::uintptr_t>(sock);
    return true;
}

void TcpBroadcastStream::acceptPendingConnections() {
    if (_listenSocket == kInvalidSocket) {
        return;
    }

    while (true) {
        SOCKET client = accept(static_cast<SOCKET>(_listenSocket), nullptr, nullptr);
        if (client == INVALID_SOCKET) {
            return; // WSAEWOULDBLOCK: nothing pending right now
        }

        BOOL noDelay = TRUE;
        setsockopt(client, IPPROTO_TCP, TCP_NODELAY, reinterpret_cast<const char*>(&noDelay), sizeof(noDelay));

        // A stalled/non-draining client (e.g. a sender app that stopped
        // reading) must not be able to stall broadcasts to everyone else
        // for more than this long — past it, write() gives up on it.
        DWORD sendTimeoutMs = 200;
        setsockopt(client, SOL_SOCKET, SO_SNDTIMEO, reinterpret_cast<const char*>(&sendTimeoutMs), sizeof(sendTimeoutMs));

        _clients.push_back(static_cast<std::uintptr_t>(client));
        std::printf("Client connected (%d now).\n", clientCount());
    }
}

int TcpBroadcastStream::available() {
    for (std::size_t i = 0; i < _clients.size();) {
        SOCKET sock = static_cast<SOCKET>(_clients[i]);
        u_long bytesAvailable = 0;
        if (ioctlsocket(sock, FIONREAD, &bytesAvailable) != 0) {
            closeClient(i);
            continue; // index i now holds the next client (if any)
        }

        if (bytesAvailable > 0) {
            _readCursor = i;
            return static_cast<int>(bytesAvailable);
        }

        i++;
    }
    return 0;
}

int TcpBroadcastStream::read() {
    if (_readCursor >= _clients.size()) {
        return -1;
    }

    SOCKET sock = static_cast<SOCKET>(_clients[_readCursor]);
    char byte;
    int received = recv(sock, &byte, 1, 0);
    if (received <= 0) {
        closeClient(_readCursor);
        return -1;
    }
    return static_cast<unsigned char>(byte);
}

size_t TcpBroadcastStream::write(const char* data, std::size_t len) {
    for (std::size_t i = 0; i < _clients.size();) {
        SOCKET sock = static_cast<SOCKET>(_clients[i]);
        std::size_t sent = 0;
        bool failed = false;

        while (sent < len) {
            int result = send(sock, data + sent, static_cast<int>(len - sent), 0);
            if (result <= 0) {
                failed = true;
                break;
            }
            sent += static_cast<std::size_t>(result);
        }

        if (failed) {
            closeClient(i);
            continue; // index i now holds the next client (if any)
        }
        i++;
    }
    return len;
}

void TcpBroadcastStream::closeClient(std::size_t index) {
    if (index >= _clients.size()) {
        return;
    }

    closesocket(static_cast<SOCKET>(_clients[index]));
    _clients.erase(_clients.begin() + static_cast<std::ptrdiff_t>(index));
    std::printf("Client disconnected (%d left).\n", clientCount());
}

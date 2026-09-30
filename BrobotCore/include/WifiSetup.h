#pragma once

#include <Arduino.h>

#if defined(ESP32)

#include <functional>

// Gets the ESP32 onto WiFi without ever hardcoding a network name/password
// in firmware: tries whatever credentials are saved in flash (NVS) first,
// and if there are none, or the saved ones fail to connect within a short
// timeout, falls back to serving its own "Peemo-Setup" access point with a
// tiny HTML form for entering the real network's SSID/password. Submitting
// the form saves the credentials and reboots into station mode — this
// function only returns once an actual WiFi connection is up.
namespace WifiSetup {

enum class Result : uint8_t {
    CONNECTED, // WiFi is up
    ABORTED,   // shouldAbort() returned true first — WiFi is left switched off
};

// onPortalTick, if given, is called on every iteration of the config
// portal's serve loop (not during a normal fast reconnect with already-
// working saved credentials) — this module knows nothing about Face/
// Personality/IDisplay, so it hands control back to main.cpp to keep
// something showing on screen instead of leaving it black for however long
// the portal stays open.
//
// shouldAbort, if given, is polled while waiting on a network and on every
// portal iteration; the moment it returns true, WiFi is shut down and
// ABORTED comes back. It exists for the USB link (see main.cpp): a PC that
// is already plugged in shouldn't have to sit through up to ~25s of failed
// reconnects, or a portal that never returns, before it can talk to Peemo.
Result connectOrStartPortal(const std::function<void()>& onPortalTick = nullptr,
                            const std::function<bool()>& shouldAbort = nullptr);

// Switches the radio off entirely (USB took over — see main.cpp).
void turnOff();

// Non-blocking: starts re-associating with the last network that worked and
// returns immediately; poll WiFi.status() for the outcome. The counterpart of
// turnOff(), for when USB goes away and WiFi has to come back.
void beginReconnect();

}

#endif

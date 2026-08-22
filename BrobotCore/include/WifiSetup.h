#pragma once

#include <Arduino.h>

#if defined(ESP32)

#include <functional>

// Gets the ESP32 onto WiFi without ever hardcoding a network name/password
// in firmware: tries whatever credentials are saved in flash (NVS) first,
// and if there are none, or the saved ones fail to connect within a short
// timeout, falls back to serving its own "MiMo-Setup" access point with a
// tiny HTML form for entering the real network's SSID/password. Submitting
// the form saves the credentials and reboots into station mode — this
// function only returns once an actual WiFi connection is up.
namespace WifiSetup {

// onPortalTick, if given, is called on every iteration of the config
// portal's serve loop (not during a normal fast reconnect with already-
// working saved credentials) — this module knows nothing about Face/
// Personality/IDisplay, so it hands control back to main.cpp to keep
// something showing on screen instead of leaving it black for however long
// the portal stays open.
void connectOrStartPortal(const std::function<void()>& onPortalTick = nullptr);

}

#endif

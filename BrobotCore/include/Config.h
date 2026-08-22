#pragma once

#include <Arduino.h>

// Logical display resolution. Must always match the Brobot Virtual Display's
// resolution (Brobot.Display.Abstractions on the PC side).
constexpr int LOGICAL_WIDTH = 160;
constexpr int LOGICAL_HEIGHT = 128;

#ifndef VSCREEN
#define VSCREEN 1
#endif

// Pin wiring for a physical ST7735S. Only used when VSCREEN == 0.
// Chosen for the ESP32-C3 SuperMini specifically: GPIO2/8/9 are strapping
// pins (sampled at boot — an external circuit holding one low can prevent
// boot or force download mode) and GPIO8 also drives the SuperMini's
// onboard WS2812 LED, so all three are avoided here in favor of GPIO0/1/3/4/6/10,
// which carry no boot-time role on this chip.
constexpr uint8_t TFT_CS_PIN = 10;
constexpr uint8_t TFT_RST_PIN = 1;
constexpr uint8_t TFT_DC_PIN = 3;
constexpr uint8_t TFT_SCK_PIN = 4;
constexpr uint8_t TFT_MOSI_PIN = 6;

constexpr unsigned long SERIAL_BAUD_RATE = 115200;

// TCP port the ESP32 build's WiFi protocol server listens on for FACE/MSG/
// etc. commands from Brobot.Sender — same port BrobotCore/native's dev
// server uses (and the same default Brobot.Sender's TCP field already has),
// so switching from the local dev server to a real device is just changing
// the host field.
constexpr uint16_t PROTOCOL_TCP_PORT = 5555;

// ~60 fps. NOTE: this exceeds the 115200 baud link's ~11.5 KB/s budget by
// several times over (a frame's worth of draw commands, incl. the eyes'
// rounded-corner cuts in Face.cpp, easily runs 500+ bytes) — fine for now
// since development is TCP-only via BrobotCore/native while waiting on the
// ESP32 (see CLAUDE.md), but this MUST come back down (or the baud rate
// must go up) before this is flashed to real Serial hardware again, or
// frames will lag/garble.
constexpr unsigned long FRAME_INTERVAL_MS = 16;

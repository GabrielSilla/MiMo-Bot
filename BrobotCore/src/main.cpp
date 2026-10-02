#include <Arduino.h>
#include <stdio.h>
#include <string.h>

#include "Buzzer.h"
#include "Config.h"
#include "DeviceSettings.h"
#include "Face.h"
#include "Personality.h"
#include "PongGame.h"
#include "Protocol.h"
#include "RpgBattle.h"
#include "StreamMode.h"
#if defined(ESP32)
#include "UsbLink.h"
#endif

#if VSCREEN
#include "SerialVirtualDisplay.h"
SerialVirtualDisplay display(Serial);
#else
#include "ST7735PhysicalDisplay.h"
ST7735PhysicalDisplay display(TFT_CS_PIN, TFT_DC_PIN, TFT_RST_PIN);
#endif

#if defined(ESP32)
// On real hardware, Brobot.Sender talks to Core over WiFi instead of
// Serial — .NET's SerialPort proved unreliable against the ESP32-C3
// SuperMini's native USB-CDC port (freezes documented while debugging
// this). WifiSetup::connectOrStartPortal() only returns once WiFi is up;
// see WifiSetup.cpp for the credential-entry portal it falls back to.
#include "WifiSetup.h"
#include <WiFi.h>
WiFiServer protocolServer(PROTOCOL_TCP_PORT);
WiFiClient protocolClient;
bool serverRunning = false;

// The USB port as the protocol link (see UsbLink.h for why it isn't plain
// Serial). Everything USB-related below goes through this.
UsbLink usbLink;
unsigned long lastDiagAt = 0;

// USB link. While Brobot.Sender is plugged in over USB it sends `HOST USB`
// as a keepalive and WiFi is switched off entirely (one link at a time, and
// no radio burning power/bandwidth for nothing). No keepalive for
// USB_HOST_TIMEOUT_MS brings WiFi back so the network path keeps working.
bool usbActive = false;
unsigned long lastUsbPingAt = 0;
// Whether WiFi was ever actually up this boot. If USB took over before that
// (boot-time abort — see setup()), there is nothing to "bring back": leaving
// USB means rebooting so the normal WiFi/portal boot path runs.
bool wifiWasUp = false;
// WiFi.begin() issued after USB went away; waiting for it to associate so
// the TCP server can be restarted.
bool wifiRestoring = false;

// Built once after WiFi connects, shown (see loop()) any time no PC app is
// currently connected over TCP — not just briefly at boot. Kept as a String
// at file scope, not a temporary, since FaceState::message is a non-owning
// const char*; the string is only reassigned between frames (see
// updateWaitingMessage below), so its c_str() pointer is valid whenever it
// is actually rendered.
String pcWaitingMessage;

// Rebuilt whenever WiFi (re)connects, since DHCP may hand back a different
// address. Only ever assigned between frames, so c_str() stays valid while
// it is being rendered.
void updateWaitingMessage() {
    pcWaitingMessage = "Peemo Configurado! IP: " + WiFi.localIP().toString() + ":" + String(PROTOCOL_TCP_PORT);
}

void startProtocolServer() {
    if (!serverRunning) {
        protocolServer.begin();
        serverRunning = true;
    }
}

void enterUsbMode(unsigned long now) {
    usbActive = true;
    lastUsbPingAt = now;
    wifiRestoring = false;
    if (protocolClient) {
        protocolClient.stop();
    }
    if (serverRunning) {
        protocolServer.end();
        serverRunning = false;
    }
    WifiSetup::turnOff();
}

void leaveUsbMode() {
    usbActive = false;
    if (!wifiWasUp) {
        ESP.restart();
    }
    WifiSetup::beginReconnect();
    wifiRestoring = true;
}

// Whether any PC app has connected at all since this boot. The waiting
// screen above exists purely to get a *first* connection going -- once
// that's happened, a later disconnect (Brobot.Sender's tray "Sair", a
// Windows shutdown/logoff, or just a dropped link) should look like the
// device actually switched off, not loop back to "please connect" forever.
// See loop()'s render section.
bool everConnected = false;
#endif

Personality personality;
DeviceSettings deviceSettings;
PongGame pongGame;
RpgBattle rpgBattle;
StreamMode streamMode;
Buzzer buzzer;
Protocol protocol(personality, deviceSettings, pongGame, rpgBattle, streamMode, buzzer);

unsigned long lastFrameAt = 0;

// Tracks the last expression a sound was triggered for, so
// buzzer.playForExpression only fires on an actual *change* — see
// Buzzer::playForExpression's own comment for why calling it every frame
// would break both one-shot and looping cues.
Expression lastSoundExpression = Expression::NEUTRAL;
// Tracks deviceSettings.soundEnabled() across frames so a SOUND OFF -> ON
// toggle re-triggers the current expression's cue even when the expression
// itself hasn't changed (an expression-only check would otherwise stay
// silent until the next expression change came along on its own).
bool lastSoundEnabled = true;

// Shared by both the ESP32 (pcConnected) and non-ESP32 render paths below —
// factored out so the expression-change/sound-trigger logic only lives in
// one place instead of being duplicated per platform.
void renderPersonalityFrame(unsigned long now) {
    FaceState state = personality.currentState();
    bool soundEnabled = deviceSettings.soundEnabled();
    if (!soundEnabled) {
        buzzer.mute();
    } else if (state.expression != lastSoundExpression || !lastSoundEnabled) {
        buzzer.playForExpression(state.expression, now);
    }
    lastSoundExpression = state.expression;
    lastSoundEnabled = soundEnabled;
    Face::render(display, state);
}

void setup() {
#if defined(ESP32)
    // Receive ring for the USB link. Bytes that arrive while it is full are
    // dropped (unlike TCP, where the kernel holds them and flow-controls the
    // sender), and loop() is busy for a while at a time pushing the canvas over
    // SPI while a FRAME payload — up to ~41KB — streams in. Must be sized for
    // every FRAME the PC keeps in flight at once (see Config.h).
    usbLink.begin(USB_RX_BUFFER_BYTES);
#else
    Serial.begin(SERIAL_BAUD_RATE);
#endif
    randomSeed(analogRead(A0));

#if !VSCREEN
    display.begin();
#endif

    buzzer.begin();

#if defined(ESP32)
    WifiSetup::Result wifiResult = WifiSetup::connectOrStartPortal([&]() {
        // Built directly, bypassing Personality entirely — its FINISHED
        // message auto-clears ~10s after typing finishes (intended for the
        // "Pensamentos da IA" Terminei! status, see CLAUDE.md), which isn't
        // what's wanted for a setup screen that needs to stay up for
        // however long it takes someone to find the "Peemo-Setup" network
        // and fill in the form. A plain FaceState with the full text set
        // has no typing/expiry timers to fight — it just always shows.
        unsigned long now = millis();
        if (now - lastFrameAt >= FRAME_INTERVAL_MS) {
            lastFrameAt = now;
            FaceState portalState;
            portalState.expression = Expression::FINISHED;
            portalState.message = "Acesse a rede Peemo-Setup. http://192.168.4.1 para configurar";
            portalState.nowMs = now;
            display.clear(0, 0, 0);
            Face::render(display, portalState);
            display.present();
        }
    }, []() {
        // Any byte on USB means a PC is already there — no point burning
        // ~25s on networks, or sitting in the portal, before it can talk to
        // Peemo. The bytes stay buffered for loop() to read.
        return usbLink.available() > 0;
    });

    if (wifiResult == WifiSetup::Result::ABORTED) {
        usbActive = true;
        lastUsbPingAt = millis();
    } else {
        wifiWasUp = true;
        startProtocolServer();

        usbLink.print("WiFi connected, IP: ");
        usbLink.println(WiFi.localIP());
    }

    // Rendered every frame by loop() for as long as no PC app is connected
    // (see below) — not just briefly at boot — so the IP:port stays legible
    // on the physical screen for however long it takes to open Brobot.Sender
    // and fill in the Conexão card, no Serial monitor needed. Same "IP:porta"
    // shape as that card's own text field, so it can be typed in verbatim.
    if (wifiWasUp) {
        updateWaitingMessage();
    }
#endif

    // Last thing before loop() takes over, on every path (portal or not) —
    // this is what _bootStartedAt anchors the eyes-falling-into-place boot
    // animation to (see Personality::update), so it has to line up with
    // when frames actually start rendering, not with whatever came before
    // (the multi-second IP screen above, or the portal, would otherwise eat
    // the whole animation window before anyone ever saw it).
    personality.begin(millis());
}

void loop() {
    unsigned long now = millis();

#if defined(ESP32)
    if (!usbActive && serverRunning && (!protocolClient || !protocolClient.connected())) {
        protocolClient = protocolServer.available();
        if (protocolClient) {
            // Nagle (on by default) batches small writes hoping to
            // coalesce with more outgoing data — invisible for the usual
            // fire-and-forget FACE/MSG/PONG KEY commands (never waited on),
            // but stalls a tight request-response exchange like STREAM/
            // FRAME's one-frame-in-flight FRAMEOK ack (see PROTOCOL.md) by
            // tens to hundreds of ms per round trip. Brobot.Connection
            // disables it on its side of this same link for the same
            // reason (see BrobotConnection.cs's ConnectTcp).
            protocolClient.setNoDelay(true);
        }
    }
    bool tcpConnected = !usbActive && protocolClient && protocolClient.connected();
    if (tcpConnected) {
        protocol.poll(protocolClient, now);
        protocol.takeHostUsbPing(); // only meaningful on the USB link itself
    }

    // Always listened to, WiFi up or not: that is how a PC that just plugged
    // in gets its first PING answered and gets to send `HOST USB`.
    if (usbLink.available() > 0) {
        protocol.poll(usbLink, now);
        if (protocol.takeHostUsbPing()) {
            if (!usbActive) {
                enterUsbMode(now);
            }
            lastUsbPingAt = now;
        }
    }
    if (usbActive && now - lastUsbPingAt > USB_HOST_TIMEOUT_MS) {
        leaveUsbMode();
    }
    // Every few seconds over USB: a line the PC app writes to its log, so a
    // stalling stream can be told apart (buffer never allocated, frames
    // abandoned, rows dropped, heap exhausted) without a serial monitor.
    if (usbActive && now - lastDiagAt >= 5000) {
        lastDiagAt = now;
        // up= is millis() since boot: the PC app watches it go backwards to
        // notice a Core reboot that never dropped the USB port (the C3's
        // USB-Serial-JTAG can survive a CPU reset) and resend THEME/CLASSICCOLOR.
        usbLink.printf("DIAG up=%lu rx=%u heap=%u maxblock=%u abandoned=%u dropped=%u\n",
                      now, (unsigned)usbLink.rxCapacity(), (unsigned)ESP.getFreeHeap(), (unsigned)ESP.getMaxAllocHeap(),
                      (unsigned)protocol.abandonedFrames(), (unsigned)streamMode.droppedRows());
    }
    if (wifiRestoring && WiFi.status() == WL_CONNECTED) {
        wifiRestoring = false;
        startProtocolServer();
        updateWaitingMessage();
    }

    bool pcConnected = usbActive || tcpConnected;
    if (pcConnected) {
        everConnected = true;
    }
    // Where replies and spontaneous lines (PONG OVER, ...) go.
    Stream& hostStream = usbActive ? static_cast<Stream&>(usbLink) : static_cast<Stream&>(protocolClient);
#else
    protocol.poll(Serial, now);
#endif

#if !VSCREEN
    // Drained every loop() iteration — independent of the FRAME_INTERVAL_MS
    // gate below — so streamed rows land on the canvas with as little
    // latency as possible instead of waiting for the next render tick. Only
    // meaningful against the physical display, which is the only IDisplay
    // implementation with a canvas to write into (see StreamMode.h).
    while (streamMode.hasPendingRow()) {
        uint8_t row;
        const uint16_t* pixels;
        streamMode.takePendingRow(row, pixels);
        display.writeStreamRow(row, pixels);
    }
#endif

    // Pong, RPG Battle and STREAM are all exclusive: while any one is active
    // it replaces Personality's own update entirely rather than adding
    // another priority tier, so nothing (not even the idle blink/look-
    // around/sleep timers, let alone a NOTIFY) advances or interrupts it —
    // see PongGame.h/RpgBattle.h/StreamMode.h. Protocol::dispatch is what
    // keeps the three from ever being active at once, so this is a plain
    // either/or/or, never more than one.
    if (pongGame.isActive()) {
        pongGame.update(now);
    } else if (rpgBattle.isActive()) {
        rpgBattle.update(now);
    } else if (streamMode.isActive()) {
        streamMode.update(now);
    } else {
        personality.update(now);
    }
    buzzer.update(now);

    // Fired exactly once, the instant a round/battle ends by the Core's own
    // decision — tells whichever PC app is connected to stop listening to
    // the keyboard (see PROTOCOL.md's PONG OVER / RPG OVER). Written
    // straight to the active stream, the same "reply directly, don't route
    // through Personality" idiom Protocol::dispatch already uses for the
    // PING reply. A PONG/RPG STOP never reaches here — both classes'
    // stop() deliberately never set justEnded(), since main.cpp already
    // knows why in that case (it just sent the STOP itself).
    if (pongGame.justEnded()) {
        char overLine[24];
        snprintf(overLine, sizeof(overLine), "PONG OVER %d", pongGame.lastScore());
#if defined(ESP32)
        if (pcConnected) {
            hostStream.println(overLine);
            hostStream.println("PRESENT");
        }
#else
        Serial.println(overLine);
        Serial.println("PRESENT");
#endif
    }
    if (rpgBattle.justEnded()) {
        // RPG battles bypass Personality/Expression entirely (see
        // RpgBattle.h), so this is the one place that can know "the battle
        // was just won" — playForExpression's switch never sees it. Gated
        // on soundEnabled() the same way renderPersonalityFrame gates every
        // other cue.
        if (deviceSettings.soundEnabled() && strcmp(rpgBattle.lastResultToken(), "VICTORY") == 0) {
            buzzer.playRpgVictory(now);
        }
        char overLine[24];
        snprintf(overLine, sizeof(overLine), "RPG OVER %s", rpgBattle.lastResultToken());
#if defined(ESP32)
        if (pcConnected) {
            hostStream.println(overLine);
            hostStream.println("PRESENT");
        }
#else
        Serial.println(overLine);
        Serial.println("PRESENT");
#endif
    }

#if !VSCREEN
    // Cheap bool set — fine to do every loop() iteration rather than only
    // on change, and keeps this in the one place that already knows the
    // concrete ST7735PhysicalDisplay type (setScanlinesEnabled isn't part
    // of IDisplay — see its own comment).
    display.setScanlinesEnabled(deviceSettings.scanlinesEnabled());
#endif

    // While a FRAME payload is still arriving, StreamMode's canvas rows are
    // a mix of this transfer's already-applied rows and whichever rows
    // haven't been reached yet — pushing that to the physical panel now
    // would show as tearing (part of the picture already updated, part
    // still the previous frame). Holding off present() until the transfer
    // finishes avoids ever showing that intermediate state; lastFrameAt is
    // deliberately left untouched so this re-checks (and, once the transfer
    // completes, presents) on the very next loop() iteration rather than
    // waiting out another full FRAME_INTERVAL_MS on top of the transfer.
    bool streamActive = streamMode.isActive();
    bool midFrameTransfer = streamActive && protocol.isReceivingFrame();

    if (!midFrameTransfer && now - lastFrameAt >= FRAME_INTERVAL_MS) {
        lastFrameAt = now;

        // Normally cleared every frame like always. While STREAM is active,
        // clearing is skipped except the one frame right after STREAM START
        // (consumeNeedsCanvasClear() is edge-triggered — true exactly once)
        // — the canvas is "cleared once, then only receives stream data"
        // per PROTOCOL.md, since streamed rows accumulate across frames
        // rather than being redrawn from scratch each time.
        if (!streamActive || streamMode.consumeNeedsCanvasClear()) {
            display.clear(0, 0, 0);
        }

        if (pongGame.isActive()) {
            // Exclusive, same bypass shape as the branches below: a round
            // in progress owns the whole frame, Personality/Face never get
            // a look-in until it ends.
            pongGame.render(display);
        } else if (rpgBattle.isActive()) {
            rpgBattle.render(display, now);
        } else if (streamActive) {
            // No-op: rows were already pushed straight into the canvas by
            // the drain loop above, independent of this render tick —
            // present() below just flushes whatever's accumulated there.
        } else {
#if defined(ESP32)
            if (!pcConnected && !everConnected) {
                // Still waiting for the very first connection since boot —
                // show the persistent IP message instead of Personality's own
                // idle face, bypassing Personality entirely (same trick the
                // config-portal screen in setup() uses): this has to stay up
                // indefinitely, and with no PC connected there's no FACE/MSG
                // command that could arrive to drive Personality's own
                // message system anyway.
                FaceState waitingState;
                waitingState.expression = Expression::FINISHED;
                waitingState.message = pcWaitingMessage.c_str();
                waitingState.nowMs = now;
                Face::render(display, waitingState);
            } else if (!pcConnected) {
                // A PC app has connected before and just isn't right now —
                // Brobot.Sender already said its goodbye over NOTIFY BYE
                // while the link was still open (see MainWindow's
                // SendFarewellAndWait), so by the time the disconnect
                // actually lands here the screen should just go dark, the
                // same as a real device switched off, rather than resurrect
                // the "please connect" screen every single time. Nothing to
                // draw — display.clear() above already left the frame black.
            } else {
                renderPersonalityFrame(now);
            }
#else
            renderPersonalityFrame(now);
#endif
        }
        display.present();
    }
}

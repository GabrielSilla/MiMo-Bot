#include <Arduino.h>

#include "Config.h"
#include "Face.h"
#include "Personality.h"
#include "Protocol.h"

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
#endif

Personality personality;
Protocol protocol(personality);

unsigned long lastFrameAt = 0;

void setup() {
    Serial.begin(SERIAL_BAUD_RATE);
    randomSeed(analogRead(A0));

#if !VSCREEN
    display.begin();
#endif

#if defined(ESP32)
    WifiSetup::connectOrStartPortal([&]() {
        // Built directly, bypassing Personality entirely — its FINISHED
        // message auto-clears ~10s after typing finishes (intended for the
        // "Pensamentos da IA" Terminei! status, see CLAUDE.md), which isn't
        // what's wanted for a setup screen that needs to stay up for
        // however long it takes someone to find the "MiMo-Setup" network
        // and fill in the form. A plain FaceState with the full text set
        // has no typing/expiry timers to fight — it just always shows.
        unsigned long now = millis();
        if (now - lastFrameAt >= FRAME_INTERVAL_MS) {
            lastFrameAt = now;
            FaceState portalState;
            portalState.expression = Expression::FINISHED;
            portalState.message = "Acesse a rede MiMo-Setup. http://192.168.4.1 para configurar";
            portalState.nowMs = now;
            display.clear(0, 0, 0);
            Face::render(display, portalState);
            display.present();
        }
    });

    protocolServer.begin();

    Serial.print("WiFi connected, IP: ");
    Serial.println(WiFi.localIP());

    // Briefly show the IP on the physical screen too, so Brobot.Sender's
    // TCP host field can be filled in without needing a Serial monitor.
    display.clear(0, 0, 0);
    display.drawText("MiMo IP:", 10, 50, 255, 255, 255);
    display.drawText(WiFi.localIP().toString().c_str(), 10, 65, 100, 220, 180);
    display.present();
    delay(3000);
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
    if (!protocolClient || !protocolClient.connected()) {
        protocolClient = protocolServer.available();
    }
    if (protocolClient && protocolClient.connected()) {
        protocol.poll(protocolClient, now);
    }
#else
    protocol.poll(Serial, now);
#endif

    personality.update(now);

    if (now - lastFrameAt >= FRAME_INTERVAL_MS) {
        lastFrameAt = now;
        display.clear(0, 0, 0);
        Face::render(display, personality.currentState());
        display.present();
    }
}

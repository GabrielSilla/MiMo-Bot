#include "WifiSetup.h"

#if defined(ESP32)

#include <Preferences.h>
#include <WebServer.h>
#include <WiFi.h>

namespace {

constexpr const char* kPrefsNamespace = "wifi";
constexpr const char* kApSsid = "MiMo-Setup";
constexpr unsigned long kConnectTimeoutMs = 15000;

bool tryConnectSaved() {
    Preferences prefs;
    prefs.begin(kPrefsNamespace, /*readOnly=*/true);
    String ssid = prefs.getString("ssid", "");
    String password = prefs.getString("pass", "");
    prefs.end();

    if (ssid.isEmpty()) {
        return false;
    }

    WiFi.mode(WIFI_STA);
    WiFi.begin(ssid.c_str(), password.c_str());

    unsigned long start = millis();
    while (WiFi.status() != WL_CONNECTED && millis() - start < kConnectTimeoutMs) {
        delay(250);
    }

    return WiFi.status() == WL_CONNECTED;
}

void saveCredentials(const String& ssid, const String& password) {
    Preferences prefs;
    prefs.begin(kPrefsNamespace, /*readOnly=*/false);
    prefs.putString("ssid", ssid);
    prefs.putString("pass", password);
    prefs.end();
}

String htmlEscape(const String& input) {
    String out;
    out.reserve(input.length());
    for (size_t i = 0; i < input.length(); i++) {
        char c = input[i];
        if (c == '&') out += "&amp;";
        else if (c == '<') out += "&lt;";
        else if (c == '>') out += "&gt;";
        else if (c == '"') out += "&quot;";
        else out += c;
    }
    return out;
}

// Scans for nearby networks and renders them as a <select> so the SSID
// doesn't have to be typed by hand (easy to mistype/miscapitalize a real
// network name). Re-scans on every page load rather than once at portal
// startup, since the AP can stay up for as long as it takes someone to find
// their phone and connect — a stale scan from minutes earlier isn't
// noticeably faster and could be wrong if networks appeared/disappeared.
String buildSetupPage() {
    int networkCount = WiFi.scanNetworks();
    String options;
    String seenSsids = ",";
    for (int i = 0; i < networkCount; i++) {
        String ssid = WiFi.SSID(i);
        if (ssid.isEmpty()) {
            continue; // hidden network — has to go through the manual field
        }
        String marker = "," + ssid + ",";
        if (seenSsids.indexOf(marker) >= 0) {
            continue; // same network seen again (mesh/multiple APs) — first RSSI wins
        }
        seenSsids += ssid + ",";

        String escaped = htmlEscape(ssid);
        options += "<option value='" + escaped + "'>" + escaped + " (" + String(WiFi.RSSI(i)) + " dBm)</option>";
    }
    WiFi.scanDelete();

    return String(
        "<html><body style='font-family:sans-serif'>"
        "<h2>Configurar WiFi do MiMo</h2>"
        "<form method='POST' action='/save'>"
        "Rede:<br>"
        "<select name='ssid_select'>") + options + String("</select><br><br>"
        "Rede n\xC3\xA3o apareceu? Digite: <input name='ssid_manual'><br><br>"
        "Senha: <input name='pass' type='password'><br><br>"
        "<button type='submit'>Salvar e conectar</button>"
        "</form></body></html>");
}

// Never returns — the only way out is saving new credentials, which reboots
// straight into WifiSetup::connectOrStartPortal() again via setup().
void runConfigPortal(const std::function<void()>& onPortalTick) {
    WiFi.mode(WIFI_AP_STA); // AP_STA (not plain AP) so scanNetworks() still works while serving the portal
    WiFi.softAP(kApSsid);

    WebServer server(80);

    server.on("/", HTTP_GET, [&server]() {
        server.send(200, "text/html", buildSetupPage());
    });

    server.on("/save", HTTP_POST, [&server]() {
        String ssid = server.arg("ssid_manual");
        if (ssid.isEmpty()) {
            ssid = server.arg("ssid_select");
        }
        String password = server.arg("pass");
        saveCredentials(ssid, password);
        server.send(200, "text/html",
            "<html><body style='font-family:sans-serif'>"
            "<h2>Salvo! Reiniciando...</h2></body></html>");
        delay(1000);
        ESP.restart();
    });

    server.begin();

    while (true) {
        server.handleClient();
        if (onPortalTick) {
            onPortalTick();
        }
    }
}

} // namespace

namespace WifiSetup {

void connectOrStartPortal(const std::function<void()>& onPortalTick) {
    if (tryConnectSaved()) {
        return;
    }

    // No saved credentials, or the saved ones didn't work (wrong password,
    // router out of range, etc.) — either way the only way forward is
    // someone entering the real ones via the portal.
    runConfigPortal(onPortalTick);
}

} // namespace WifiSetup

#endif

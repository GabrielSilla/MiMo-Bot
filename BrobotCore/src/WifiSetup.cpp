#include "WifiSetup.h"

#if defined(ESP32)

#include <Preferences.h>
#include <WebServer.h>
#include <WiFi.h>
#include <vector>

namespace {

constexpr const char* kPrefsNamespace = "wifi";
constexpr const char* kApSsid = "Peemo-Setup";
// Per-network attempt — kept short since a boot can walk through up to
// kMaxSavedNetworks of these before falling back to the portal.
constexpr unsigned long kConnectTimeoutMs = 5000;
// Remembers the last few networks Peemo has actually connected to (most
// recent first) rather than just one, since it moves between a small set of
// known places (home, work, ...) rather than staying on a single network.
constexpr uint8_t kMaxSavedNetworks = 5;

struct SavedNetwork {
    String ssid;
    String password;
};

std::vector<SavedNetwork> loadSavedNetworks() {
    Preferences prefs;
    prefs.begin(kPrefsNamespace, /*readOnly=*/true);
    uint8_t count = prefs.getUChar("count", 0);
    if (count > kMaxSavedNetworks) {
        count = kMaxSavedNetworks;
    }

    std::vector<SavedNetwork> networks;
    networks.reserve(count);
    for (uint8_t i = 0; i < count; i++) {
        String ssid = prefs.getString(("ssid" + String(i)).c_str(), "");
        String password = prefs.getString(("pass" + String(i)).c_str(), "");
        networks.push_back({ssid, password});
    }
    prefs.end();
    return networks;
}

void persistSavedNetworks(const std::vector<SavedNetwork>& networks) {
    Preferences prefs;
    prefs.begin(kPrefsNamespace, /*readOnly=*/false);
    uint8_t count = networks.size() < kMaxSavedNetworks ? static_cast<uint8_t>(networks.size()) : kMaxSavedNetworks;
    prefs.putUChar("count", count);
    for (uint8_t i = 0; i < count; i++) {
        prefs.putString(("ssid" + String(i)).c_str(), networks[i].ssid);
        prefs.putString(("pass" + String(i)).c_str(), networks[i].password);
    }
    prefs.end();
}

// Moves ssid to the front of the list (inserting it if it isn't already
// there), evicting the oldest entry once there are more than
// kMaxSavedNetworks — this is what turns the list into "last N networks
// connected to" rather than just "every network ever entered".
void rememberNetwork(std::vector<SavedNetwork>& networks, const String& ssid, const String& password) {
    for (size_t i = 0; i < networks.size(); i++) {
        if (networks[i].ssid == ssid) {
            networks.erase(networks.begin() + i);
            break;
        }
    }
    networks.insert(networks.begin(), {ssid, password});
    if (networks.size() > kMaxSavedNetworks) {
        networks.resize(kMaxSavedNetworks);
    }
}

// Tries each saved network in most-recent-first order, one kConnectTimeoutMs
// attempt each. A hit anywhere but the front promotes that network back to
// most-recent, so a place Peemo visits often naturally floats to the top of
// the list (and survives longest once the list fills up).
//
// Returns 1 on connect, 0 when every network failed, -1 when shouldAbort()
// fired (WiFi already shut off by then).
int tryConnectSavedNetworks(const std::function<bool()>& shouldAbort) {
    std::vector<SavedNetwork> networks = loadSavedNetworks();
    if (networks.empty()) {
        return 0;
    }

    WiFi.mode(WIFI_STA);
    // Modem sleep (the default) dozes the radio between the AP's DTIM
    // beacons and only wakes it on that schedule — invisible for the
    // occasional FACE/MSG line, but it's what was silently capping STREAM/
    // FRAME's round trip to well under what the raw data volume needs (see
    // specs/sender-gba.md's Phase 0 measurement and PROTOCOL.md's STREAM/
    // FRAME section) even after fixing Nagle on both ends of the TCP link.
    // Always-on costs continuous power a battery-powered board couldn't
    // afford, but Peemo is mains-powered, so there's nothing to trade off.
    WiFi.setSleep(false);
    for (size_t i = 0; i < networks.size(); i++) {
        const SavedNetwork& network = networks[i];
        WiFi.begin(network.ssid.c_str(), network.password.c_str());

        unsigned long start = millis();
        while (WiFi.status() != WL_CONNECTED && millis() - start < kConnectTimeoutMs) {
            if (shouldAbort && shouldAbort()) {
                WiFi.disconnect(true);
                WiFi.mode(WIFI_OFF);
                return -1;
            }
            delay(50);
        }

        if (WiFi.status() == WL_CONNECTED) {
            if (i != 0) {
                rememberNetwork(networks, network.ssid, network.password);
                persistSavedNetworks(networks);
            }
            return 1;
        }

        WiFi.disconnect();
    }

    return 0;
}

void saveCredentials(const String& ssid, const String& password) {
    std::vector<SavedNetwork> networks = loadSavedNetworks();
    rememberNetwork(networks, ssid, password);
    persistSavedNetworks(networks);
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
        "<h2>Configurar WiFi do Peemo</h2>"
        "<form method='POST' action='/save'>"
        "Rede:<br>"
        "<select name='ssid_select'>") + options + String("</select><br><br>"
        "Rede n\xC3\xA3o apareceu? Digite: <input name='ssid_manual'><br><br>"
        "Senha: <input name='pass' type='password'><br><br>"
        "<button type='submit'>Salvar e conectar</button>"
        "</form></body></html>");
}

// Only leaves two ways: saving new credentials (reboots straight into
// WifiSetup::connectOrStartPortal() again via setup()), or shouldAbort()
// firing, which tears the portal down and returns.
void runConfigPortal(const std::function<void()>& onPortalTick, const std::function<bool()>& shouldAbort) {
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

    while (!(shouldAbort && shouldAbort())) {
        server.handleClient();
        if (onPortalTick) {
            onPortalTick();
        }
    }

    server.stop();
    WiFi.softAPdisconnect(true);
    WiFi.mode(WIFI_OFF);
}

} // namespace

namespace WifiSetup {

Result connectOrStartPortal(const std::function<void()>& onPortalTick, const std::function<bool()>& shouldAbort) {
    int saved = tryConnectSavedNetworks(shouldAbort);
    if (saved > 0) {
        return Result::CONNECTED;
    }
    if (saved < 0) {
        return Result::ABORTED;
    }

    // No saved credentials, or the saved ones didn't work (wrong password,
    // router out of range, etc.) — either way the only way forward is
    // someone entering the real ones via the portal (or a USB host showing up).
    runConfigPortal(onPortalTick, shouldAbort);
    return Result::ABORTED;
}

void turnOff() {
    WiFi.disconnect(true);
    WiFi.mode(WIFI_OFF);
}

void beginReconnect() {
    WiFi.mode(WIFI_STA);
    WiFi.setSleep(false); // same reasoning as tryConnectSavedNetworks
    WiFi.setAutoReconnect(true);
    // No arguments: re-uses the credentials the WiFi driver itself persisted
    // the last time begin(ssid, pass) succeeded, i.e. the network that was
    // up when USB took over.
    WiFi.begin();
}

} // namespace WifiSetup

#endif

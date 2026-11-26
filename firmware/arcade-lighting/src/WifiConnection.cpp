#ifdef ARCADE_NETWORK

#include <Arduino.h>
#include <WiFi.h>

#include "WifiConnection.h"
#if __has_include("WifiCredentials.h")
#include "WifiCredentials.h"
#else
#define ARCADE_WIFI_SSID ""
#define ARCADE_WIFI_PASSWORD ""
#endif

void WifiConnection::begin() {
    if (ARCADE_WIFI_SSID[0] == '\0') {
        Serial.println("Wi-Fi credentials missing. See WifiCredentials.example.h.");
        return;
    }

    WiFi.persistent(false);
    WiFi.onEvent([](WiFiEvent_t, WiFiEventInfo_t info) {
        const IPAddress ip(info.got_ip.ip_info.ip.addr);
        Serial.printf("Wi-Fi IP: %s\n", ip.toString().c_str());
    }, ARDUINO_EVENT_WIFI_STA_GOT_IP);
    WiFi.mode(WIFI_STA);
    WiFi.setAutoReconnect(true);
    WiFi.begin(ARCADE_WIFI_SSID, ARCADE_WIFI_PASSWORD);
}

#endif

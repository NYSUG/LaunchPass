#ifdef ARCADE_NETWORK

#include <Arduino.h>
#include <ArduinoJson.h>
#include <WiFi.h>
#include <esp_http_server.h>
#include <lwip/sockets.h>
#include <ctype.h>
#include <strings.h>

#include "HttpApi.h"
#include "ZoneControl.h"

namespace {
using namespace LightingConfig;
using Effects::Type;
constexpr char kVersion[] = "0.2.0";
constexpr char kZonePrefix[] = "/api/zones/";
constexpr size_t kMaxBody = 2048;

esp_err_t error(httpd_req_t* req, const char* status, const char* message) {
    JsonDocument doc;
    doc["error"] = message;
    String body;
    serializeJson(doc, body);
    httpd_resp_set_status(req, status);
    httpd_resp_set_type(req, "application/json");
    return httpd_resp_send(req, body.c_str(), body.length());
}

esp_err_t sendJson(httpd_req_t* req, const JsonDocument& doc,
                   const char* status = "200 OK") {
    String body;
    serializeJson(doc, body);
    httpd_resp_set_status(req, status);
    httpd_resp_set_type(req, "application/json");
    return httpd_resp_send(req, body.c_str(), body.length());
}

bool lanPeer(httpd_req_t* req) {
    sockaddr_storage peer{};
    socklen_t size = sizeof(peer);
    if (WiFi.status() != WL_CONNECTED ||
        getpeername(httpd_req_to_sockfd(req), reinterpret_cast<sockaddr*>(&peer), &size) != 0) {
        return false;
    }

    uint32_t address;
    if (peer.ss_family == AF_INET) {
        address = reinterpret_cast<const sockaddr_in&>(peer).sin_addr.s_addr;
    } else if (peer.ss_family == AF_INET6) {
        const auto& ipv6 = reinterpret_cast<const sockaddr_in6&>(peer).sin6_addr;
        constexpr uint8_t mappedPrefix[] = {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xff};
        if (memcmp(ipv6.s6_addr, mappedPrefix, sizeof(mappedPrefix)) != 0) {
            return false;
        }
        memcpy(&address, ipv6.s6_addr + sizeof(mappedPrefix), sizeof(address));
    } else {
        return false;
    }

    const uint32_t mask = WiFi.subnetMask();
    const uint32_t local = WiFi.localIP();
    return mask != 0 && (address & mask) == (local & mask);
}

struct JsonReader {
    const char* next;
    const char* end;

    int read() { return next == end ? -1 : static_cast<unsigned char>(*next++); }

    size_t readBytes(char* buffer, size_t length) {
        const size_t available = end - next;
        const size_t count = length < available ? length : available;
        memcpy(buffer, next, count);
        next += count;
        return count;
    }
};

enum class BodyResult { Valid, Rejected, ReceiveFailed };

BodyResult readJson(httpd_req_t* req, JsonDocument& doc) {
    if (req->content_len == 0 || req->content_len > kMaxBody) {
        error(req, req->content_len ? "413 Payload Too Large" : "400 Bad Request",
              "expected a JSON body of 1 to 2048 bytes");
        return BodyResult::Rejected;
    }

    char contentType[80];
    if (httpd_req_get_hdr_value_str(req, "Content-Type", contentType, sizeof(contentType)) != ESP_OK ||
        strncasecmp(contentType, "application/json", 16) != 0 ||
        (contentType[16] != '\0' && contentType[16] != ';')) {
        error(req, "415 Unsupported Media Type", "use Content-Type: application/json");
        return BodyResult::Rejected;
    }

    char body[kMaxBody];
    size_t received = 0;
    const uint32_t started = millis();
    while (received < req->content_len) {
        const int count = httpd_req_recv(req, body + received, req->content_len - received);
        if (count <= 0 || millis() - started > 2000) {
            error(req, "408 Request Timeout", "incomplete request body");
            return BodyResult::ReceiveFailed;
        }
        received += count;
    }

    JsonReader reader{body, body + received};
    const auto result = deserializeJson(doc, reader, DeserializationOption::NestingLimit(5));
    while (reader.next != reader.end && isspace(static_cast<unsigned char>(*reader.next))) {
        ++reader.next;
    }
    if (result || !doc.is<JsonObject>() || reader.next != reader.end) {
        error(req, "400 Bad Request", "expected one JSON object");
        return BodyResult::Rejected;
    }
    return BodyResult::Valid;
}

int findZone(JsonString name) {
    for (int zone = 0; zone < kZoneCount; ++zone) {
        if (name == JsonString(kZones[zone].name)) {
            return zone;
        }
    }
    return -1;
}

const char* effectName(Type type) {
    switch (type) {
        case Type::Off: return "off";
        case Type::Solid: return "solid";
        case Type::Breathe: return "breathe";
        case Type::Chase: return "chase";
    }
    return "off";
}

bool parseEffect(JsonVariantConst value, Effects::Settings& settings) {
    const auto object = value.as<JsonObjectConst>();
    const auto effect = object["effect"].as<JsonString>();
    Type type;
    if (effect == JsonString("off")) {
        type = Type::Off;
    } else if (effect == JsonString("solid")) {
        type = Type::Solid;
    } else if (effect == JsonString("breathe")) {
        type = Type::Breathe;
    } else if (effect == JsonString("chase")) {
        type = Type::Chase;
    } else {
        return false;
    }

    const bool animated = type == Type::Breathe || type == Type::Chase;
    settings = {type, type == Type::Off ? CRGB::Black : CRGB::White,
                type == Type::Breathe ? 2000UL : (animated ? 250UL : 0UL),
                static_cast<uint8_t>(type == Type::Off ? 0 : 255)};
    for (JsonPairConst field : object) {
        const auto key = field.key();
        const auto parameter = field.value();
        if (key == JsonString("effect")) {
            continue;
        }
        if (key == JsonString("color") && type != Type::Off) {
            const auto rgb = parameter.as<JsonArrayConst>();
            if (rgb.size() != 3 || !rgb[0].is<uint8_t>() ||
                !rgb[1].is<uint8_t>() || !rgb[2].is<uint8_t>()) {
                return false;
            }
            settings.color = CRGB(rgb[0].as<uint8_t>(), rgb[1].as<uint8_t>(), rgb[2].as<uint8_t>());
        } else if (key == JsonString("brightness") && type != Type::Off && parameter.is<uint8_t>()) {
            settings.brightness = parameter.as<uint8_t>();
        } else if (key == JsonString("speed_ms") && animated && parameter.is<uint32_t>()) {
            const auto speed = parameter.as<uint32_t>();
            if (speed < 20 || speed > 60000) {
                return false;
            }
            settings.speedMs = speed;
        } else {
            return false;
        }
    }
    return true;
}

esp_err_t status(httpd_req_t* req) {
    JsonDocument doc;
    doc["firmware_version"] = kVersion;
    doc["uptime_ms"] = static_cast<uint64_t>(esp_timer_get_time()) / 1000;
    doc["ip_address"] = WiFi.localIP().toString();
    doc["wifi_status"] = WiFi.status() == WL_CONNECTED ? "connected" : "disconnected";
    int enabled = 0;
    for (const auto& zone : kZones) {
        enabled += zone.enabled;
    }
    doc["enabled_zone_count"] = enabled;
    return sendJson(req, doc);
}

esp_err_t zones(httpd_req_t* req) {
    Effects::Settings effects[kZoneCount];
    ZoneControl::snapshot(effects);
    JsonDocument doc;
    auto result = doc["zones"].to<JsonArray>();
    for (int i = 0; i < kZoneCount; ++i) {
        auto item = result.add<JsonObject>();
        item["name"] = kZones[i].name;
        item["gpio"] = kZones[i].pin;
        item["led_count"] = kZones[i].count;
        item["enabled"] = kZones[i].enabled;
        item["reverse"] = kZones[i].reverse;
        const auto& effect = effects[i];
        item["effect"] = effectName(effect.type);
        if (effect.type != Type::Off) {
            auto color = item["color"].to<JsonArray>();
            color.add(effect.color.r);
            color.add(effect.color.g);
            color.add(effect.color.b);
            item["brightness"] = effect.brightness;
        }
        if (effect.type == Type::Breathe || effect.type == Type::Chase) {
            item["speed_ms"] = effect.speedMs;
        }
    }
    return sendJson(req, doc);
}

esp_err_t queued(httpd_req_t* req, const ZoneControl::Update (&updates)[kZoneCount]) {
    JsonDocument doc;
    doc["revision"] = ZoneControl::queue(updates);
    doc["accepted"] = true;
    return sendJson(req, doc, "202 Accepted");
}

esp_err_t updateZone(httpd_req_t* req) {
    const int zone = findZone(JsonString(req->uri + sizeof(kZonePrefix) - 1));
    if (zone < 0) {
        return error(req, "404 Not Found", "unknown zone");
    }
    if (!kZones[zone].enabled) {
        return error(req, "409 Conflict", "zone is disabled");
    }

    JsonDocument doc;
    const auto body = readJson(req, doc);
    if (body != BodyResult::Valid) {
        return body == BodyResult::Rejected ? ESP_OK : ESP_FAIL;
    }
    ZoneControl::Update updates[kZoneCount] = {};
    if (!parseEffect(doc.as<JsonVariantConst>(), updates[zone].effect)) {
        return error(req, "400 Bad Request", "invalid effect or parameters");
    }
    updates[zone].included = true;
    return queued(req, updates);
}

esp_err_t scene(httpd_req_t* req) {
    JsonDocument doc;
    const auto body = readJson(req, doc);
    if (body != BodyResult::Valid) {
        return body == BodyResult::Rejected ? ESP_OK : ESP_FAIL;
    }
    const auto entries = doc["zones"].as<JsonObjectConst>();
    if (doc.size() != 1 || entries.size() == 0) {
        return error(req, "400 Bad Request", "expected a nonempty zones object");
    }

    ZoneControl::Update updates[kZoneCount] = {};
    for (JsonPairConst entry : entries) {
        const int zone = findZone(entry.key());
        if (zone < 0) {
            return error(req, "404 Not Found", "unknown zone");
        }
        if (!kZones[zone].enabled) {
            return error(req, "409 Conflict", "zone is disabled");
        }
        if (!parseEffect(entry.value(), updates[zone].effect)) {
            return error(req, "400 Bad Request", "invalid effect or parameters");
        }
        updates[zone].included = true;
    }
    return queued(req, updates);
}

esp_err_t blackout(httpd_req_t* req) {
    if (req->content_len != 0) {
        return error(req, "400 Bad Request", "blackout takes an empty body");
    }
    ZoneControl::Update updates[kZoneCount] = {};
    for (int zone = 0; zone < kZoneCount; ++zone) {
        updates[zone] = {kZones[zone].enabled, {Type::Off, CRGB::Black, 0, 0}};
    }

    const uint32_t revision = ZoneControl::queue(updates, true);
    const uint32_t started = millis();
    while (!ZoneControl::applied(revision) && millis() - started < 250) {
        delay(1);
    }
    if (!ZoneControl::applied(revision)) {
        return error(req, "503 Service Unavailable", "blackout frame still pending");
    }
    JsonDocument doc;
    doc["blackout"] = true;
    return sendJson(req, doc);
}

template <esp_err_t (*Handler)(httpd_req_t*)>
esp_err_t serveLan(httpd_req_t* req) {
    if (!lanPeer(req)) {
        return error(req, "403 Forbidden", "local IPv4 subnet required");
    }
    return Handler(req);
}
}

void HttpApi::begin() {
    httpd_handle_t server = nullptr;
    httpd_config_t config = HTTPD_DEFAULT_CONFIG();
    config.uri_match_fn = httpd_uri_match_wildcard;
    config.recv_wait_timeout = 1;
    config.send_wait_timeout = 1;
    config.stack_size = 8192;
    config.lru_purge_enable = true;
    if (httpd_start(&server, &config) != ESP_OK) {
        Serial.println("HTTP server could not start.");
        return;
    }

    const httpd_uri_t routes[] = {
        {"/api/status", HTTP_GET, serveLan<status>, nullptr},
        {"/api/zones", HTTP_GET, serveLan<zones>, nullptr},
        {"/api/zones/*", HTTP_POST, serveLan<updateZone>, nullptr},
        {"/api/scene", HTTP_POST, serveLan<scene>, nullptr},
        {"/api/blackout", HTTP_POST, serveLan<blackout>, nullptr},
    };
    for (const auto& route : routes) {
        if (httpd_register_uri_handler(server, &route) != ESP_OK) {
            httpd_stop(server);
            Serial.println("HTTP routes could not be registered.");
            return;
        }
    }
}

#endif

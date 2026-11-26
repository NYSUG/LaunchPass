#include <Arduino.h>

#include "Diagnostics.h"
#include "Effects.h"
#include "LedOutputs.h"

namespace {
using namespace LightingConfig;
using Effects::Settings;
using Effects::Type;

constexpr Settings off{Type::Off, CRGB::Black, 0};

constexpr Settings solid(CRGB color) {
    return {Type::Solid, color, 0};
}

constexpr Settings chase(CRGB color, uint32_t stepMs) {
    return {Type::Chase, color, stepMs};
}

struct Phase {
    const char* label;
    uint32_t durationMs;
    Settings left;
    Settings right;
};

constexpr uint32_t kLeftChaseMs = 250;
constexpr uint32_t kRightChaseMs = 400;
constexpr uint32_t kLeftPassMs = kZones[kLeft].count * kLeftChaseMs;
constexpr uint32_t kRightPassMs = kZones[kRight].count * kRightChaseMs;

const Phase kMilestone1[] = {
    {"Startup: off", 1000, off, off},
    {"Solid red", 2000, solid(CRGB::Red), off},
    {"Solid green", 2000, solid(CRGB::Green), off},
    {"Solid blue", 2000, solid(CRGB::Blue), off},
    {"Solid white", 2000, solid(CRGB::White), off},
    {"Pixel chase", kLeftPassMs, chase(CRGB::White, kLeftChaseMs), off},
    {"All off", 1000, off, off},
};

const Phase kMultiZone[] = {
    {"Startup: off", 1000, off, off},
    {"Independent solids: left red, right blue", 2500, solid(CRGB::Red), solid(CRGB::Blue)},
    {"Independent chases: left red, right blue",
     kLeftPassMs > kRightPassMs ? kLeftPassMs : kRightPassMs,
     chase(CRGB::Red, kLeftChaseMs), chase(CRGB::Blue, kRightChaseMs)},
    {"Synchronized green", 1500, solid(CRGB::Green), solid(CRGB::Green)},
    {"Synchronized blue", 1500, solid(CRGB::Blue), solid(CRGB::Blue)},
    {"Synchronized white", 1500, solid(CRGB::White), solid(CRGB::White)},
    {"Left off, right stays white", 2500, off, solid(CRGB::White)},
    {"All off", 1500, off, off},
};

template <size_t Count>
void tickDiagnostic(const Phase (&phases)[Count], uint32_t now) {
    static size_t phase = 0;
    static uint32_t started = now;
    static uint32_t lastFrame = now;
    static bool entered = true;

    if (now - started >= phases[phase].durationMs) {
        ++phase;
        if (phase == Count) {
            phase = 1;  // Startup blackout only runs at boot.
        }
        started = now;
        entered = true;
    }

    if (entered) {
        Serial.println(phases[phase].label);
        entered = false;
    } else if (now - lastFrame < 10) {
        return;
    }

    Effects::render(kLeft, phases[phase].left, now - started);
    Effects::render(kRight, phases[phase].right, now - started);
    LedOutputs::show();
    lastFrame = now;
}
}

void Diagnostics::tick(uint32_t now) {
    if (kDiagnostic == Diagnostic::Milestone1) {
        tickDiagnostic(kMilestone1, now);
    } else {
        tickDiagnostic(kMultiZone, now);
    }
}

#pragma once

#include <FastLED.h>

namespace LightingConfig {
struct Zone {
    uint8_t pin;
    const char* name;
    int count;
    bool enabled;
    bool reverse;
};

constexpr Zone kZones[] = {
    {25, "left",       10, true,  false},
    {26, "right",      10, true,  false},
    {27, "coin_door",   0, false, false},
    {33, "rear",        0, false, false},
    {32, "marquee",     0, false, false},
    {18, "monitor",     0, false, false},
    {19, "reserved_1",  0, false, false},
    {23, "reserved_2",  0, false, false},
};

constexpr int kZoneCount = sizeof(kZones) / sizeof(kZones[0]);
constexpr int kLeft = 0;
constexpr int kRight = 1;
constexpr EOrder kColorOrder = GRB;
constexpr uint8_t kBrightness = 16;

template <uint8_t Pin, EOrder Order>
using LedChipset = WS2812B<Pin, Order>;

enum class Diagnostic { MultiZone, Milestone1 };
constexpr Diagnostic kDiagnostic = Diagnostic::MultiZone;
}

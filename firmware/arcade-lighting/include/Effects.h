#pragma once

#include <FastLED.h>

namespace Effects {
enum class Type { Off, Solid, Chase };

struct Settings {
    Type type;
    CRGB color;
    uint32_t stepMs;
};

void render(int zone, const Settings& effect, uint32_t elapsedMs);
}

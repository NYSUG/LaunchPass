#pragma once

#include <FastLED.h>

namespace Effects {
enum class Type { Off, Solid, Breathe, Chase };

struct Settings {
    Type type;
    CRGB color;
    uint32_t speedMs;
    uint8_t brightness;
};

void render(int zone, const Settings& effect, uint32_t elapsedMs);
}

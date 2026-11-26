#include "LedOutputs.h"

namespace {
using namespace LightingConfig;
CRGB* buffers[kZoneCount] = {};

template <int Zone, bool Enabled = kZones[Zone].enabled>
struct Output {
    static void begin() {}
};

template <int Zone>
struct Output<Zone, true> {
    static void begin() {
        constexpr auto config = kZones[Zone];
        static_assert(config.count > 0, "Enabled zones need at least one pixel");
        static CRGB pixels[config.count];
        buffers[Zone] = pixels;
        FastLED.addLeds<LedChipset, config.pin, kColorOrder>(pixels, config.count);
        fill_solid(pixels, config.count, CRGB::Black);
    }
};

template <int Zone>
void beginOutputs() {
    Output<Zone>::begin();
    beginOutputs<Zone + 1>();
}

template <>
void beginOutputs<kZoneCount>() {}
}

void LedOutputs::begin() {
    beginOutputs<0>();
    FastLED.setBrightness(kBrightness);
    FastLED.setDither(0);
    show();
}

CRGB* LedOutputs::pixels(int zone) {
    return buffers[zone];
}

void LedOutputs::show() {
    FastLED.show();
}

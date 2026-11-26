#include "Effects.h"
#include "LedOutputs.h"

void Effects::render(int zone, const Settings& effect, uint32_t elapsedMs) {
    auto* pixels = LedOutputs::pixels(zone);
    if (!pixels) {
        return;
    }

    const auto& config = LightingConfig::kZones[zone];
    fill_solid(pixels, config.count, effect.type == Type::Solid ? effect.color : CRGB::Black);
    if (effect.type == Type::Chase) {
        const int pixel = elapsedMs / effect.stepMs % config.count;
        pixels[config.reverse ? config.count - 1 - pixel : pixel] = effect.color;
    }
}

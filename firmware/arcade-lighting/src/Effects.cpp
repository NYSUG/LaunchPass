#include "Effects.h"
#include "LedOutputs.h"

void Effects::render(int zone, const Settings& effect, uint32_t elapsedMs) {
    auto* pixels = LedOutputs::pixels(zone);
    if (!pixels) {
        return;
    }

    const auto& config = LightingConfig::kZones[zone];
    CRGB color = effect.color;
    uint8_t brightness = effect.brightness;
    if (effect.type == Type::Breathe) {
        const uint32_t phase = (elapsedMs % effect.speedMs) * 256 / effect.speedMs;
        brightness = scale8(brightness, ease8InOutQuad(triwave8(phase)));
    }
    color.nscale8(brightness);
    const bool solid = effect.type == Type::Solid || effect.type == Type::Breathe;
    fill_solid(pixels, config.count, solid ? color : CRGB::Black);
    if (effect.type == Type::Chase) {
        const int pixel = elapsedMs / effect.speedMs % config.count;
        pixels[config.reverse ? config.count - 1 - pixel : pixel] = color;
    }
}

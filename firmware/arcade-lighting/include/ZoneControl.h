#pragma once

#include "Effects.h"
#include "LightingConfig.h"

namespace ZoneControl {
struct Update {
    bool included;
    Effects::Settings effect;
};

uint32_t queue(const Update (&updates)[LightingConfig::kZoneCount], bool urgent = false);
void snapshot(Effects::Settings (&effects)[LightingConfig::kZoneCount]);
bool applied(uint32_t revision);
void tick(uint32_t now);
}

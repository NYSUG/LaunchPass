#pragma once

#include "LightingConfig.h"

namespace LedOutputs {
void begin();
CRGB* pixels(int zone);
void show();
}

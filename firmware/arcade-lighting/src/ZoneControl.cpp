#ifdef ARCADE_NETWORK

#include <Arduino.h>

#include "LedOutputs.h"
#include "ZoneControl.h"

namespace {
using namespace LightingConfig;
portMUX_TYPE lock = portMUX_INITIALIZER_UNLOCKED;
ZoneControl::Update pending[kZoneCount] = {};
Effects::Settings active[kZoneCount] = {};
uint32_t started[kZoneCount] = {};
uint32_t requested = 0;
uint32_t shown = 0;
bool urgentFrame = false;
}

uint32_t ZoneControl::queue(const Update (&updates)[kZoneCount], bool urgent) {
    portENTER_CRITICAL(&lock);
    for (int zone = 0; zone < kZoneCount; ++zone) {
        if (updates[zone].included) {
            pending[zone] = updates[zone];
        }
    }
    urgentFrame |= urgent;
    const uint32_t revision = ++requested;
    portEXIT_CRITICAL(&lock);
    return revision;
}

void ZoneControl::snapshot(Effects::Settings (&effects)[kZoneCount]) {
    portENTER_CRITICAL(&lock);
    for (int zone = 0; zone < kZoneCount; ++zone) {
        effects[zone] = active[zone];
    }
    portEXIT_CRITICAL(&lock);
}

bool ZoneControl::applied(uint32_t revision) {
    portENTER_CRITICAL(&lock);
    const bool done = shown - revision < 0x80000000UL;
    portEXIT_CRITICAL(&lock);
    return done;
}

void ZoneControl::tick(uint32_t now) {
    static uint32_t lastFrame = 0;
    portENTER_CRITICAL(&lock);
    if (now - lastFrame < 10 && !urgentFrame) {
        portEXIT_CRITICAL(&lock);
        return;
    }

    bool redraw = urgentFrame;
    for (int zone = 0; zone < kZoneCount; ++zone) {
        if (pending[zone].included) {
            active[zone] = pending[zone].effect;
            started[zone] = now;
            pending[zone].included = false;
            redraw = true;
        }
        redraw |= active[zone].type == Effects::Type::Breathe ||
                  active[zone].type == Effects::Type::Chase;
    }
    const uint32_t revision = requested;
    urgentFrame = false;
    portEXIT_CRITICAL(&lock);
    lastFrame = now;

    if (redraw) {
        for (int zone = 0; zone < kZoneCount; ++zone) {
            Effects::render(zone, active[zone], now - started[zone]);
        }
        LedOutputs::show();
        portENTER_CRITICAL(&lock);
        shown = revision;
        portEXIT_CRITICAL(&lock);
    }
}

#endif

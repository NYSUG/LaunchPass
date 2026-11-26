#include <Arduino.h>

#include "Diagnostics.h"
#include "LedOutputs.h"

void setup() {
    Serial.begin(115200);
    LedOutputs::begin();
}

void loop() {
    Diagnostics::tick(millis());
}

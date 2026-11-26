#include <Arduino.h>

#include "Diagnostics.h"
#include "LedOutputs.h"
#ifdef ARCADE_NETWORK
#include "HttpApi.h"
#include "WifiConnection.h"
#include "ZoneControl.h"
#endif

void setup() {
    Serial.begin(115200);
    LedOutputs::begin();
#ifdef ARCADE_NETWORK
    WifiConnection::begin();
    HttpApi::begin();
#endif
}

void loop() {
#ifdef ARCADE_NETWORK
    ZoneControl::tick(millis());
#else
    Diagnostics::tick(millis());
#endif
    delay(1);  // Yield to the scheduler between time-based frames.
}

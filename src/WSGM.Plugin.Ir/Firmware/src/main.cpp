#include <Arduino.h>
#include <ArduinoJson.h>
#include <Adafruit_NeoPixel.h>
#include <IRrecv.h>
#include <IRsend.h>
#include <IRutils.h>

// Seeed XIAO IR Mate: D1/D2/D3/D4/D5 map to GPIO 3/4/5/6/7.
constexpr uint8_t TxPin = 3, RxPin = 4, TouchPin = 5, MotorPin = 6, LedPin = 7;
constexpr size_t MaxFrame = 32768, MaxTimings = 1024;
IRrecv receiver(RxPin, MaxTimings + 1, 50, true);
IRsend transmitter(TxPin);
Adafruit_NeoPixel led(1, LedPin, NEO_GRB + NEO_KHZ800);
decode_results captured;
String input, learningId;
uint32_t learningDeadline = 0, feedbackDeadline = 0;
bool discarding = false, touchWasDown = false;

void feedback(uint32_t duration) {
  led.setPixelColor(0, led.Color(0, 0, 32));
  led.show();
  digitalWrite(MotorPin, HIGH);
  feedbackDeadline = millis() + duration;
}

void reply(const String &id, const char *status, JsonDocument *payload = nullptr) {
  JsonDocument message;
  message["v"] = 1;
  message["id"] = id;
  message["status"] = status;
  if (payload) message["data"] = payload->as<JsonVariant>();
  serializeJson(message, Serial);
  Serial.println();
}

void cancelLearn(const char *reason) {
  if (!learningId.isEmpty()) {
    reply(learningId, reason);
    learningId = "";
    receiver.resume();
  }
  led.clear();
  led.show();
  digitalWrite(MotorPin, LOW);
}

void dispatch(const String &line) {
  JsonDocument request;
  if (deserializeJson(request, line, DeserializationOption::NestingLimit(8))) {
    reply("", "malformed");
    return;
  }
  String id = request["id"] | "";
  if (id.isEmpty() || id.length() > 64) { reply("", "invalid-id"); return; }
  if (request["v"].as<int>() != 1) { reply(id, "protocol-mismatch"); return; }
  String operation = request["op"] | "";
  if (operation == "identify" || operation == "health") {
    JsonDocument data;
    char identity[17];
    snprintf(identity, sizeof(identity), "%016llx", ESP.getEfuseMac());
    data["identity"] = identity;
    data["model"] = "xiao-ir-mate";
    data["firmware"] = "0.1.0";
    data["protocol"] = 1;
    data["maxTimings"] = MaxTimings;
    data["learning"] = !learningId.isEmpty();
    data["uptimeMs"] = millis();
    reply(id, "ok", &data);
  } else if (operation == "cancel") {
    cancelLearn("cancelled");
    reply(id, "ok");
  } else if (!learningId.isEmpty()) {
    reply(id, "busy");
  } else if (operation == "learn") {
    uint32_t timeout = request["timeoutMs"] | 15000;
    if (timeout < 1000 || timeout > 30000) { reply(id, "invalid-timeout"); return; }
    receiver.resume();
    learningId = id;
    learningDeadline = millis() + timeout;
    feedback(100);
  } else if (operation == "send") {
    JsonArrayConst timings = request["payload"]["timingsUs"].as<JsonArrayConst>();
    uint32_t carrier = request["payload"]["carrierHz"] | 0;
    int repeats = request["repeats"] | 0;
    int gap = request["gapMs"] | 40;
    if (timings.size() < 2 || timings.size() > MaxTimings || carrier < 20000 || carrier > 60000
        || repeats < 0 || repeats > 4 || gap < 0 || gap > 200) { reply(id, "invalid-payload"); return; }
    uint16_t raw[MaxTimings];
    uint64_t duration = 0;
    for (size_t i = 0; i < timings.size(); ++i) {
      if (!timings[i].is<uint32_t>()) { reply(id, "invalid-timing"); return; }
      uint32_t value = timings[i].as<uint32_t>();
      if (value < 1 || value > 65535) { reply(id, "invalid-timing"); return; }
      raw[i] = value;
      duration += value;
    }
    if (duration > 2000000 || duration * (repeats + 1) + uint64_t(gap) * 1000 * repeats > 5000000) {
      reply(id, "duration-limit"); return;
    }
    receiver.disableIRIn();
    for (int i = 0; i <= repeats; ++i) {
      if (i) delay(gap);
      transmitter.sendRaw(raw, timings.size(), carrier);
      yield();
    }
    receiver.enableIRIn();
    feedback(70);
    reply(id, "transmitted"); // Confirms emission only, never the appliance's resulting state.
  } else {
    reply(id, "unsupported-operation");
  }
}

void setup() {
  led.begin();
  pinMode(MotorPin, OUTPUT);
  pinMode(TouchPin, INPUT_PULLDOWN);
  led.clear();
  led.show();
  digitalWrite(MotorPin, LOW);
  Serial.begin(115200);
  input.reserve(MaxFrame);
  transmitter.begin();
  receiver.enableIRIn();
}

void loop() {
  // Limit each turn so an endless serial sender cannot starve learn timeout or feedback cleanup.
  for (size_t count = 0; count < 512 && Serial.available(); ++count) {
    char ch = Serial.read();
    if (ch == '\n') {
      if (discarding) reply("", "frame-too-large");
      else if (!input.isEmpty()) dispatch(input);
      input = "";
      discarding = false;
    } else if (ch != '\r' && !discarding) {
      if (input.length() >= MaxFrame) { input = ""; discarding = true; }
      else input += ch;
    }
  }
  if (feedbackDeadline && int32_t(millis() - feedbackDeadline) >= 0) {
    feedbackDeadline = 0;
    digitalWrite(MotorPin, LOW);
    led.clear();
    led.show();
  }
  bool touchDown = digitalRead(TouchPin) == HIGH;
  if (touchDown && !touchWasDown) feedback(70);
  touchWasDown = touchDown;
  if (!learningId.isEmpty() && int32_t(millis() - learningDeadline) >= 0) cancelLearn("timeout");
  if (receiver.decode(&captured)) {
    if (!learningId.isEmpty()) {
      if (captured.overflow || captured.rawlen < 3 || captured.rawlen - 1 > MaxTimings) {
        cancelLearn("capture-overflow");
      } else {
        JsonDocument data;
        // This capture path measures the envelope, not carrier. Do not claim a measured frequency.
        data["carrierHz"] = 38000;
        data["carrierSource"] = "assumed";
        data["protocol"] = typeToString(captured.decode_type);
        data["address"] = captured.address;
        data["command"] = captured.command;
        data["bits"] = captured.bits;
        data["repeat"] = captured.repeat;
        JsonArray raw = data["timingsUs"].to<JsonArray>();
        bool valid = true;
        for (uint16_t i = 1; i < captured.rawlen; ++i) {
          uint32_t timing = captured.rawbuf[i] * kRawTick;
          if (!timing || timing > 65535) { valid = false; break; }
          raw.add(timing);
        }
        if (valid) reply(learningId, "learned", &data);
        else reply(learningId, "timing-limit");
        learningId = "";
        feedback(valid ? 70 : 300);
      }
    }
    receiver.resume();
  }
  delay(1);
}

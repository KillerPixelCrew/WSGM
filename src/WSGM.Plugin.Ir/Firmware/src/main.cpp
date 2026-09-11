#include <Arduino.h>
#include <ArduinoJson.h>
#include <Adafruit_NeoPixel.h>
#include <ESPmDNS.h>
#include <IRrecv.h>
#include <IRsend.h>
#include <IRutils.h>
#include <Preferences.h>
#include <WiFi.h>

// Seeed XIAO IR Mate: D1/D2/D3/D4/D5 map to GPIO 3/4/5/6/7.
constexpr uint8_t TxPin = 3, RxPin = 4, TouchPin = 5, MotorPin = 6, LedPin = 7;
constexpr size_t MaxFrame = 32768, MaxTimings = 1024;
constexpr uint16_t NetworkPort = 7521;
constexpr uint32_t ClientIdleMs = 120000;
constexpr const char *Firmware = "0.2.0";

// One line-oriented request source. USB is trusted by physical access; the network needs the token.
struct Channel {
  Channel(Stream &source, bool physical) : stream(source), trusted(physical) {}
  Stream &stream;
  bool trusted;
  String input;
  bool discarding = false;
};

IRrecv receiver(RxPin, MaxTimings + 1, 50, true);
IRsend transmitter(TxPin);
Adafruit_NeoPixel led(1, LedPin, NEO_GRB + NEO_KHZ800);
Preferences settings;
WiFiServer server(NetworkPort);
WiFiClient client;
Channel usb{Serial, true};
Channel network{client, false};
decode_results captured;
String learningId, token;
Print *learningOut = nullptr;
char hostname[24];
uint32_t learningDeadline = 0, feedbackDeadline = 0, clientActivity = 0;
bool touchWasDown = false, mdnsStarted = false;

void feedback(uint32_t duration) {
  led.setPixelColor(0, led.Color(0, 0, 32));
  led.show();
  digitalWrite(MotorPin, HIGH);
  feedbackDeadline = millis() + duration;
}

void reply(Print &out, const String &id, const char *status, JsonDocument *payload = nullptr) {
  JsonDocument message;
  message["v"] = 1;
  message["id"] = id;
  message["status"] = status;
  if (payload) message["data"] = payload->as<JsonVariant>();
  serializeJson(message, out);
  out.println();
}

void cancelLearn(const char *reason) {
  if (!learningId.isEmpty()) {
    reply(*learningOut, learningId, reason);
    learningId = "";
    learningOut = nullptr;
    receiver.resume();
  }
  led.clear();
  led.show();
  digitalWrite(MotorPin, LOW);
}

bool authorized(const String &supplied) {
  if (token.isEmpty() || supplied.length() != token.length()) return false;
  uint8_t difference = 0;
  for (size_t i = 0; i < token.length(); ++i) difference |= token[i] ^ supplied[i];
  return difference == 0;
}

void connectWifi() {
  String ssid = settings.getString("ssid", "");
  if (ssid.isEmpty()) return;
  WiFi.mode(WIFI_STA);
  WiFi.setHostname(hostname);
  WiFi.setSleep(false);
  WiFi.setAutoReconnect(true);
  WiFi.begin(ssid.c_str(), settings.getString("pass", "").c_str());
  server.begin();
}

void describe(JsonDocument &data) {
  char identity[17];
  snprintf(identity, sizeof(identity), "%016llx", ESP.getEfuseMac());
  data["identity"] = identity;
  data["model"] = "xiao-ir-mate";
  data["firmware"] = Firmware;
  data["protocol"] = 1;
  data["maxTimings"] = MaxTimings;
  data["learning"] = !learningId.isEmpty();
  data["uptimeMs"] = millis();
  data["hostname"] = hostname;
  data["port"] = NetworkPort;
  data["wifiConfigured"] = !settings.getString("ssid", "").isEmpty();
  bool connected = WiFi.status() == WL_CONNECTED;
  data["wifiConnected"] = connected;
  data["ip"] = connected ? WiFi.localIP().toString() : String("");
}

void dispatch(Channel &channel, const String &line) {
  Print &out = channel.stream;
  JsonDocument request;
  if (deserializeJson(request, line, DeserializationOption::NestingLimit(8))) {
    reply(out, "", "malformed");
    return;
  }
  String id = request["id"] | "";
  if (id.isEmpty() || id.length() > 64) { reply(out, "", "invalid-id"); return; }
  if (request["v"].as<int>() != 1) { reply(out, id, "protocol-mismatch"); return; }
  String operation = request["op"] | "";
  if (!channel.trusted && operation != "identify" && !authorized(request["token"] | "")) {
    reply(out, id, "unauthorized");
    return;
  }
  if (operation == "identify" || operation == "health") {
    JsonDocument data;
    describe(data);
    reply(out, id, "ok", &data);
  } else if (operation == "cancel") {
    cancelLearn("cancelled");
    reply(out, id, "ok");
  } else if (!learningId.isEmpty()) {
    reply(out, id, "busy");
  } else if (operation == "learn") {
    uint32_t timeout = request["timeoutMs"] | 15000;
    if (timeout < 1000 || timeout > 30000) { reply(out, id, "invalid-timeout"); return; }
    receiver.resume();
    learningId = id;
    learningOut = &out;
    learningDeadline = millis() + timeout;
    feedback(100);
  } else if (operation == "send") {
    JsonArrayConst timings = request["payload"]["timingsUs"].as<JsonArrayConst>();
    uint32_t carrier = request["payload"]["carrierHz"] | 0;
    int repeats = request["repeats"] | 0;
    int gap = request["gapMs"] | 40;
    if (timings.size() < 2 || timings.size() > MaxTimings || carrier < 20000 || carrier > 60000
        || repeats < 0 || repeats > 4 || gap < 0 || gap > 200) { reply(out, id, "invalid-payload"); return; }
    uint16_t raw[MaxTimings];
    uint64_t duration = 0;
    for (size_t i = 0; i < timings.size(); ++i) {
      if (!timings[i].is<uint32_t>()) { reply(out, id, "invalid-timing"); return; }
      uint32_t value = timings[i].as<uint32_t>();
      if (value < 1 || value > 65535) { reply(out, id, "invalid-timing"); return; }
      raw[i] = value;
      duration += value;
    }
    if (duration > 2000000 || duration * (repeats + 1) + uint64_t(gap) * 1000 * repeats > 5000000) {
      reply(out, id, "duration-limit"); return;
    }
    receiver.disableIRIn();
    for (int i = 0; i <= repeats; ++i) {
      if (i) delay(gap);
      transmitter.sendRaw(raw, timings.size(), carrier);
      yield();
    }
    receiver.enableIRIn();
    feedback(70);
    reply(out, id, "transmitted"); // Confirms emission only, never the appliance's resulting state.
  } else if (operation == "wifi") {
    // Pairing happens over USB only: whoever holds the cable sets the network and the token.
    if (!channel.trusted) { reply(out, id, "usb-only"); return; }
    String ssid = request["ssid"] | "", password = request["password"] | "", pairing = request["token"] | "";
    if (ssid.length() > 32 || password.length() > 63 || pairing.length() > 64
        || (!ssid.isEmpty() && pairing.length() < 16)) { reply(out, id, "invalid-wifi"); return; }
    if (ssid.isEmpty()) {
      settings.clear();
      token = "";
      client.stop();
      if (mdnsStarted) { MDNS.end(); mdnsStarted = false; }
      WiFi.disconnect(true, true);
      WiFi.mode(WIFI_OFF);
    } else {
      settings.putString("ssid", ssid);
      settings.putString("pass", password);
      settings.putString("token", pairing);
      token = pairing;
      WiFi.disconnect(true, false);
      connectWifi();
    }
    JsonDocument data;
    describe(data);
    reply(out, id, "ok", &data);
  } else {
    reply(out, id, "unsupported-operation");
  }
}

void pump(Channel &channel) {
  // Limit each turn so an endless sender cannot starve learn timeout or feedback cleanup.
  for (size_t count = 0; count < 512 && channel.stream.available(); ++count) {
    char ch = channel.stream.read();
    if (ch == '\n') {
      if (channel.discarding) reply(channel.stream, "", "frame-too-large");
      else if (!channel.input.isEmpty()) dispatch(channel, channel.input);
      channel.input = "";
      channel.discarding = false;
    } else if (ch != '\r' && !channel.discarding) {
      if (channel.input.length() >= MaxFrame) { channel.input = ""; channel.discarding = true; }
      else channel.input += ch;
    }
  }
}

void serveNetwork() {
  bool connected = WiFi.status() == WL_CONNECTED;
  if (connected && !mdnsStarted) {
    mdnsStarted = MDNS.begin(hostname);
    if (mdnsStarted) MDNS.addService("wsgm-ir", "tcp", NetworkPort);
  } else if (!connected && mdnsStarted) {
    MDNS.end();
    mdnsStarted = false;
  }
  if (!connected) return;
  WiFiClient incoming = server.available();
  if (incoming) {
    // One host at a time; the newest connection wins so a reconnecting host never waits on a dead one.
    if (client) client.stop();
    client = incoming;
    client.setNoDelay(true);
    network.input = "";
    network.discarding = false;
    clientActivity = millis();
  }
  if (!client || !client.connected()) return;
  if (client.available()) clientActivity = millis();
  pump(network);
  if (int32_t(millis() - clientActivity) > int32_t(ClientIdleMs)) client.stop();
}

void setup() {
  led.begin();
  pinMode(MotorPin, OUTPUT);
  pinMode(TouchPin, INPUT_PULLDOWN);
  led.clear();
  led.show();
  digitalWrite(MotorPin, LOW);
  Serial.begin(115200);
  usb.input.reserve(MaxFrame);
  network.input.reserve(MaxFrame);
  // The eFuse MAC's low bytes are the vendor prefix; the last three octets are what differ per board.
  uint64_t mac = ESP.getEfuseMac();
  snprintf(hostname, sizeof(hostname), "wsgm-ir-%02x%02x%02x",
           uint8_t(mac >> 24), uint8_t(mac >> 32), uint8_t(mac >> 40));
  settings.begin("wsgmir", false);
  token = settings.getString("token", "");
  transmitter.begin();
  receiver.enableIRIn();
  connectWifi();
}

void loop() {
  pump(usb);
  serveNetwork();
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
        if (valid) reply(*learningOut, learningId, "learned", &data);
        else reply(*learningOut, learningId, "timing-limit");
        learningId = "";
        learningOut = nullptr;
        feedback(valid ? 70 : 300);
      }
    }
    receiver.resume();
  }
  delay(1);
}

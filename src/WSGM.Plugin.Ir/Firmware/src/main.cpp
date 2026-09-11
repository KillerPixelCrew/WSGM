#include <Arduino.h>
#include <ArduinoJson.h>
#include <cerrno>
#include <Adafruit_NeoPixel.h>
#include <ESPmDNS.h>
#include <IRac.h>
#include <IRrecv.h>
#include <IRsend.h>
#include <IRutils.h>
#include <Preferences.h>
#include <WebServer.h>
#include <WiFi.h>
#include <uri/UriBraces.h>

#include "remotes_generated.h"

// Seeed XIAO IR Mate: D1/D2/D3/D4/D5 map to GPIO 3/4/5/6/7.
constexpr uint8_t TxPin = 3, RxPin = 4, TouchPin = 5, MotorPin = 6, LedPin = 7;
constexpr size_t MaxFrame = 32768, MaxTimings = 1024, MaxStateBytes = 64;
constexpr uint16_t NetworkPort = 7521, WebPort = 80;
constexpr uint32_t ClientIdleMs = 120000;
constexpr const char *Firmware = "0.4.0";

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
IRac airConditioner(TxPin);
Adafruit_NeoPixel led(1, LedPin, NEO_GRB + NEO_KHZ800);
Preferences settings;
WiFiServer server(NetworkPort);
WiFiClient client;
WebServer web(WebPort);
Channel usb{Serial, true};
Channel network{client, false};
decode_results captured;
String learningId, token, webUser, webPassword;
// Built-in remotes: full definitions for sending, and the id/label catalog hosts and pages read.
JsonDocument remotes, catalog;

// One background sequence at a time; loop() advances it so delays never block other requests.
struct SequenceRun {
  JsonObjectConst remote;
  JsonArrayConst steps;
  size_t next = 0;
  uint32_t due = 0;
  bool active = false;
} sequence;
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

// Capture pauses during emission so the receiver never decodes the endpoint's own frame.
template <typename Send> bool emit(Send send) {
  receiver.disableIRIn();
  bool sent = send();
  receiver.enableIRIn();
  feedback(sent ? 70 : 300);
  return sent;
}

// The library maps an unknown name to the default it is given, so parse with two defaults to detect one.
template <typename T> bool parseName(JsonVariantConst value, T fallback, T other, T (*parse)(const char *, T), T &result) {
  if (value.isNull()) { result = fallback; return true; }
  if (!value.is<const char *>()) return false;
  result = parse(value.as<const char *>(), fallback);
  return result == parse(value.as<const char *>(), other);
}

bool parseState(const char *hex, uint8_t *state, size_t &length) {
  size_t digits = strlen(hex);
  if (!digits || digits % 2 || digits / 2 > MaxStateBytes) return false;
  for (size_t i = 0; i < digits; i += 2) {
    char pair[3] = {hex[i], hex[i + 1], 0};
    char *end = nullptr;
    unsigned long value = strtoul(pair, &end, 16);
    if (!isxdigit(pair[0]) || !isxdigit(pair[1]) || *end) return false;
    state[i / 2] = uint8_t(value);
  }
  length = digits / 2;
  return true;
}

// Send paths return a protocol status so requests, web presses and sequences share one implementation.
const char *sendRaw(JsonVariantConst payload, int repeats, int gap) {
  JsonArrayConst timings = payload["timingsUs"].as<JsonArrayConst>();
  uint32_t carrier = payload["carrierHz"] | 0;
  if (timings.size() < 2 || timings.size() > MaxTimings || carrier < 20000 || carrier > 60000
      || repeats < 0 || repeats > 4 || gap < 0 || gap > 200) return "invalid-payload";
  uint16_t raw[MaxTimings];
  uint64_t duration = 0;
  for (size_t i = 0; i < timings.size(); ++i) {
    if (!timings[i].is<uint32_t>()) return "invalid-timing";
    uint32_t value = timings[i].as<uint32_t>();
    if (value < 1 || value > 65535) return "invalid-timing";
    raw[i] = value;
    duration += value;
  }
  if (duration > 2000000 || duration * (repeats + 1) + uint64_t(gap) * 1000 * repeats > 5000000) return "duration-limit";
  emit([&] {
    for (int i = 0; i <= repeats; ++i) {
      if (i) delay(gap);
      transmitter.sendRaw(raw, timings.size(), carrier);
      yield();
    }
    return true;
  });
  return "transmitted"; // Confirms emission only, never the appliance's resulting state.
}

const char *sendCode(JsonVariantConst request) {
  decode_type_t protocol = strToDecodeType(request["protocol"] | "");
  if (protocol <= decode_type_t::UNUSED) return "unknown-protocol";
  bool sent;
  if (hasACState(protocol)) {
    uint8_t state[MaxStateBytes];
    size_t length = 0;
    if (!parseState(request["state"] | "", state, length)) return "invalid-code";
    sent = emit([&] { return transmitter.send(protocol, state, length); });
  } else {
    const char *value = request["value"] | "";
    char *end = nullptr;
    errno = 0;
    uint64_t code = strtoull(value, &end, 16);
    int bits = request["bits"] | int(IRsend::defaultBits(protocol));
    int repeats = request["repeats"] | int(IRsend::minRepeats(protocol));
    if (!*value || *end || errno || bits < 1 || bits > 64 || repeats < 0 || repeats > 4) return "invalid-code";
    sent = emit([&] { return transmitter.send(protocol, code, bits, repeats); });
  }
  return sent ? "transmitted" : "unsupported-protocol";
}

const char *sendAc(JsonVariantConst request) {
  stdAc::state_t state;
  state.protocol = strToDecodeType(request["protocol"] | "");
  if (state.protocol <= decode_type_t::UNUSED || !IRac::isProtocolSupported(state.protocol)) return "unsupported-protocol";
  JsonVariantConst model = request["model"];
  bool valid = model.is<int>() ? (state.model = model.as<int16_t>(), true)
                               : parseName<int16_t>(model, -1, -2, IRac::strToModel, state.model);
  valid = valid && parseName(request["mode"], stdAc::opmode_t::kAuto, stdAc::opmode_t::kCool, IRac::strToOpmode, state.mode);
  valid = valid && parseName(request["fan"], stdAc::fanspeed_t::kAuto, stdAc::fanspeed_t::kLow, IRac::strToFanspeed, state.fanspeed);
  valid = valid && parseName(request["swingV"], stdAc::swingv_t::kOff, stdAc::swingv_t::kAuto, IRac::strToSwingV, state.swingv);
  valid = valid && parseName(request["swingH"], stdAc::swingh_t::kOff, stdAc::swingh_t::kAuto, IRac::strToSwingH, state.swingh);
  state.power = request["power"] | true;
  state.celsius = request["celsius"] | true;
  state.degrees = request["degrees"] | 24.0f;
  state.quiet = request["quiet"] | false;
  state.turbo = request["turbo"] | false;
  state.econo = request["econo"] | false;
  state.light = request["light"] | false;
  state.filter = request["filter"] | false;
  state.clean = request["clean"] | false;
  state.beep = request["beep"] | false;
  state.sleep = request["sleep"] | int16_t(-1);
  if (!valid || state.degrees < 10 || state.degrees > 90) return "invalid-ac-state";
  bool sent = emit([&] { return airConditioner.sendAc(state, nullptr); });
  return sent ? "transmitted" : "unsupported-protocol";
}

JsonObjectConst findById(JsonVariantConst list, const char *id) {
  for (JsonObjectConst entry : list.as<JsonArrayConst>()) {
    if (strcmp(entry["id"] | "", id) == 0) return entry;
  }
  return JsonObjectConst();
}

JsonObjectConst findRemote(const char *id) { return findById(remotes["remotes"], id); }

bool busy() { return !learningId.isEmpty() || sequence.active; }

const char *pressButton(JsonObjectConst button) {
  if (!button["code"].isNull()) return sendCode(button["code"]);
  if (!button["ac"].isNull()) return sendAc(button["ac"]);
  return sendRaw(button["raw"], button["repeats"] | 0, button["gapMs"] | 40);
}

const char *pressNamed(JsonObjectConst remote, const char *id) {
  JsonObjectConst button = findById(remote["buttons"], id);
  return button.isNull() ? "unknown-button" : pressButton(button);
}

bool listed(JsonVariantConst names, const char *name) {
  for (JsonVariantConst entry : names.as<JsonArrayConst>()) {
    if (strcmp(entry | "", name) == 0) return true;
  }
  return false;
}

// A climate request is checked against what the definition declares before any state is sent.
const char *sendClimate(JsonObjectConst remote, JsonVariantConst request) {
  JsonObjectConst climate = remote["climate"];
  if (climate.isNull()) return "unknown-climate";
  const char *mode = request["mode"] | "", *fan = request["fan"] | "";
  float degrees = request["degrees"] | 0.0f;
  bool toggleSwing = request["toggleSwing"] | false;
  if (!request["power"].is<bool>() || !listed(climate["modes"], mode) || !listed(climate["fans"], fan)
      || degrees < climate["minDegrees"].as<float>() || degrees > climate["maxDegrees"].as<float>()
      || (toggleSwing && strcmp(climate["swing"] | "none", "toggle") != 0)) return "invalid-ac-state";
  JsonDocument state;
  state["protocol"] = climate["protocol"];
  if (!climate["model"].isNull()) state["model"] = climate["model"];
  state["power"] = request["power"].as<bool>();
  state["mode"] = mode;
  state["degrees"] = degrees;
  state["celsius"] = climate["celsius"] | true;
  state["fan"] = fan;
  // Toggle-type swing: the library appends the toggle frame whenever vertical swing is not off.
  state["swingV"] = toggleSwing ? "auto" : "off";
  return sendAc(state.as<JsonVariantConst>());
}

const char *startSequence(JsonObjectConst remote, const char *id) {
  JsonObjectConst entry = findById(remote["sequences"], id);
  if (entry.isNull()) return "unknown-sequence";
  sequence.remote = remote;
  sequence.steps = entry["steps"];
  sequence.next = 0;
  sequence.due = millis();
  sequence.active = true;
  return "started";
}

void stepSequence() {
  if (!sequence.active || int32_t(millis() - sequence.due) < 0) return;
  if (sequence.next >= sequence.steps.size()) {
    sequence.active = false;
    return;
  }
  JsonObjectConst step = sequence.steps[sequence.next++];
  if (!step["delayMs"].isNull()) {
    sequence.due = millis() + step["delayMs"].as<uint32_t>();
    return;
  }
  // A failed step ends the sequence, so later steps never run against an unknown state.
  if (strcmp(pressNamed(sequence.remote, step["button"] | ""), "transmitted") != 0) sequence.active = false;
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

// Unset keys are normal on a fresh or unpaired endpoint, so read them without the NVS error path.
String stored(const char *key) { return settings.isKey(key) ? settings.getString(key) : String(); }

void connectWifi() {
  String ssid = stored("ssid");
  if (ssid.isEmpty()) return;
  WiFi.mode(WIFI_STA);
  WiFi.setHostname(hostname);
  WiFi.setSleep(false);
  WiFi.setAutoReconnect(true);
  WiFi.begin(ssid.c_str(), stored("pass").c_str());
  server.begin();
  web.begin();
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
  data["wifiConfigured"] = !stored("ssid").isEmpty();
  bool connected = WiFi.status() == WL_CONNECTED;
  data["wifiConnected"] = connected;
  data["ip"] = connected ? WiFi.localIP().toString() : String("");
  data["webPort"] = WebPort;
  data["webConfigured"] = !webUser.isEmpty();
  data["remotes"] = RemotePageCount;
  data["sequenceRunning"] = sequence.active;
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
    sequence.active = false;
    reply(out, id, "ok");
  } else if (operation == "remotes") {
    reply(out, id, "ok", &catalog);
  } else if (operation == "web") {
    // Like Wi-Fi pairing, web credentials are set only by whoever holds the USB cable.
    if (!channel.trusted) { reply(out, id, "usb-only"); return; }
    String user = request["user"] | "", password = request["password"] | "";
    if (user.length() > 32 || user.indexOf(':') >= 0 || password.length() > 64
        || (!user.isEmpty() && password.length() < 8) || (user.isEmpty() && !password.isEmpty())) {
      reply(out, id, "invalid-web");
      return;
    }
    settings.putString("webuser", user);
    settings.putString("webpass", password);
    webUser = user;
    webPassword = password;
    JsonDocument data;
    describe(data);
    reply(out, id, "ok", &data);
  } else if (busy()) {
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
    reply(out, id, sendRaw(request["payload"], request["repeats"] | 0, request["gapMs"] | 40));
  } else if (operation == "sendCode") {
    reply(out, id, sendCode(request.as<JsonVariantConst>()));
  } else if (operation == "sendAc") {
    reply(out, id, sendAc(request.as<JsonVariantConst>()));
  } else if (operation == "press" || operation == "climate" || operation == "run") {
    JsonObjectConst remote = findRemote(request["remote"] | "");
    if (remote.isNull()) { reply(out, id, "unknown-remote"); return; }
    if (operation == "press") reply(out, id, pressNamed(remote, request["button"] | ""));
    else if (operation == "run") reply(out, id, startSequence(remote, request["sequence"] | ""));
    else reply(out, id, sendClimate(remote, request.as<JsonVariantConst>()));
  } else if (operation == "protocols") {
    JsonDocument data;
    JsonArray list = data["protocols"].to<JsonArray>();
    for (int type = 1; type <= kLastDecodeType; ++type) {
      decode_type_t protocol = decode_type_t(type);
      JsonObject entry = list.add<JsonObject>();
      entry["name"] = typeToString(protocol);
      entry["bits"] = IRsend::defaultBits(protocol);
      entry["state"] = hasACState(protocol);
      entry["ac"] = IRac::isProtocolSupported(protocol);
    }
    reply(out, id, "ok", &data);
  } else if (operation == "wifi") {
    // Pairing happens over USB only: whoever holds the cable sets the network and the token.
    if (!channel.trusted) { reply(out, id, "usb-only"); return; }
    String ssid = request["ssid"] | "", password = request["password"] | "", pairing = request["token"] | "";
    if (ssid.length() > 32 || password.length() > 63 || pairing.length() > 64
        || (!ssid.isEmpty() && pairing.length() < 16)) { reply(out, id, "invalid-wifi"); return; }
    if (ssid.isEmpty()) {
      settings.clear();
      token = webUser = webPassword = "";
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
  web.handleClient();
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

// Every web route needs the credentials set over USB. Mutations also need a custom header: a browser
// may attach saved credentials to a cross-site form post, but a custom header forces a CORS preflight
// that this server never grants.
bool webAllowed(bool mutation) {
  if (webUser.isEmpty()) {
    web.send(403, "text/plain", "Set web credentials over USB first.");
    return false;
  }
  if (!web.authenticate(webUser.c_str(), webPassword.c_str())) {
    web.requestAuthentication(BASIC_AUTH, "WSGM IR");
    return false;
  }
  if (mutation && web.header("X-WSGM-IR") != "1") {
    web.send(403, "text/plain", "Missing X-WSGM-IR header.");
    return false;
  }
  return true;
}

void webReply(const char *status) {
  int code = !strcmp(status, "transmitted")     ? 200
             : !strcmp(status, "started")       ? 202
             : !strcmp(status, "busy")          ? 409
             : !strncmp(status, "unknown-", 8)  ? 404
                                                : 422;
  web.send(code, "application/json", String("{\"status\":\"") + status + "\"}");
}

void sendPage(const uint8_t *gzip, size_t length) {
  web.sendHeader("Content-Encoding", "gzip");
  web.sendHeader("Cache-Control", "no-cache");
  web.send_P(200, "text/html", (PGM_P)gzip, length);
}

void webAction(const char *operation) {
  if (!webAllowed(true)) return;
  JsonObjectConst remote = findRemote(web.pathArg(0).c_str());
  if (remote.isNull()) return webReply("unknown-remote");
  if (busy()) return webReply("busy");
  if (!strcmp(operation, "press")) return webReply(pressNamed(remote, web.pathArg(1).c_str()));
  if (!strcmp(operation, "run")) return webReply(startSequence(remote, web.pathArg(1).c_str()));
  JsonDocument body;
  if (deserializeJson(body, web.arg("plain"), DeserializationOption::NestingLimit(4))) return webReply("malformed");
  webReply(sendClimate(remote, body.as<JsonVariantConst>()));
}

void setupWeb() {
  static const char *headers[] = {"X-WSGM-IR"};
  web.collectHeaders(headers, 1);
  web.on("/", HTTP_GET, [] {
    if (webAllowed(false)) sendPage(IndexPage, sizeof(IndexPage));
  });
  web.on(UriBraces("/remotes/{}/remote.json"), HTTP_GET, [] {
    if (!webAllowed(false)) return;
    JsonObjectConst entry = findById(catalog["remotes"], web.pathArg(0).c_str());
    if (entry.isNull()) return webReply("unknown-remote");
    String body;
    serializeJson(entry, body);
    web.send(200, "application/json", body);
  });
  web.on(UriBraces("/remotes/{}/"), HTTP_GET, [] {
    if (!webAllowed(false)) return;
    for (size_t i = 0; i < RemotePageCount; ++i) {
      if (web.pathArg(0) == RemotePages[i].remote) return sendPage(RemotePages[i].gzip, RemotePages[i].length);
    }
    webReply("unknown-remote");
  });
  // Pages use relative links, so a remote's address always ends with a slash.
  web.on(UriBraces("/remotes/{}"), HTTP_GET, [] {
    if (!webAllowed(false)) return;
    web.sendHeader("Location", "/remotes/" + web.pathArg(0) + "/");
    web.send(302);
  });
  web.on(UriBraces("/remotes/{}/buttons/{}"), HTTP_POST, [] { webAction("press"); });
  web.on(UriBraces("/remotes/{}/sequences/{}"), HTTP_POST, [] { webAction("run"); });
  web.on(UriBraces("/remotes/{}/climate"), HTTP_POST, [] { webAction("climate"); });
  web.onNotFound([] {
    if (webAllowed(false)) web.send(404, "text/plain", "Not found.");
  });
}

void setup() {
  led.begin();
  pinMode(MotorPin, OUTPUT);
  pinMode(TouchPin, INPUT_PULLDOWN);
  led.clear();
  led.show();
  digitalWrite(MotorPin, LOW);
  // The default 256-byte CDC queue drops the tail of a full raw payload written in one burst.
  Serial.setRxBufferSize(MaxFrame);
  Serial.begin(115200);
  usb.input.reserve(MaxFrame);
  network.input.reserve(MaxFrame);
  // The eFuse MAC's low bytes are the vendor prefix; the last three octets are what differ per board.
  uint64_t mac = ESP.getEfuseMac();
  snprintf(hostname, sizeof(hostname), "wsgm-ir-%02x%02x%02x",
           uint8_t(mac >> 24), uint8_t(mac >> 32), uint8_t(mac >> 40));
  settings.begin("wsgmir", false);
  token = stored("token");
  webUser = stored("webuser");
  webPassword = stored("webpass");
  // Generated and validated at build time, so parsing cannot meet a definition the build refused.
  deserializeJson(remotes, (const char *)RemotesJson, sizeof(RemotesJson));
  deserializeJson(catalog, (const char *)CatalogJson, sizeof(CatalogJson));
  setupWeb();
  transmitter.begin();
  receiver.enableIRIn();
  connectWifi();
}

void loop() {
  pump(usb);
  serveNetwork();
  stepSequence();
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

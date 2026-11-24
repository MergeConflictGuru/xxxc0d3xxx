#include <rtc/rtc.hpp>
#include <nlohmann/json.hpp>
#include <atomic>
#include <chrono>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>
#include <cstdint>

#if defined(_WIN32)
#define API extern "C" __declspec(dllexport)
#else
#define API extern "C" __attribute__((visibility("default")))
#endif
using json = nlohmann::json;
using Clock = std::chrono::steady_clock;
using Event = void (*)(void *, const char *, const char *, const char *, const unsigned char *, int);
struct Net;
struct Session {
    std::shared_ptr<rtc::PeerConnection> pc;
    std::shared_ptr<rtc::DataChannel> dc;
    std::vector<rtc::Candidate> candidates;
    std::string id, remote, localSdp;
    int server = 0;
    bool accepted = false;
};
struct Server { bool ready = false; };
struct Net : std::enable_shared_from_this<Net> {
    std::recursive_mutex gate;
    std::string id, ca;
    bool host, alive = true;
    Event event;
    void *context;
    std::vector<Server> servers;
    std::vector<rtc::IceServer> extraIce;
    bool allowRelay = false;
    std::map<std::string, std::shared_ptr<Session>> sessions;
    void emit(const char *kind, const std::string &id = "", const json &body = json::object(), const unsigned char *data = nullptr, int count = 0) {
        std::lock_guard<std::recursive_mutex> lock(gate);
        if (!alive) return;
        std::string text = body.dump(); event(context, kind, id.c_str(), text.c_str(), data, count);
    }
    void sendSignal(int server, const std::string &type, const std::string &remote, const json &payload) {
        if (!servers.at(server).ready) throw std::runtime_error("Signaling server is unavailable");
        emit("signal-send", "", {{"server", server}, {"message", json{{"type", type}, {"dst", remote}, {"payload", payload}}.dump()}});
    }
    void bindData(const std::shared_ptr<Session> &s, const std::shared_ptr<rtc::DataChannel> &dc) {
        s->dc = dc; auto weak = weak_from_this(); auto sid = s->id;
        dc->onOpen([weak, sid] { if (auto n = weak.lock()) n->emit("open", sid); });
        dc->onClosed([weak, sid] { if (auto n = weak.lock()) n->emit("closed", sid); });
        dc->onError([weak, sid](std::string e) { if (auto n = weak.lock()) n->emit("error", sid, {{"error", e}}); });
        dc->onMessage([weak, sid](rtc::message_variant data) {
            if (auto n = weak.lock()) {
                if (auto bytes = std::get_if<rtc::binary>(&data)) {
                    if (bytes->size() <= 8192) n->emit("packet", sid, json::object(), reinterpret_cast<const unsigned char *>(bytes->data()), static_cast<int>(bytes->size()));
                    else n->emit("error", sid, {{"error", "Oversized transport packet"}});
                }
            }
        });
    }
    void setIce(const std::string &iceJson) {
        std::lock_guard<std::recursive_mutex> lock(gate);
        extraIce.clear();
        allowRelay = false;
        if (iceJson.empty() || iceJson.size() > 16384) return;
        auto list = json::parse(iceJson);
        if (!list.is_array()) return;
        int turnCount = 0;
        for (const auto &entry : list) {
            if (!entry.is_object()) continue;
            std::string user = entry.value("username", ""), pass = entry.value("credential", "");
            std::vector<std::string> urls;
            if (entry.contains("urls") && entry["urls"].is_array()) {
                for (const auto &u : entry["urls"]) if (u.is_string()) urls.push_back(u.get<std::string>());
            } else if (entry.contains("urls") && entry["urls"].is_string()) {
                urls.push_back(entry["urls"].get<std::string>());
            } else if (entry.contains("url") && entry["url"].is_string()) {
                urls.push_back(entry["url"].get<std::string>());
            }
            for (const auto &url : urls) {
                try {
                    rtc::IceServer server(url);
                    if (server.type == rtc::IceServer::Type::Turn) {
                        if (server.relayType != rtc::IceServer::RelayType::TurnUdp || turnCount >= 2) continue;
                        server.username = user;
                        server.password = pass;
                        extraIce.push_back(std::move(server));
                        allowRelay = true;
                        ++turnCount;
                    } else {
                        extraIce.push_back(std::move(server));
                    }
                } catch (...) { }
            }
        }
    }
    std::shared_ptr<Session> create(const std::string &sid, const std::string &remote, int server) {
        if (sessions.size() >= 8) throw std::runtime_error("Too many pending direct connections");
        rtc::Configuration config;
        config.iceServers.emplace_back("stun:stun.l.google.com:19302");
        config.iceServers.emplace_back("stun:stun1.l.google.com:19302");
        for (const auto &ice : extraIce) config.iceServers.push_back(ice);
        config.maxMessageSize = 8192;
        auto s = std::make_shared<Session>(); s->id = sid; s->remote = remote; s->server = server;
        s->pc = std::make_shared<rtc::PeerConnection>(config); sessions[sid] = s;
        auto weak = weak_from_this();
        s->pc->onLocalDescription([weak, sid](rtc::Description description) {
            if (auto n = weak.lock()) {
                std::lock_guard<std::recursive_mutex> lock(n->gate);
                if (!n->alive || !n->sessions.count(sid)) return;
                auto s = n->sessions.at(sid); std::string sdp = description; s->localSdp = sdp;
                if (n->host) n->emit("sign", sid, {{"sdp", sdp}, {"remote", s->remote}});
                else {
                    try { n->sendSignal(s->server, "OFFER", s->remote, {{"type", "data"}, {"connectionId", sid}, {"label", "nightcall"}, {"serialization", "none"}, {"reliable", true}, {"sdp", {{"type", "offer"}, {"sdp", sdp}}}}); }
                    catch (const std::exception &e) { n->emit("error", sid, {{"error", e.what()}}); }
                }
            }
        });
        s->pc->onLocalCandidate([weak, sid](rtc::Candidate candidate) {
            if (auto n = weak.lock()) {
                std::lock_guard<std::recursive_mutex> lock(n->gate);
                if (!n->alive || !n->sessions.count(sid)) return;
                auto s = n->sessions.at(sid);
                try { n->sendSignal(s->server, "CANDIDATE", s->remote, {{"type", "data"}, {"connectionId", sid}, {"candidate", {{"candidate", std::string(candidate)}, {"sdpMid", candidate.mid()}, {"sdpMLineIndex", 0}}}}); }
                catch (...) { /* An established direct path does not need signaling. */ }
            }
        });
        s->pc->onDataChannel([weak, sid](std::shared_ptr<rtc::DataChannel> dc) {
            if (auto n = weak.lock()) { std::lock_guard<std::recursive_mutex> lock(n->gate); if (n->alive && n->sessions.count(sid)) n->bindData(n->sessions.at(sid), dc); }
        });
        s->pc->onStateChange([weak, sid](rtc::PeerConnection::State state) {
            if (auto n = weak.lock()) {
                if (state == rtc::PeerConnection::State::Failed || state == rtc::PeerConnection::State::Closed) n->emit("closed", sid);
            }
        });
        return s;
    }
    void signal(int server, const std::string &text) {
        std::lock_guard<std::recursive_mutex> lock(gate);
        if (!alive || text.size() > 131072) return;
        auto message = json::parse(text); auto type = message.value("type", "");
        if (type == "OPEN") { servers[server].ready = true; emit("registered", "", {{"server", server}}); return; }
        if (type == "HEARTBEAT") return;
        if (type == "ID-TAKEN" || type == "ERROR") {
            emit("server-error", "", {{"server", server}, {"error", type}});
            servers[server].ready = false; return;
        }
        if (!message.contains("payload")) return;
        auto payload = message["payload"]; std::string sid = payload.value("connectionId", ""), remote = message.value("src", "");
        if (sid.empty() || sid.size() > 128 || remote.empty() || remote.size() > 128) return;
        if (type == "OFFER" && host && !sessions.count(sid)) {
            auto s = create(sid, remote, server); s->accepted = true;
            s->pc->setRemoteDescription(rtc::Description(payload["sdp"]["sdp"].get<std::string>(), "offer"));
        } else if (type == "ANSWER" && !host && sessions.count(sid)) {
            auto s = sessions.at(sid); if (remote != s->remote) return;
            // The application verifies the persistent PC certificate and signature
            // before accepting the SDP fingerprint. Never trust the broker alone.
            emit("verify", sid, payload);
        } else if (type == "CANDIDATE" && sessions.count(sid) && payload.contains("candidate")) {
            auto s = sessions.at(sid); if (remote != s->remote) return;
            auto c = payload["candidate"]; std::string candidate = c.value("candidate", "");
            if (candidate.empty() || candidate.size() > 4096) return;
            rtc::Candidate parsed(candidate, c.value("sdpMid", "0"));
            if (s->accepted) s->pc->addRemoteCandidate(parsed); else if (s->candidates.size() < 64) s->candidates.push_back(parsed);
        }
    }

};
using Handle = std::shared_ptr<Net>;
API void *nightcall_net_create(const char *id, int host, const char *ca, const char *urls, Event event, void *context) {
    try {
        if (!id || !ca || !urls || !event) return nullptr;
        auto n = std::make_shared<Net>(); n->id = id; n->host = host != 0; n->ca = ca; n->event = event; n->context = context;
        auto list = json::parse(urls);
        if (!list.is_array() || list.empty() || list.size() > 4) return nullptr;
        for (const auto &url : list) { std::string value = url.get<std::string>(); if (value.rfind("wss://", 0) != 0) return nullptr; n->servers.push_back(Server{}); }
        return new Handle(n);
    } catch (...) { return nullptr; }
}
API int nightcall_net_set_ice(void *handle, const char *ice_json) {
    try { if (!handle || !ice_json) return -1; auto n = *static_cast<Handle *>(handle); n->setIce(ice_json); return 0; } catch (...) { return -1; }
}
API int nightcall_net_signal(void *handle, int server, const char *text) {
    try { if (!handle || !text) return -1; auto n = *static_cast<Handle *>(handle); n->signal(server, text); return 0; } catch (...) { return -1; }
}
API int nightcall_net_connect(void *handle, int server, const char *remote, const char *sid) {
    try {
        auto n = *static_cast<Handle *>(handle); std::lock_guard<std::recursive_mutex> lock(n->gate);
        if (!n->alive || n->host || !n->servers.at(server).ready) return -1;
        auto s = n->create(sid, remote, server); n->bindData(s, s->pc->createDataChannel("nightcall")); return 0;
    } catch (...) { return -1; }
}
API int nightcall_net_answer(void *handle, const char *sid, const char *certificate, const char *signature) {
    try {
        auto n = *static_cast<Handle *>(handle); std::lock_guard<std::recursive_mutex> lock(n->gate); auto s = n->sessions.at(sid);
        if (s->localSdp.empty() || !n->host) return -1;
        n->sendSignal(s->server, "ANSWER", s->remote, {{"type", "data"}, {"connectionId", sid}, {"sdp", {{"type", "answer"}, {"sdp", s->localSdp}}}, {"certificate", certificate}, {"signature", signature}}); return 0;
    } catch (...) { return -1; }
}
API int nightcall_net_accept(void *handle, const char *sid, const char *sdp) {
    try {
        auto n = *static_cast<Handle *>(handle); std::lock_guard<std::recursive_mutex> lock(n->gate); auto s = n->sessions.at(sid);
        if (n->host) return -1;
        s->pc->setRemoteDescription(rtc::Description(sdp, "answer")); s->accepted = true;
        for (auto &c : s->candidates) s->pc->addRemoteCandidate(c); s->candidates.clear(); return 0;
    } catch (...) { return -1; }
}
API int nightcall_net_send(void *handle, const char *sid, const unsigned char *bytes, int count) {
    try {
        auto n = *static_cast<Handle *>(handle); std::lock_guard<std::recursive_mutex> lock(n->gate); auto s = n->sessions.at(sid);
        if (!n->alive || !s->dc || !s->dc->isOpen() || count < 1 || count > 8192 || s->dc->bufferedAmount() > 131072) return -1;
        return s->dc->send(reinterpret_cast<const rtc::byte *>(bytes), count) ? 0 : -1;
    } catch (...) { return -1; }
}
API void nightcall_net_close(void *handle, const char *sid) {
    try {
        auto n = *static_cast<Handle *>(handle); std::shared_ptr<Session> s;
        { std::lock_guard<std::recursive_mutex> lock(n->gate); auto i = n->sessions.find(sid); if (i == n->sessions.end()) return; s = i->second; n->sessions.erase(i); }
        s->pc->close();
    } catch (...) {}
}
API void nightcall_net_destroy(void *handle) {
    if (!handle) return; auto owner = static_cast<Handle *>(handle); auto n = *owner;
    std::map<std::string, std::shared_ptr<Session>> sessions;
    { std::lock_guard<std::recursive_mutex> lock(n->gate); n->alive = false; sessions.swap(n->sessions); }
    for (auto &s : sessions) s.second->pc->close(); delete owner;
}

#ifdef __ANDROID__
#include <jni.h>
struct JavaNet { void *net = nullptr; JavaVM *vm = nullptr; jobject instance = nullptr; jmethodID callback = nullptr; };
static void javaEvent(void *context, const char *kind, const char *session, const char *body, const unsigned char *data, int count) {
    auto owner = static_cast<JavaNet *>(context); JNIEnv *env = nullptr; bool detach = false;
    if (owner->vm->GetEnv(reinterpret_cast<void **>(&env), JNI_VERSION_1_6) != JNI_OK) {
        if (owner->vm->AttachCurrentThread(&env, nullptr) != JNI_OK) return; detach = true;
    }
    jstring k = env->NewStringUTF(kind), s = env->NewStringUTF(session), b = env->NewStringUTF(body);
    jbyteArray packet = nullptr;
    if (data && count > 0) { packet = env->NewByteArray(count); if (packet) env->SetByteArrayRegion(packet, 0, count, reinterpret_cast<const jbyte *>(data)); }
    if (k && s && b) env->CallVoidMethod(owner->instance, owner->callback, k, s, b, packet);
    if (env->ExceptionCheck()) env->ExceptionClear();
    if (k) env->DeleteLocalRef(k); if (s) env->DeleteLocalRef(s); if (b) env->DeleteLocalRef(b); if (packet) env->DeleteLocalRef(packet);
    if (detach) owner->vm->DetachCurrentThread();
}
struct JString {
    JNIEnv *env; jstring value; const char *text;
    JString(JNIEnv *e, jstring s) : env(e), value(s), text(e->GetStringUTFChars(s, nullptr)) {}
    ~JString() { if (text) env->ReleaseStringUTFChars(value, text); }
};
extern "C" JNIEXPORT jlong JNICALL Java_local_dictationrelay_NativePeer_nativeCreate(JNIEnv *env, jobject self, jstring id, jstring ca, jstring urls) {
    auto owner = new JavaNet(); env->GetJavaVM(&owner->vm); owner->instance = env->NewGlobalRef(self);
    auto cls = env->GetObjectClass(self); owner->callback = env->GetMethodID(cls, "onNativeEvent", "(Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;[B)V"); env->DeleteLocalRef(cls);
    JString i(env, id), c(env, ca), u(env, urls);
    if (owner->callback) owner->net = nightcall_net_create(i.text, 0, c.text, u.text, javaEvent, owner);
    if (!owner->net) { env->DeleteGlobalRef(owner->instance); delete owner; return 0; }
    return reinterpret_cast<jlong>(owner);
}
extern "C" JNIEXPORT jint JNICALL Java_local_dictationrelay_NativePeer_nativeSetIce(JNIEnv *env, jclass, jlong h, jstring iceJson) {
    if (!h || !iceJson) return -1; JString j(env, iceJson); return nightcall_net_set_ice(reinterpret_cast<JavaNet *>(h)->net, j.text);
}
extern "C" JNIEXPORT jint JNICALL Java_local_dictationrelay_NativePeer_nativeSignal(JNIEnv *env, jclass, jlong h, jint server, jstring text) {
    if (!h) return -1; JString t(env, text); return nightcall_net_signal(reinterpret_cast<JavaNet *>(h)->net, server, t.text);
}
extern "C" JNIEXPORT jint JNICALL Java_local_dictationrelay_NativePeer_nativeConnect(JNIEnv *env, jclass, jlong h, jint server, jstring remote, jstring sid) {
    JString r(env, remote), s(env, sid); return nightcall_net_connect(reinterpret_cast<JavaNet *>(h)->net, server, r.text, s.text);
}
extern "C" JNIEXPORT jint JNICALL Java_local_dictationrelay_NativePeer_nativeAccept(JNIEnv *env, jclass, jlong h, jstring sid, jstring sdp) {
    JString s(env, sid), d(env, sdp); return nightcall_net_accept(reinterpret_cast<JavaNet *>(h)->net, s.text, d.text);
}
extern "C" JNIEXPORT jint JNICALL Java_local_dictationrelay_NativePeer_nativeSend(JNIEnv *env, jclass, jlong h, jstring sid, jbyteArray packet) {
    if (!h || !packet) return -1; jsize count = env->GetArrayLength(packet); if (count < 1 || count > 8192) return -1;
    JString s(env, sid); jbyte *data = env->GetByteArrayElements(packet, nullptr); if (!data) return -1;
    int result = nightcall_net_send(reinterpret_cast<JavaNet *>(h)->net, s.text, reinterpret_cast<unsigned char *>(data), count);
    env->ReleaseByteArrayElements(packet, data, JNI_ABORT); return result;
}
extern "C" JNIEXPORT void JNICALL Java_local_dictationrelay_NativePeer_nativeClose(JNIEnv *env, jclass, jlong h, jstring sid) {
    if (!h) return; JString s(env, sid); nightcall_net_close(reinterpret_cast<JavaNet *>(h)->net, s.text);
}
extern "C" JNIEXPORT void JNICALL Java_local_dictationrelay_NativePeer_nativeDestroy(JNIEnv *env, jclass, jlong h) {
    if (!h) return; auto owner = reinterpret_cast<JavaNet *>(h); nightcall_net_destroy(owner->net); env->DeleteGlobalRef(owner->instance); delete owner;
}
#endif

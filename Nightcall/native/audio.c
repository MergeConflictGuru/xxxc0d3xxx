#include <stdint.h>
#include <opus.h>
#if defined(_WIN32)
#define API __declspec(dllexport)
#else
#define API __attribute__((visibility("default")))
#endif

/* The encoded wire frame is exactly 20 ms of 48 kHz stereo audio at 96 kbit/s. */
API void *nightcall_encoder_create(void) {
    int error = 0;
    OpusEncoder *encoder = opus_encoder_create(48000, 2, OPUS_APPLICATION_AUDIO, &error);
    if (!encoder || error != OPUS_OK) return 0;
    if (opus_encoder_ctl(encoder, OPUS_SET_BITRATE(96000)) != OPUS_OK ||
        opus_encoder_ctl(encoder, OPUS_SET_COMPLEXITY(5)) != OPUS_OK ||
        opus_encoder_ctl(encoder, OPUS_SET_FORCE_CHANNELS(2)) != OPUS_OK) {
        opus_encoder_destroy(encoder); return 0;
    }
    return encoder;
}
API int nightcall_encode(void *encoder, const int16_t *pcm, unsigned char *packet, int capacity) {
    if (!encoder || !pcm || !packet || capacity < 1 || capacity > 1275) return OPUS_BAD_ARG;
    return opus_encode(encoder, pcm, 960, packet, capacity);
}
API void nightcall_encoder_destroy(void *encoder) { if (encoder) opus_encoder_destroy(encoder); }
API void *nightcall_decoder_create(void) {
    int error = 0; OpusDecoder *decoder = opus_decoder_create(48000, 2, &error);
    if (error != OPUS_OK) { if (decoder) opus_decoder_destroy(decoder); return 0; }
    return decoder;
}
API int nightcall_decode(void *decoder, const unsigned char *packet, int length, int16_t *pcm) {
    if (!decoder || !packet || !pcm || length < 1 || length > 1275) return OPUS_BAD_ARG;
    if (opus_packet_get_nb_samples(packet, length, 48000) != 960) return OPUS_BAD_ARG;
    return opus_decode(decoder, packet, length, pcm, 960, 0);
}
API void nightcall_decoder_destroy(void *decoder) { if (decoder) opus_decoder_destroy(decoder); }

API void *nightcall_decoder_mono_create(void) {
    int error = 0; OpusDecoder *decoder = opus_decoder_create(16000, 1, &error);
    if (error != OPUS_OK) { if (decoder) opus_decoder_destroy(decoder); return 0; }
    return decoder;
}
API int nightcall_decode_mono(void *decoder, const unsigned char *packet, int length, int16_t *pcm) {
    if (!decoder || !packet || !pcm || length < 1 || length > 1275 || opus_packet_get_nb_samples(packet, length, 16000) != 320) return OPUS_BAD_ARG;
    return opus_decode(decoder, packet, length, pcm, 320, 0);
}

#ifdef __ANDROID__
#include <jni.h>
JNIEXPORT jlong JNICALL Java_local_dictationrelay_OpusEncoder_create(JNIEnv *env, jclass cls) {
    (void)env; (void)cls; return (jlong)(uintptr_t)nightcall_encoder_create();
}
JNIEXPORT jbyteArray JNICALL Java_local_dictationrelay_OpusEncoder_encodeFrame(JNIEnv *env, jclass cls, jlong handle, jbyteArray input) {
    (void)cls;
    if (!handle || (*env)->GetArrayLength(env, input) != 3840) return 0;
    unsigned char raw[3840], packet[1275]; int16_t pcm[1920];
    (*env)->GetByteArrayRegion(env, input, 0, 3840, (jbyte *)raw);
    if ((*env)->ExceptionCheck(env)) return 0;
    for (int i = 0; i < 1920; i++) pcm[i] = (int16_t)((unsigned int)raw[2*i] | ((unsigned int)raw[2*i+1] << 8));
    int length = nightcall_encode((void *)(uintptr_t)handle, pcm, packet, sizeof(packet));
    if (length < 1) return 0;
    jbyteArray result = (*env)->NewByteArray(env, length);
    if (result) (*env)->SetByteArrayRegion(env, result, 0, length, (jbyte *)packet);
    return result;
}
JNIEXPORT void JNICALL Java_local_dictationrelay_OpusEncoder_destroy(JNIEnv *env, jclass cls, jlong handle) {
    (void)env; (void)cls; nightcall_encoder_destroy((void *)(uintptr_t)handle);
}
#endif

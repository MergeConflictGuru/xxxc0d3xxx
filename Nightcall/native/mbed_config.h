#ifndef NIGHTCALL_MBED_CONFIG_H
#define NIGHTCALL_MBED_CONFIG_H
/* libdatachannel's DTLS backend references this extension even in data-only builds. */
#define MBEDTLS_SSL_DTLS_SRTP
#endif

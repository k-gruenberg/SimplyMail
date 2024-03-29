// This file was autogenerated by some hot garbage in the `uniffi` crate.
// Trust me, you don't want to mess with it!

#pragma once

#include <stdbool.h>
#include <stdint.h>

// The following structs are used to implement the lowest level
// of the FFI, and thus useful to multiple uniffied crates.
// We ensure they are declared exactly once, with a header guard, UNIFFI_SHARED_H.
#ifdef UNIFFI_SHARED_H
    // We also try to prevent mixing versions of shared uniffi header structs.
    // If you add anything to the #else block, you must increment the version suffix in UNIFFI_SHARED_HEADER_V4
    #ifndef UNIFFI_SHARED_HEADER_V4
        #error Combining helper code from multiple versions of uniffi is not supported
    #endif // ndef UNIFFI_SHARED_HEADER_V4
#else
#define UNIFFI_SHARED_H
#define UNIFFI_SHARED_HEADER_V4
// ⚠️ Attention: If you change this #else block (ending in `#endif // def UNIFFI_SHARED_H`) you *must* ⚠️
// ⚠️ increment the version suffix in all instances of UNIFFI_SHARED_HEADER_V4 in this file.           ⚠️

typedef struct RustBuffer
{
    int32_t capacity;
    int32_t len;
    uint8_t *_Nullable data;
} RustBuffer;

typedef int32_t (*ForeignCallback)(uint64_t, int32_t, RustBuffer, RustBuffer *_Nonnull);

typedef struct ForeignBytes
{
    int32_t len;
    const uint8_t *_Nullable data;
} ForeignBytes;

// Error definitions
typedef struct RustCallStatus {
    int8_t code;
    RustBuffer errorBuf;
} RustCallStatus;

// ⚠️ Attention: If you change this #else block (ending in `#endif // def UNIFFI_SHARED_H`) you *must* ⚠️
// ⚠️ increment the version suffix in all instances of UNIFFI_SHARED_HEADER_V4 in this file.           ⚠️
#endif // def UNIFFI_SHARED_H

void rust_lib_dab3_simply_check_imap(
      RustBuffer domain,uint16_t port,RustBuffer username,RustBuffer password,
    RustCallStatus *_Nonnull out_status
    );
RustBuffer rust_lib_dab3_simply_fetch_inbox_top(
      RustBuffer domain,uint16_t port,RustBuffer username,RustBuffer password,
    RustCallStatus *_Nonnull out_status
    );
int8_t rust_lib_dab3_simply_check_smtp(
      RustBuffer smtp_server,RustBuffer smtp_username,RustBuffer smtp_password,
    RustCallStatus *_Nonnull out_status
    );
RustBuffer rust_lib_dab3_simply_send_plain_text_email(
      RustBuffer smtp_server,RustBuffer smtp_username,RustBuffer smtp_password,RustBuffer headers,RustBuffer body,
    RustCallStatus *_Nonnull out_status
    );
RustBuffer rust_lib_dab3_simply_send_html_email(
      RustBuffer smtp_server,RustBuffer smtp_username,RustBuffer smtp_password,RustBuffer headers,RustBuffer plain_text_body,RustBuffer html_body,
    RustCallStatus *_Nonnull out_status
    );
RustBuffer ffi_rust_lib_dab3_rustbuffer_alloc(
      int32_t size,
    RustCallStatus *_Nonnull out_status
    );
RustBuffer ffi_rust_lib_dab3_rustbuffer_from_bytes(
      ForeignBytes bytes,
    RustCallStatus *_Nonnull out_status
    );
void ffi_rust_lib_dab3_rustbuffer_free(
      RustBuffer buf,
    RustCallStatus *_Nonnull out_status
    );
RustBuffer ffi_rust_lib_dab3_rustbuffer_reserve(
      RustBuffer buf,int32_t additional,
    RustCallStatus *_Nonnull out_status
    );

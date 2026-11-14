/*
 * sha_api.c — miscellaneous exports that are not part of any hot path.
 *
 * Everything here returns static storage: the managed side must never free
 * these pointers, and they stay valid for the process lifetime.
 */
#include "sha_internal.h"

uint32_t sha_abi_version(void) {
    return SHAGUI_ABI_VERSION;
}

const char *sha_algo_name(sha_algo_t algo) {
    switch (algo) {
        case SHA_ALGO_SHA1:   return "SHA-1";
        case SHA_ALGO_SHA256: return "SHA-256";
        default:              return "unknown";
    }
}

const char *sha_status_string(sha_status_t st) {
    switch (st) {
        case SHA_OK:                      return "OK";
        case SHA_ERR_INVALID_ARG:         return "invalid argument";
        case SHA_ERR_UNSUPPORTED:         return "unsupported algorithm";
        case SHA_ERR_IO:                  return "I/O error";
        case SHA_ERR_CANCELLED:           return "cancelled";
        case SHA_ERR_CHANGED:             return "file changed while hashing";
        case SHA_ERR_ACCESS:              return "access denied";
        case SHA_ERR_NOMEM:               return "out of memory";
        case SHA_ERR_STATE:               return "invalid context state";
        case SHA_ERR_OVERFLOW:            return "message too long";
        case SHA_ERR_TOO_LARGE_FOR_TRACE: return "input too large to trace";
        case SHA_ERR_PATH_TOO_LONG:       return "path too long";
        case SHA_ERR_UTF8:                return "invalid UTF-8 path";
        default:                          return "unknown status";
    }
}

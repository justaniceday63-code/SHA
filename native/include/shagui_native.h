/*
 * shagui_native.h — public C ABI of shaNative.dll
 *
 * SHA Integrity & Visualization Studio — native backend.
 *
 * Rules for this boundary (see docs/NATIVE_API.md):
 *   - Plain C only. No C++ types, no exceptions, no STL, no callbacks
 *     into managed code other than the explicitly declared progress hook.
 *   - Every struct that crosses the boundary is POD with an explicit size
 *     field or a fixed-width layout, so the C# side can marshal it safely.
 *   - All strings are UTF-8.
 *   - Calling convention is cdecl on x64 (the default), declared explicitly
 *     so the C# side can pin it.
 *
 * Derived from E:\Downloads\SHA1_256 (sha1.c / sha256.c); the compression
 * logic itself is unchanged, it only gained a nullable trace sink.
 */
#ifndef SHAGUI_NATIVE_H
#define SHAGUI_NATIVE_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * SHAGUI_BUILD  — defined while compiling the DLL itself.
 * SHAGUI_STATIC — defined when the sources are compiled straight into a
 *                 test executable instead of being linked against the DLL,
 *                 so no import library is needed.
 */
#if defined(SHAGUI_STATIC)
#  define SHAGUI_API
#elif defined(_WIN32) || defined(_WIN64)
#  ifdef SHAGUI_BUILD
#    define SHAGUI_API __declspec(dllexport)
#  else
#    define SHAGUI_API __declspec(dllimport)
#  endif
#else
#  define SHAGUI_API
#endif

/* Bump on any breaking change to sha_trace_step_t or to an exported
 * signature. The managed side asserts equality at startup. */
#define SHAGUI_ABI_VERSION 1u

/* Digest sizes in bytes. */
#define SHA1_DIGEST_LEN   20u
#define SHA256_DIGEST_LEN 32u

/* Default streaming buffer used by the file layer (bytes). Exposed so the
 * Settings page and the native default cannot drift apart. */
#define SHA_DEFAULT_BUFFER_SIZE (1u << 20) /* 1 MiB */

/* ---------------------------------------------------------------------- */
/* Algorithms and status codes                                             */
/* ---------------------------------------------------------------------- */

typedef enum sha_algo {
    SHA_ALGO_SHA1   = 1,
    SHA_ALGO_SHA256 = 2
} sha_algo_t;

typedef enum sha_status {
    SHA_OK                      = 0,
    SHA_ERR_INVALID_ARG         = 1,
    SHA_ERR_UNSUPPORTED         = 2,
    SHA_ERR_IO                  = 3,
    SHA_ERR_CANCELLED           = 4,
    SHA_ERR_CHANGED             = 5,
    SHA_ERR_ACCESS              = 6,
    SHA_ERR_NOMEM               = 7,
    SHA_ERR_STATE               = 8,
    SHA_ERR_OVERFLOW            = 9,
    SHA_ERR_TOO_LARGE_FOR_TRACE = 10,
    SHA_ERR_PATH_TOO_LONG       = 11,
    SHA_ERR_UTF8                = 12
} sha_status_t;

/*
 * Status precedence when several conditions apply at once — this is what the
 * Verify / Batch UI renders:
 *
 *     INVALID_ARG > CANCELLED > IO > ACCESS > CHANGED > OK
 *
 * A folder run that skipped anything at all never returns SHA_OK.
 */

/* ---------------------------------------------------------------------- */
/* Core streaming context                                                  */
/* ---------------------------------------------------------------------- */

/* Opaque. One context = one owner = one thread. Not thread-safe; the only
 * object shared across threads is sha_cancel_t, and only via the exported
 * helpers below. */
typedef struct sha_ctx sha_ctx_t;

SHAGUI_API sha_status_t sha_create(sha_algo_t algo, sha_ctx_t **out);
SHAGUI_API void         sha_destroy(sha_ctx_t *ctx);
SHAGUI_API sha_status_t sha_reset(sha_ctx_t *ctx);

/* len is uint64_t so a caller can feed more than 4 GiB across calls. */
SHAGUI_API sha_status_t sha_update(sha_ctx_t *ctx, const void *data, uint64_t len);

/* Writes digest_len bytes into out. ctx is single-use afterwards until reset. */
SHAGUI_API sha_status_t sha_final(sha_ctx_t *ctx, uint8_t *out, size_t cap,
                                  size_t *written);

/* Convenience one-shot over a buffer. Replaces the original sha1()/sha256(). */
SHAGUI_API sha_status_t sha_hash_buffer(sha_algo_t algo, const void *data,
                                        uint64_t len, uint8_t *out, size_t cap,
                                        size_t *written);

SHAGUI_API size_t      sha_digest_length(sha_algo_t algo);
SHAGUI_API const char *sha_algo_name(sha_algo_t algo);      /* static, do not free */
SHAGUI_API const char *sha_status_string(sha_status_t st);  /* static, do not free */
SHAGUI_API uint32_t    sha_abi_version(void);

/* ---------------------------------------------------------------------- */
/* Cancellation — every mutation goes through the DLL                      */
/* ---------------------------------------------------------------------- */

/*
 * The managed side must never poke .flag directly with Marshal.WriteInt32:
 * that has no ordering guarantee against a concurrent native read. Call
 * sha_cancel_request() instead; it InterlockedExchange()es inside the DLL.
 *
 * The struct is caller-allocated (it is plain storage), but only the three
 * functions below may touch it.
 */
typedef struct sha_cancel {
    volatile long flag;
} sha_cancel_t;

SHAGUI_API void sha_cancel_request(sha_cancel_t *c);
SHAGUI_API void sha_cancel_clear(sha_cancel_t *c);
SHAGUI_API int  sha_cancelled(const sha_cancel_t *c);

/* ---------------------------------------------------------------------- */
/* Progress reporting                                                      */
/* ---------------------------------------------------------------------- */

typedef enum sha_stage {
    /* Emitted by the managed folder walker, not by C. */
    SHA_STAGE_ENUMERATING = 0,
    /* Emitted by sha_hash_file(). */
    SHA_STAGE_HASHING     = 1,
    SHA_STAGE_FINALIZING  = 2
} sha_stage_t;

#define SHA_PROGRESS_PATH_LEN 1024

typedef struct sha_progress {
    uint32_t    struct_size;      /* caller sets this to sizeof(sha_progress_t) */
    sha_stage_t stage;
    uint64_t    file_bytes_done;
    uint64_t    file_bytes_total;
    uint64_t    bytes_done;       /* == file_* for single-file calls */
    uint64_t    bytes_total;
    uint32_t    path_truncated;   /* 1 when current_path had to be cut */
    uint32_t    reserved;
    char        current_path[SHA_PROGRESS_PATH_LEN]; /* UTF-8, may be truncated */
} sha_progress_t;

/*
 * Called from the hashing thread. Return non-zero to cancel the operation
 * (equivalent to sha_cancel_request). The delegate must stay alive for the
 * whole call — pin it with GCHandle.
 *
 * C throttles this to ~10 Hz internally; callers do not need to.
 */
typedef int (__cdecl *sha_progress_cb)(const sha_progress_t *p, void *user);

/* ---------------------------------------------------------------------- */
/* File hashing                                                            */
/* ---------------------------------------------------------------------- */

/*
 * Streams utf8_path through the algorithm using buf_size bytes (0 =>
 * SHA_DEFAULT_BUFFER_SIZE). Times only the read+hash work, so the returned
 * elapsed value excludes UI, enumeration and rendering.
 *
 * elapsed_100ns is optional; when non-NULL it receives 100 ns units
 * (FILETIME granularity) measured with QueryPerformanceCounter.
 */
SHAGUI_API sha_status_t sha_hash_file(const char *utf8_path, sha_algo_t algo,
                                      uint64_t buf_size,
                                      sha_progress_cb cb, void *cb_user,
                                      sha_cancel_t *cancel,
                                      uint8_t *out, size_t cap,
                                      uint64_t *elapsed_100ns);

/* ---------------------------------------------------------------------- */
/* Educational trace                                                       */
/* ---------------------------------------------------------------------- */

typedef enum sha_trace_stage {
    SHA_TRACE_NONE         = 0,
    /* Stage 1b: raw bytes of the block decoded into big-endian words.
     * (Stage 1a, text -> UTF-8, happens in the managed layer and is shown
     * there; C never sees the .NET string.) */
    SHA_TRACE_INPUT_WORDS  = 1,
    SHA_TRACE_PADDING      = 2,
    SHA_TRACE_BLOCK_SPLIT  = 3,
    SHA_TRACE_SCHEDULE     = 4,
    SHA_TRACE_COMPRESSION  = 5,
    SHA_TRACE_STATE_UPDATE = 6,
    SHA_TRACE_DIGEST       = 7
} sha_trace_stage_t;

/* aux[] payload by stage:
 *   COMPRESSION  (SHA-1) aux[0]=temp aux[1]=f aux[2]=K aux[3]=W[t]
 *   COMPRESSION  (SHA-256) aux[0]=T1 aux[1]=T2 aux[2]=Sigma0(a)
 *                          aux[3]=CH aux[4]=Sigma1(e) aux[5]=MAJ
 *                          aux[6]=K aux[7]=W[i]
 *   SCHEDULE     aux[0..3] = the four operands that produced W[t]
 *   PADDING      (pad_len carries the count, msg_bits the original length)
 *   others       reserved, zero
 */
typedef struct sha_trace_step {
    uint32_t stage;          /* sha_trace_stage_t */
    uint32_t block;          /* 0-based block index this step belongs to */
    uint32_t round;          /* 0..79 / 0..63, else 0xFFFFFFFF */
    uint32_t w_index;        /* word index the step refers to, else 0xFFFFFFFF */
    uint32_t words[8];       /* A..H *after* this step (SHA-1 uses [0..4]) */
    uint32_t words_before[8];/* A..H *before* this step                */
    uint32_t aux[8];
    uint64_t msg_bits;       /* stage 2: original message length in bits */
    uint32_t pad_len;        /* stage 2: padding bytes written           */
    uint32_t flags;
} sha_trace_step_t; /* 128 bytes on x64 — frozen at SHAGUI_ABI_VERSION 1 */

/*
 * Scope the trace to a block range instead of the whole message. A 1 MiB
 * message would be 16384 blocks x ~144 steps x 128 B = ~300 MB, so tracing
 * is deliberately limited to selected blocks (default: block 0) and to
 * SHA_TRACE_MAX_STEPS total steps.
 */
#define SHA_TRACE_MAX_STEPS 100000u

SHAGUI_API sha_status_t sha_trace_configure(sha_ctx_t *ctx, int enable,
                                            uint32_t first_block,
                                            uint32_t block_count);

SHAGUI_API size_t              sha_trace_count(const sha_ctx_t *ctx);
/* Contiguous array, valid until the next trace call or sha_destroy().
 * One P/Invoke for the whole trace — never call this per step in a loop. */
SHAGUI_API const sha_trace_step_t *sha_trace_data(const sha_ctx_t *ctx);
SHAGUI_API sha_status_t        sha_trace_get(const sha_ctx_t *ctx, size_t i,
                                             sha_trace_step_t *out);

#ifdef __cplusplus
}
#endif

#endif /* SHAGUI_NATIVE_H */

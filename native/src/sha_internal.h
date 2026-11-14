/*
 * sha_internal.h — private definitions shared by the native backend.
 *
 * Not installed, not part of the ABI. The managed side only ever sees
 * shagui_native.h.
 */
#ifndef SHA_INTERNAL_H
#define SHA_INTERNAL_H

#include "shagui_native.h"

/* ---------------------------------------------------------------------- */
/* Bit helpers — carried over unchanged from E:\Downloads\SHA1_256         */
/* ---------------------------------------------------------------------- */

#define ROTL32(x, n) (((x) << (n)) | ((x) >> (32 - (n))))
#define ROTR32(x, n) (((x) >> (n)) | ((x) << (32 - (n))))
#define SHR32(x, n)  ((x) >> (n))

static inline uint32_t sha_load_be32(const uint8_t *p) {
    return ((uint32_t)p[0] << 24) |
           ((uint32_t)p[1] << 16) |
           ((uint32_t)p[2] << 8)  |
           ((uint32_t)p[3]);
}

static inline void sha_store_be32(uint8_t *p, uint32_t v) {
    p[0] = (uint8_t)(v >> 24);
    p[1] = (uint8_t)(v >> 16);
    p[2] = (uint8_t)(v >> 8);
    p[3] = (uint8_t)v;
}

/* ---------------------------------------------------------------------- */
/* Trace storage (sha_trace.c)                                             */
/* ---------------------------------------------------------------------- */

typedef struct sha_trace sha_trace_t;

/* All of these tolerate tr == NULL, so the hot path is a single predictable
 * branch when tracing is off. */
sha_trace_t         *sha_trace_new(uint32_t first_block, uint32_t block_count);
void                 sha_trace_free(sha_trace_t *tr);
void                 sha_trace_clear(sha_trace_t *tr);   /* keep config, drop steps */
int                  sha_trace_wants_block(const sha_trace_t *tr, uint32_t block);
void                 sha_trace_push(sha_trace_t *tr, const sha_trace_step_t *s);
size_t               sha_trace_stored(const sha_trace_t *tr);
const sha_trace_step_t *sha_trace_stored_data(const sha_trace_t *tr);

/* ---------------------------------------------------------------------- */
/* Context (sha_ctx.c)                                                     */
/* ---------------------------------------------------------------------- */

struct sha_ctx {
    sha_algo_t algo;
    uint32_t   state[8];     /* SHA-1 uses [0..4] only            */
    uint64_t   total_len;    /* bytes accepted by sha_update      */
    uint8_t    buf[64];      /* partial block held across updates */
    uint32_t   buf_len;
    int        finalized;
    int        reset_done;
    uint32_t   block_index;  /* 0-based index of the next block   */
    sha_trace_t *trace;
};

/* ---------------------------------------------------------------------- */
/* Compression functions                                                   */
/* ---------------------------------------------------------------------- */

/* The round logic is the original code from E:\Downloads\SHA1_256; the only
 * change is the nullable trace sink and the block index used to tag steps. */
void sha1_compress(uint32_t state[5], const uint8_t block[64],
                   sha_trace_t *tr, uint32_t block_index);

void sha256_compress(uint32_t state[8], const uint8_t block[64],
                     sha_trace_t *tr, uint32_t block_index);

void sha1_init_state(uint32_t state[5]);
void sha256_init_state(uint32_t state[8]);

/* Shared by both: emits the SHA_TRACE_BLOCK_SPLIT step for a block. */
void sha_trace_block_split(sha_trace_t *tr, uint32_t block_index,
                           uint64_t block_bits);

#endif /* SHA_INTERNAL_H */

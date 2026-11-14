/*
 * sha_ctx.c — streaming Init / Update / Final.
 *
 * This is the file that replaces the original one-shot sha1() and sha256()
 * wrappers from E:\Downloads\SHA1_256. Those wrappers calloc'd a padded copy
 * of the entire message, which made inputs larger than RAM impossible. Here
 * the message is consumed 64 bytes at a time out of a fixed 64-byte buffer,
 * so memory use is constant regardless of input size.
 *
 * Padding (FIPS 180-4 §5.1 / §5.2) is performed once, in sha_final():
 *   1. append a single 1 bit          -> 0x80 byte
 *   2. append k zero bits so that the length is 56 mod 64
 *   3. append the original length as a 64-bit big-endian bit count
 */
#include "sha_internal.h"

#include <stdlib.h>
#include <string.h>

/* FIPS 180-4 limits the message length to 2^64 - 1 bits, i.e. 2^61 - 1
 * bytes. Refusing earlier keeps the bit-length computation exact. */
#define SHA_MAX_MESSAGE_BYTES ((uint64_t)0x1FFFFFFFFFFFFFFF) /* 2^61 - 1 */

static int algo_valid(sha_algo_t algo) {
    return algo == SHA_ALGO_SHA1 || algo == SHA_ALGO_SHA256;
}

size_t sha_digest_length(sha_algo_t algo) {
    switch (algo) {
        case SHA_ALGO_SHA1:   return SHA1_DIGEST_LEN;
        case SHA_ALGO_SHA256: return SHA256_DIGEST_LEN;
        default:              return 0;
    }
}

static void ctx_init_state(sha_ctx_t *c) {
    if (c->algo == SHA_ALGO_SHA1) {
        sha1_init_state(c->state);
    } else {
        sha256_init_state(c->state);
    }
    c->total_len   = 0;
    c->buf_len     = 0;
    c->block_index = 0;
    c->finalized   = 0;
    memset(c->buf, 0, sizeof(c->buf));
}

sha_status_t sha_create(sha_algo_t algo, sha_ctx_t **out) {
    if (out == NULL) {
        return SHA_ERR_INVALID_ARG;
    }
    *out = NULL;
    if (!algo_valid(algo)) {
        return SHA_ERR_UNSUPPORTED;
    }

    sha_ctx_t *c = (sha_ctx_t *)calloc(1, sizeof(sha_ctx_t));
    if (c == NULL) {
        return SHA_ERR_NOMEM;
    }
    c->algo = algo;
    ctx_init_state(c);

    *out = c;
    return SHA_OK;
}

void sha_destroy(sha_ctx_t *ctx) {
    if (ctx == NULL) {
        return;
    }
    sha_trace_free(ctx->trace);
    /* Wipe: the buffer may hold a fragment of sensitive input. */
    memset(ctx, 0, sizeof(*ctx));
    free(ctx);
}

sha_status_t sha_reset(sha_ctx_t *ctx) {
    if (ctx == NULL) {
        return SHA_ERR_INVALID_ARG;
    }
    ctx_init_state(ctx);
    /* Keep the trace *configuration* (block range) but drop the previous
     * run's steps, so configure-then-run-then-run-again behaves sanely. */
    sha_trace_clear(ctx->trace);
    return SHA_OK;
}

/* Compress one already-full block and advance the block counter. */
static void compress_block(sha_ctx_t *c, const uint8_t *block) {
    if (c->algo == SHA_ALGO_SHA1) {
        sha1_compress(c->state, block, c->trace, c->block_index);
    } else {
        sha256_compress(c->state, block, c->trace, c->block_index);
    }
    c->block_index++;
}

sha_status_t sha_update(sha_ctx_t *ctx, const void *data, uint64_t len) {
    if (ctx == NULL) {
        return SHA_ERR_INVALID_ARG;
    }
    if (ctx->finalized) {
        return SHA_ERR_STATE;                 /* final already consumed it */
    }
    if (len > 0 && data == NULL) {
        return SHA_ERR_INVALID_ARG;
    }
    if (len > SHA_MAX_MESSAGE_BYTES ||
        ctx->total_len > SHA_MAX_MESSAGE_BYTES - len) {
        return SHA_ERR_OVERFLOW;              /* would overflow 2^64 bits */
    }

    const uint8_t *p = (const uint8_t *)data;
    uint64_t remaining = len;

    /* 1. Top up the partial block held from the previous call. */
    if (ctx->buf_len > 0 && remaining > 0) {
        uint64_t space = 64u - (uint64_t)ctx->buf_len;
        uint64_t take  = (remaining < space) ? remaining : space;
        memcpy(ctx->buf + ctx->buf_len, p, (size_t)take);
        ctx->buf_len += (uint32_t)take;
        p        += take;
        remaining -= take;

        if (ctx->buf_len == 64u) {
            compress_block(ctx, ctx->buf);
            ctx->buf_len = 0;
        }
    }

    /* 2. Consume whole blocks straight from the caller's buffer — no copy,
     *    which is what keeps large files cheap. */
    while (remaining >= 64u) {
        compress_block(ctx, p);
        p        += 64;
        remaining -= 64;
    }

    /* 3. Stash the tail for the next call / sha_final(). */
    if (remaining > 0) {
        memcpy(ctx->buf, p, (size_t)remaining);
        ctx->buf_len = (uint32_t)remaining;
    }

    ctx->total_len += len;
    return SHA_OK;
}

/* Describe the padding as trace steps so the UI can show exactly what
 * sha_final() is about to do (spec §7.2 stage 2). */
static void trace_padding(sha_ctx_t *c, uint64_t bit_len,
                          size_t original_tail) {
    sha_trace_t *tr = c->trace;
    if (!sha_trace_wants_block(tr, c->block_index)) {
        return;
    }

    sha_trace_step_t s;

    /* (a) the original length, before anything is appended */
    memset(&s, 0, sizeof(s));
    s.stage    = SHA_TRACE_PADDING;
    s.block    = c->block_index;
    s.round    = 0xFFFFFFFFu;
    s.w_index  = 0xFFFFFFFFu;
    s.msg_bits = bit_len;
    s.pad_len  = (uint32_t)original_tail;
    s.flags    = 0;                       /* 0 = "original message" */
    sha_trace_push(tr, &s);

    /* (b) the appended 1 bit (0x80) */
    s.flags = 1;                          /* 1 = "append 1 bit" */
    sha_trace_push(tr, &s);

    /* (c) the zero fill */
    s.flags = 2;                          /* 2 = "append 0 bits" */
    sha_trace_push(tr, &s);

    /* (d) the 64-bit original length */
    s.flags = 3;                          /* 3 = "append 64-bit length" */
    sha_trace_push(tr, &s);
}

sha_status_t sha_final(sha_ctx_t *ctx, uint8_t *out, size_t cap,
                       size_t *written) {
    if (ctx == NULL || out == NULL) {
        return SHA_ERR_INVALID_ARG;
    }
    if (ctx->finalized) {
        return SHA_ERR_STATE;
    }

    const size_t need = sha_digest_length(ctx->algo);
    if (need == 0) {
        return SHA_ERR_UNSUPPORTED;
    }
    if (cap < need) {
        return SHA_ERR_INVALID_ARG;
    }

    const uint64_t bit_len = ctx->total_len * 8u;
    const size_t   original_tail = ctx->buf_len;

    trace_padding(ctx, bit_len, original_tail);

    /* --- padding step 1: append the 1 bit ---------------------------- */
    ctx->buf[ctx->buf_len++] = 0x80u;

    /* --- padding step 2: zero fill up to 56 mod 64 ------------------- */
    if (ctx->buf_len > 56u) {
        memset(ctx->buf + ctx->buf_len, 0, 64u - ctx->buf_len);
        compress_block(ctx, ctx->buf);
        ctx->buf_len = 0;
    }
    memset(ctx->buf + ctx->buf_len, 0, 56u - ctx->buf_len);

    /* --- padding step 3: 64-bit big-endian bit length ---------------- */
    for (int i = 0; i < 8; i++) {
        ctx->buf[56 + i] = (uint8_t)(bit_len >> (56 - 8 * i));
    }
    compress_block(ctx, ctx->buf);
    ctx->buf_len = 0;
    ctx->finalized = 1;

    /* --- stage 7: serialize the chaining state ----------------------- */
    const uint32_t *st = ctx->state;
    const int words = (ctx->algo == SHA_ALGO_SHA1) ? 5 : 8;
    for (int i = 0; i < words; i++) {
        sha_store_be32(out + (size_t)i * 4u, st[i]);
    }

    if (sha_trace_wants_block(ctx->trace, ctx->block_index > 0
                                ? ctx->block_index - 1
                                : 0)) {
        sha_trace_step_t s;
        memset(&s, 0, sizeof(s));
        s.stage    = SHA_TRACE_DIGEST;
        s.block    = (ctx->block_index > 0) ? ctx->block_index - 1 : 0;
        s.round    = 0xFFFFFFFFu;
        s.w_index  = 0xFFFFFFFFu;
        s.msg_bits = bit_len;
        for (int i = 0; i < words; i++) {
            s.words[i] = st[i];
        }
        sha_trace_push(ctx->trace, &s);
    }

    if (written != NULL) {
        *written = need;
    }
    return SHA_OK;
}

sha_status_t sha_hash_buffer(sha_algo_t algo, const void *data, uint64_t len,
                             uint8_t *out, size_t cap, size_t *written) {
    sha_ctx_t *ctx = NULL;
    sha_status_t st = sha_create(algo, &ctx);
    if (st != SHA_OK) {
        return st;
    }

    st = sha_update(ctx, data, len);
    if (st == SHA_OK) {
        st = sha_final(ctx, out, cap, written);
    }

    sha_destroy(ctx);
    return st;
}

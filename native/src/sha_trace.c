/*
 * sha_trace.c — optional educational trace storage.
 *
 * Design constraints (see docs/ARCHITECTURE.md):
 *   - Disabled by default; the hot path is `if (ctx->trace)`.
 *   - Scoped to a block range, not the whole message. Tracing a 1 MiB input
 *     wholesale would be ~300 MB of steps, so callers select blocks
 *     (default block 0) and SHA_TRACE_MAX_STEPS hard-caps the total.
 *   - Steps live in one contiguous array so the managed side performs a
 *     single P/Invoke instead of millions of per-step calls.
 *   - Everything is freed by sha_destroy(); the DLL owns the memory.
 */
#include "sha_internal.h"

#include <stdlib.h>
#include <string.h>

struct sha_trace {
    sha_trace_step_t *steps;
    size_t    count;
    size_t    capacity;
    uint32_t  first_block;
    uint32_t  block_count;   /* 0 == unlimited (still capped by MAX_STEPS) */
    int       overflowed;
};

sha_trace_t *sha_trace_new(uint32_t first_block, uint32_t block_count) {
    sha_trace_t *tr = (sha_trace_t *)calloc(1, sizeof(*tr));
    if (tr == NULL) {
        return NULL;
    }
    tr->first_block = first_block;
    tr->block_count = block_count;
    return tr;
}

void sha_trace_free(sha_trace_t *tr) {
    if (tr == NULL) {
        return;
    }
    free(tr->steps);   /* may be NULL when nothing was recorded */
    free(tr);
}

int sha_trace_wants_block(const sha_trace_t *tr, uint32_t block) {
    if (tr == NULL) {
        return 0;
    }
    if (block < tr->first_block) {
        return 0;
    }
    if (tr->block_count != 0 &&
        block >= tr->first_block + tr->block_count) {
        return 0;
    }
    return 1;
}

void sha_trace_push(sha_trace_t *tr, const sha_trace_step_t *s) {
    if (tr == NULL || s == NULL) {
        return;                     /* tracing disabled: no work at all */
    }
    if (tr->overflowed) {
        return;
    }
    if (tr->count >= SHA_TRACE_MAX_STEPS) {
        tr->overflowed = 1;         /* stop recording rather than grow forever */
        return;
    }
    if (tr->count == tr->capacity) {
        size_t next = (tr->capacity == 0) ? 256 : tr->capacity * 2;
        if (next > SHA_TRACE_MAX_STEPS) {
            next = SHA_TRACE_MAX_STEPS;
        }
        sha_trace_step_t *grown = (sha_trace_step_t *)realloc(
            tr->steps, next * sizeof(sha_trace_step_t));
        if (grown == NULL) {
            tr->overflowed = 1;     /* degrade to "truncated", never fail hashing */
            return;
        }
        tr->steps   = grown;
        tr->capacity = next;
    }
    tr->steps[tr->count++] = *s;
}

size_t sha_trace_stored(const sha_trace_t *tr) {
    return (tr == NULL) ? 0 : tr->count;
}

const sha_trace_step_t *sha_trace_stored_data(const sha_trace_t *tr) {
    return (tr == NULL) ? NULL : tr->steps;
}

void sha_trace_block_split(sha_trace_t *tr, uint32_t block_index,
                           uint64_t block_bits) {
    if (!sha_trace_wants_block(tr, block_index)) {
        return;
    }
    sha_trace_step_t s;
    memset(&s, 0, sizeof(s));
    s.stage   = SHA_TRACE_BLOCK_SPLIT;
    s.block   = block_index;
    s.round   = 0xFFFFFFFFu;
    s.w_index = 0xFFFFFFFFu;
    s.msg_bits = block_bits;
    sha_trace_push(tr, &s);
}

void sha_trace_clear(sha_trace_t *tr) {
    if (tr == NULL) {
        return;
    }
    tr->count = 0;
    tr->overflowed = 0;
    /* capacity and the block range are deliberately kept: re-running the
     * same sample should not re-allocate. */
}

/* ====================================================================== */
/* Public trace exports                                                    */
/* ====================================================================== */

/*
 * Call this immediately after sha_create() / sha_reset() and before any
 * sha_update(). Tracing mid-stream would silently miss earlier blocks.
 *
 * Tracing is opt-in: normal hashing and benchmarking never call this, so
 * the benchmark path pays only a predicted-away NULL branch.
 */
sha_status_t sha_trace_configure(sha_ctx_t *ctx, int enable,
                                 uint32_t first_block, uint32_t block_count) {
    if (ctx == NULL) {
        return SHA_ERR_INVALID_ARG;
    }
    if (ctx->finalized) {
        return SHA_ERR_STATE;    /* too late, the run is already over */
    }

    if (ctx->trace != NULL) {
        sha_trace_free(ctx->trace);
        ctx->trace = NULL;
    }
    if (!enable) {
        return SHA_OK;
    }

    sha_trace_t *tr = sha_trace_new(first_block, block_count);
    if (tr == NULL) {
        return SHA_ERR_NOMEM;
    }
    ctx->trace = tr;
    return SHA_OK;
}

size_t sha_trace_count(const sha_ctx_t *ctx) {
    if (ctx == NULL) {
        return 0;
    }
    return sha_trace_stored(ctx->trace);
}

const sha_trace_step_t *sha_trace_data(const sha_ctx_t *ctx) {
    if (ctx == NULL) {
        return NULL;
    }
    return sha_trace_stored_data(ctx->trace);
}

sha_status_t sha_trace_get(const sha_ctx_t *ctx, size_t i,
                           sha_trace_step_t *out) {
    if (ctx == NULL || out == NULL) {
        return SHA_ERR_INVALID_ARG;
    }
    const sha_trace_step_t *data = sha_trace_stored_data(ctx->trace);
    if (data == NULL || i >= sha_trace_stored(ctx->trace)) {
        return SHA_ERR_INVALID_ARG;
    }
    *out = data[i];
    return SHA_OK;
}

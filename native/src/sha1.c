/*
 * sha1.c — SHA-1, adapted from E:\Downloads\SHA1_256\sha1.c.
 *
 * What changed and why:
 *   - sha1_message_schedule / sha1_f / the 80-round loop keep the original
 *     arithmetic and the original structure; nothing about the algorithm was
 *     rewritten.
 *   - The old one-shot wrapper (which calloc'd a padded copy of the whole
 *     message, i.e. O(n) memory and no streaming) is gone. Padding now lives
 *     in sha_ctx.c:sha_final(), so inputs larger than memory work.
 *   - A nullable sha_trace_t *sink was threaded through. When it is NULL the
 *     only cost is one perfectly predicted branch, so benchmarking and normal
 *     hashing are unaffected.
 */
#include "sha_internal.h"

#include <string.h>

/* ---------------------------------------------------------------------- */
/* Message schedule                                                        */
/* ---------------------------------------------------------------------- */

/*
 * W[0..15]  are the block's raw big-endian words        -> trace stage 1
 * W[16..79] are ROTL1(W[t-3] ^ W[t-8] ^ W[t-14] ^ W[t-16]) -> trace stage 4
 */
static void sha1_message_schedule(const uint8_t block[64], uint32_t W[80],
                                  sha_trace_t *tr, uint32_t block_index) {
    const int want = sha_trace_wants_block(tr, block_index);

    for (int t = 0; t < 16; t++) {
        uint32_t word = sha_load_be32(&block[t * 4]);
        W[t] = word;
        if (want) {
            sha_trace_step_t s;
            memset(&s, 0, sizeof(s));
            s.stage      = SHA_TRACE_INPUT_WORDS;
            s.block      = block_index;
            s.round      = 0xFFFFFFFFu;
            s.w_index    = (uint32_t)t;
            s.words[0]   = word;
            s.aux[0]     = block[t * 4];       /* the four raw bytes, so the */
            s.aux[1]     = block[t * 4 + 1];   /* UI can show byte -> word    */
            s.aux[2]     = block[t * 4 + 2];
            s.aux[3]     = block[t * 4 + 3];
            sha_trace_push(tr, &s);
        }
    }

    for (int t = 16; t < 80; t++) {
        uint32_t a = W[t - 3];
        uint32_t b = W[t - 8];
        uint32_t c = W[t - 14];
        uint32_t d = W[t - 16];
        uint32_t word = ROTL32(a ^ b ^ c ^ d, 1);
        W[t] = word;
        if (want) {
            sha_trace_step_t s;
            memset(&s, 0, sizeof(s));
            s.stage      = SHA_TRACE_SCHEDULE;
            s.block      = block_index;
            s.round      = 0xFFFFFFFFu;
            s.w_index    = (uint32_t)t;
            s.words[0]   = word;
            s.aux[0]     = a;
            s.aux[1]     = b;
            s.aux[2]     = c;
            s.aux[3]     = d;
            sha_trace_push(tr, &s);
        }
    }
}

/* ---------------------------------------------------------------------- */
/* Boolean function — unchanged                                            */
/* ---------------------------------------------------------------------- */

static uint32_t sha1_f(int t, uint32_t b, uint32_t c, uint32_t d) {
    if (t < 20) {
        return (b & c) | (~b & d);        /* Ch      */
    } else if (t < 40) {
        return b ^ c ^ d;                 /* Parity  */
    } else if (t < 60) {
        return (b & c) | (b & d) | (c & d); /* Maj   */
    } else {
        return b ^ c ^ d;                 /* Parity  */
    }
}

/* Round constants — unchanged, one per 20-round phase. */
static uint32_t sha1_k(int t) {
    if (t < 20) {
        return 0x5A827999u;
    } else if (t < 40) {
        return 0x6ED9EBA1u;
    } else if (t < 60) {
        return 0x8F1BBCDCu;
    } else {
        return 0xCA62C1D6u;
    }
}

/* The four 20-round phases are derived on the managed side as round / 20;
 * no native helper is needed, which keeps the export surface small. */

/* ---------------------------------------------------------------------- */
/* Compression function                                                    */
/* ---------------------------------------------------------------------- */

void sha1_compress(uint32_t state[5], const uint8_t block[64],
                   sha_trace_t *tr, uint32_t block_index) {

    sha_trace_block_split(tr, block_index, 512u);

    uint32_t W[80];
    sha1_message_schedule(block, W, tr, block_index);

    uint32_t A = state[0];
    uint32_t B = state[1];
    uint32_t C = state[2];
    uint32_t D = state[3];
    uint32_t E = state[4];

    const int want = sha_trace_wants_block(tr, block_index);

    for (int t = 0; t < 80; t++) {

        uint32_t K   = sha1_k(t);
        uint32_t f   = sha1_f(t, B, C, D);
        uint32_t temp = ROTL32(A, 5) + f + E + K + W[t];

        if (want) {
            sha_trace_step_t s;
            memset(&s, 0, sizeof(s));
            s.stage        = SHA_TRACE_COMPRESSION;
            s.block        = block_index;
            s.round        = (uint32_t)t;
            s.w_index      = (uint32_t)t;
            s.words_before[0] = A;
            s.words_before[1] = B;
            s.words_before[2] = C;
            s.words_before[3] = D;
            s.words_before[4] = E;
            s.aux[0] = temp;   /* temp = ROTL5(A) + f + E + K + W[t] */
            s.aux[1] = f;
            s.aux[2] = K;
            s.aux[3] = W[t];
            sha_trace_push(tr, &s);
        }

        /* The register shift — original code, unchanged. */
        uint32_t before[5];
        if (want) {
            before[0] = A; before[1] = B; before[2] = C;
            before[3] = D; before[4] = E;
        }

        E = D;
        D = C;
        C = ROTL32(B, 30);
        B = A;
        A = temp;

        if (want) {
            sha_trace_step_t s;
            memset(&s, 0, sizeof(s));
            s.stage   = SHA_TRACE_STATE_UPDATE;
            s.block   = block_index;
            s.round   = (uint32_t)t;
            s.w_index = (uint32_t)t;
            memcpy(s.words_before, before, sizeof(before));
            s.words[0] = A;
            s.words[1] = B;
            s.words[2] = C;
            s.words[3] = D;
            s.words[4] = E;
            sha_trace_push(tr, &s);
        }
    }

    state[0] += A;
    state[1] += B;
    state[2] += C;
    state[3] += D;
    state[4] += E;
}

/* Initial chaining value — the classic SHA-1 IV, unchanged. */
void sha1_init_state(uint32_t state[5]) {
    state[0] = 0x67452301u;
    state[1] = 0xEFCDAB89u;
    state[2] = 0x98BADCFEu;
    state[3] = 0x10325476u;
    state[4] = 0xC3D2E1F0u;
}

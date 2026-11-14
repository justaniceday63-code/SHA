/*
 * sha256.c — SHA-256, adapted from E:\Downloads\SHA1_256\sha256.c.
 *
 * Independent of sha1.c on purpose: SHA-256 has its own schedule, its own
 * logical functions (CH, MAJ, SIGMA0/1, sigma0/1) and its own 64-round
 * loop, and the educational UI must show them separately (spec §7.3). The
 * only shared code is the byte-order helper in sha_internal.h.
 *
 * The one-shot wrapper that malloc'd a padded copy of the whole message is
 * gone; padding lives in sha_ctx.c:sha_final().
 */
#include "sha_internal.h"

#include <string.h>

#define CH(x, y, z)  (((x) & (y)) ^ (~(x) & (z)))
#define MAJ(x, y, z) (((x) & (y)) ^ ((x) & (z)) ^ ((y) & (z)))

#define SIGMA0(x) (ROTR32(x, 2) ^ ROTR32(x, 13) ^ ROTR32(x, 22))
#define SIGMA1(x) (ROTR32(x, 6) ^ ROTR32(x, 11) ^ ROTR32(x, 25))

#define sigma0(x) (ROTR32(x, 7) ^ ROTR32(x, 18) ^ SHR32(x, 3))
#define sigma1(x) (ROTR32(x, 17) ^ ROTR32(x, 19) ^ SHR32(x, 10))

/* ---------------------------------------------------------------------- */
/* Constants — unchanged                                                   */
/* ---------------------------------------------------------------------- */

static const uint32_t SHA256_INITIAL_HASH[8] = {
    0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a,
    0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19
};

static const uint32_t SHA256_K[64] = {
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5,
    0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
    0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3,
    0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
    0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc,
    0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
    0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7,
    0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
    0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13,
    0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
    0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3,
    0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
    0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5,
    0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
    0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208,
    0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
};

/* ---------------------------------------------------------------------- */
/* Message schedule                                                        */
/* ---------------------------------------------------------------------- */

static void sha256_message_schedule(const uint8_t block[64], uint32_t W[64],
                                    sha_trace_t *tr, uint32_t block_index) {
    const int want = sha_trace_wants_block(tr, block_index);

    for (int i = 0; i < 16; i++) {
        uint32_t word = sha_load_be32(&block[i * 4]);
        W[i] = word;
        if (want) {
            sha_trace_step_t s;
            memset(&s, 0, sizeof(s));
            s.stage    = SHA_TRACE_INPUT_WORDS;
            s.block    = block_index;
            s.round    = 0xFFFFFFFFu;
            s.w_index  = (uint32_t)i;
            s.words[0] = word;
            s.aux[0]   = block[i * 4];
            s.aux[1]   = block[i * 4 + 1];
            s.aux[2]   = block[i * 4 + 2];
            s.aux[3]   = block[i * 4 + 3];
            sha_trace_push(tr, &s);
        }
    }

    for (int i = 16; i < 64; i++) {
        uint32_t x = W[i - 2];
        uint32_t y = W[i - 7];
        uint32_t z = W[i - 15];
        uint32_t w = W[i - 16];
        uint32_t s1 = sigma1(x);
        uint32_t s0 = sigma0(z);
        uint32_t word = s1 + y + s0 + w;
        W[i] = word;
        if (want) {
            sha_trace_step_t s;
            memset(&s, 0, sizeof(s));
            s.stage    = SHA_TRACE_SCHEDULE;
            s.block    = block_index;
            s.round    = 0xFFFFFFFFu;
            s.w_index  = (uint32_t)i;
            s.words[0] = word;
            s.aux[0]   = x;            /* W[i-2]   */
            s.aux[1]   = y;            /* W[i-7]   */
            s.aux[2]   = z;            /* W[i-15]  */
            s.aux[3]   = w;            /* W[i-16]  */
            s.aux[4]   = s1;           /* sigma1   */
            s.aux[5]   = s0;           /* sigma0   */
            sha_trace_push(tr, &s);
        }
    }
}

/* ---------------------------------------------------------------------- */
/* Compression function                                                    */
/* ---------------------------------------------------------------------- */

void sha256_compress(uint32_t H[8], const uint8_t block[64],
                     sha_trace_t *tr, uint32_t block_index) {

    sha_trace_block_split(tr, block_index, 512u);

    uint32_t W[64];
    sha256_message_schedule(block, W, tr, block_index);

    uint32_t a = H[0], b = H[1], c = H[2], d = H[3];
    uint32_t e = H[4], f = H[5], g = H[6], h = H[7];

    const int want = sha_trace_wants_block(tr, block_index);

    for (int i = 0; i < 64; i++) {

        uint32_t sigma1_e = SIGMA1(e);
        uint32_t ch       = CH(e, f, g);
        uint32_t T1       = h + sigma1_e + ch + SHA256_K[i] + W[i];
        uint32_t sigma0_a = SIGMA0(a);
        uint32_t maj      = MAJ(a, b, c);
        uint32_t T2       = sigma0_a + maj;

        if (want) {
            sha_trace_step_t s;
            memset(&s, 0, sizeof(s));
            s.stage        = SHA_TRACE_COMPRESSION;
            s.block        = block_index;
            s.round        = (uint32_t)i;
            s.w_index      = (uint32_t)i;
            s.words_before[0] = a;
            s.words_before[1] = b;
            s.words_before[2] = c;
            s.words_before[3] = d;
            s.words_before[4] = e;
            s.words_before[5] = f;
            s.words_before[6] = g;
            s.words_before[7] = h;
            s.aux[0] = T1;
            s.aux[1] = T2;
            s.aux[2] = sigma0_a;
            s.aux[3] = ch;
            s.aux[4] = sigma1_e;
            s.aux[5] = maj;
            s.aux[6] = SHA256_K[i];
            s.aux[7] = W[i];
            sha_trace_push(tr, &s);
        }

        uint32_t before[8];
        if (want) {
            before[0] = a; before[1] = b; before[2] = c; before[3] = d;
            before[4] = e; before[5] = f; before[6] = g; before[7] = h;
        }

        /* The register shift — original code, unchanged. */
        h = g;
        g = f;
        f = e;
        e = d + T1;
        d = c;
        c = b;
        b = a;
        a = T1 + T2;

        if (want) {
            sha_trace_step_t s;
            memset(&s, 0, sizeof(s));
            s.stage   = SHA_TRACE_STATE_UPDATE;
            s.block   = block_index;
            s.round   = (uint32_t)i;
            s.w_index = (uint32_t)i;
            memcpy(s.words_before, before, sizeof(before));
            s.words[0] = a;
            s.words[1] = b;
            s.words[2] = c;
            s.words[3] = d;
            s.words[4] = e;
            s.words[5] = f;
            s.words[6] = g;
            s.words[7] = h;
            sha_trace_push(tr, &s);
        }
    }

    H[0] += a; H[1] += b; H[2] += c; H[3] += d;
    H[4] += e; H[5] += f; H[6] += g; H[7] += h;
}

void sha256_init_state(uint32_t state[8]) {
    for (int i = 0; i < 8; i++) {
        state[i] = SHA256_INITIAL_HASH[i];
    }
}

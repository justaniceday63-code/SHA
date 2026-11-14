/*
 * test_trace.c — the educational trace.
 *
 * Three properties matter more than anything else here:
 *
 *   1. TRACING MUST NOT CHANGE THE DIGEST. A traced run has to produce the
 *      same result as an untraced run, or the animation would be showing
 *      something the app never actually computes.
 *
 *   2. THE TRACE MUST BE SUFFICIENT TO RECONSTRUCT THE DIGEST. The UI
 *      assembles its final digest from traced register values (spec §7,
 *      stage 6 -> 7). If reconstruction fails, the page would display
 *      numbers that do not correspond to the real computation.
 *
 *   3. ROUND COUNTS MUST BE RIGHT: 80 rounds for SHA-1, 64 for SHA-256,
 *      in their own independent traces (spec §7.3 forbids sharing a
 *      compression visualization between the two).
 *
 * Exit code 0 = all passed, 1 = at least one failure.
 */
#include <stdio.h>
#include <stdint.h>
#include <string.h>

#include "shagui_native.h"

static int g_failed = 0;
static int g_passed = 0;

static void ok(int cond, const char *what) {
    if (cond) {
        printf("    PASS  %s\n", what);
        g_passed++;
    } else {
        printf("    FAIL  %s\n", what);
        g_failed++;
    }
}

static const uint32_t SHA1_IV[5] = {
    0x67452301u, 0xEFCDAB89u, 0x98BADCFEu, 0x10325476u, 0xC3D2E1F0u
};

static const uint32_t SHA256_IV[8] = {
    0x6a09e667u, 0xbb67ae85u, 0x3c6ef372u, 0xa54ff53au,
    0x510e527fu, 0x9b05688cu, 0x1f83d9abu, 0x5be0cd19u
};

static int count_stage(const sha_trace_step_t *steps, size_t n,
                       uint32_t stage) {
    int c = 0;
    for (size_t i = 0; i < n; i++) {
        if (steps[i].stage == stage) {
            c++;
        }
    }
    return c;
}

/* ---- 1. traced vs untraced digest ------------------------------------- */

static void test_digest_equality(const char *label, sha_algo_t algo,
                                 const uint8_t *data, size_t len) {
    char l[192];

    /* untraced */
    uint8_t plain[SHA256_DIGEST_LEN];
    size_t  wp = 0;
    sha_ctx_t *c1 = NULL;
    sha_create(algo, &c1);
    sha_update(c1, data, (uint64_t)len);
    sha_final(c1, plain, sizeof(plain), &wp);
    sha_destroy(c1);

    /* traced, all blocks */
    sha_ctx_t *c2 = NULL;
    sha_create(algo, &c2);
    sha_trace_configure(c2, 1, 0, 0);
    sha_update(c2, data, (uint64_t)len);
    uint8_t traced[SHA256_DIGEST_LEN];
    size_t  wt = 0;
    sha_final(c2, traced, sizeof(traced), &wt);
    const size_t nsteps = sha_trace_count(c2);

    snprintf(l, sizeof(l), "%s: traced digest == untraced digest", label);
    ok(wp == wt && memcmp(plain, traced, wp) == 0, l);

    snprintf(l, sizeof(l), "%s: trace recorded %zu steps", label, nsteps);
    ok(nsteps > 0, l);

    sha_destroy(c2);
}

/* ---- 2. reconstruct the digest from traced registers ------------------- */

static void test_reconstruction(const char *label, sha_algo_t algo,
                                const uint8_t *data, size_t len) {
    char l[192];

    sha_ctx_t *ctx = NULL;
    sha_create(algo, &ctx);
    sha_trace_configure(ctx, 1, 0, 0);
    sha_update(ctx, data, (uint64_t)len);

    uint8_t digest[SHA256_DIGEST_LEN];
    size_t  written = 0;
    sha_final(ctx, digest, sizeof(digest), &written);

    const sha_trace_step_t *steps = sha_trace_data(ctx);
    const size_t n = sha_trace_count(ctx);
    const int nwords = (algo == SHA_ALGO_SHA1) ? 5 : 8;
    const uint32_t *iv = (algo == SHA_ALGO_SHA1) ? SHA1_IV : SHA256_IV;

    /*
     * Replay the chaining exactly as sha1_compress/sha256_compress do:
     * for each block take the working registers from its LAST round and
     * add them into the chaining value.
     */
    uint32_t acc[8];
    memcpy(acc, iv, sizeof(uint32_t) * (size_t)nwords);

    uint32_t pending[8];
    uint32_t cur_block = 0;
    int have_pending = 0;

    for (size_t i = 0; i < n; i++) {
        if (steps[i].stage != SHA_TRACE_STATE_UPDATE) {
            continue;
        }
        if (!have_pending) {
            cur_block = steps[i].block;
            have_pending = 1;
        } else if (steps[i].block != cur_block) {
            for (int w = 0; w < nwords; w++) {
                acc[w] += pending[w];
            }
            cur_block = steps[i].block;
        }
        memcpy(pending, steps[i].words,
               sizeof(uint32_t) * (size_t)nwords);
    }
    if (have_pending) {
        for (int w = 0; w < nwords; w++) {
            acc[w] += pending[w];
        }
    }

    uint8_t rebuilt[SHA256_DIGEST_LEN];
    for (int w = 0; w < nwords; w++) {
        rebuilt[w * 4]     = (uint8_t)(acc[w] >> 24);
        rebuilt[w * 4 + 1] = (uint8_t)(acc[w] >> 16);
        rebuilt[w * 4 + 2] = (uint8_t)(acc[w] >> 8);
        rebuilt[w * 4 + 3] = (uint8_t)(acc[w]);
    }

    snprintf(l, sizeof(l), "%s: digest reconstructable from trace", label);
    ok(written == sha_digest_length(algo) &&
       memcmp(rebuilt, digest, written) == 0, l);

    sha_destroy(ctx);
}

/* ---- 3. stage coverage and round counts -------------------------------- */

static void test_stage_coverage(void) {
    printf("  stage coverage\n");

    static const uint8_t msg[] = "abc";
    const size_t len = 3;

    /* SHA-1: 1 block -> 16 words, 64 schedule steps, 80 rounds */
    {
        sha_ctx_t *ctx = NULL;
        sha_create(SHA_ALGO_SHA1, &ctx);
        sha_trace_configure(ctx, 1, 0, 0);
        sha_update(ctx, msg, len);
        uint8_t d[32];
        size_t w = 0;
        sha_final(ctx, d, sizeof(d), &w);

        const sha_trace_step_t *s = sha_trace_data(ctx);
        const size_t n = sha_trace_count(ctx);

        ok(count_stage(s, n, SHA_TRACE_INPUT_WORDS) == 16,
           "SHA-1 stage 1: 16 words loaded");
        ok(count_stage(s, n, SHA_TRACE_BLOCK_SPLIT) == 1,
           "SHA-1 stage 3: 1 block");
        ok(count_stage(s, n, SHA_TRACE_SCHEDULE) == 64,
           "SHA-1 stage 4: W[16..79] generated");
        ok(count_stage(s, n, SHA_TRACE_COMPRESSION) == 80,
           "SHA-1 stage 5: 80 rounds");
        ok(count_stage(s, n, SHA_TRACE_STATE_UPDATE) == 80,
           "SHA-1 stage 6: 80 register updates");
        ok(count_stage(s, n, SHA_TRACE_PADDING) == 4,
           "SHA-1 stage 2: 4 padding steps");
        ok(count_stage(s, n, SHA_TRACE_DIGEST) == 1,
           "SHA-1 stage 7: 1 digest step");

        /* The padding sequence must be: original, 1-bit, zeros, length. */
        int seq_ok = 1;
        int seen = 0;
        for (size_t i = 0; i < n && seen < 4; i++) {
            if (s[i].stage == SHA_TRACE_PADDING) {
                if (s[i].flags != (uint32_t)seen) {
                    seq_ok = 0;
                }
                seen++;
            }
        }
        ok(seq_ok && seen == 4,
           "SHA-1 padding flags ordered original/1-bit/zeros/length");

        sha_destroy(ctx);
    }

    /* SHA-256: 1 block -> 16 words, 48 schedule steps, 64 rounds */
    {
        sha_ctx_t *ctx = NULL;
        sha_create(SHA_ALGO_SHA256, &ctx);
        sha_trace_configure(ctx, 1, 0, 0);
        sha_update(ctx, msg, len);
        uint8_t d[32];
        size_t w = 0;
        sha_final(ctx, d, sizeof(d), &w);

        const sha_trace_step_t *s = sha_trace_data(ctx);
        const size_t n = sha_trace_count(ctx);

        ok(count_stage(s, n, SHA_TRACE_INPUT_WORDS) == 16,
           "SHA-256 stage 1: 16 words loaded");
        ok(count_stage(s, n, SHA_TRACE_SCHEDULE) == 48,
           "SHA-256 stage 4: W[16..63] generated");
        ok(count_stage(s, n, SHA_TRACE_COMPRESSION) == 64,
           "SHA-256 stage 5: 64 rounds");
        ok(count_stage(s, n, SHA_TRACE_STATE_UPDATE) == 64,
           "SHA-256 stage 6: 64 register updates");
        ok(count_stage(s, n, SHA_TRACE_PADDING) == 4,
           "SHA-256 stage 2: 4 padding steps");
        ok(count_stage(s, n, SHA_TRACE_DIGEST) == 1,
           "SHA-256 stage 7: 1 digest step");

        sha_destroy(ctx);
    }
}

/* ---- 4. block-range scoping -------------------------------------------- */

static void test_block_range(void) {
    printf("  block-range scoping\n");

    /* 200 bytes -> 4 padded blocks (200 + 1 + 8 rounded up to 256). */
    static uint8_t msg[200];
    for (size_t i = 0; i < sizeof(msg); i++) {
        msg[i] = (uint8_t)(i * 3 + 1);
    }

    sha_ctx_t *ctx = NULL;
    sha_create(SHA_ALGO_SHA1, &ctx);
    ok(sha_trace_configure(ctx, 1, 2, 1) == SHA_OK,
       "configure block range [2, 3)");
    sha_update(ctx, msg, sizeof(msg));
    uint8_t d[32];
    size_t w = 0;
    sha_final(ctx, d, sizeof(d), &w);

    const sha_trace_step_t *s = sha_trace_data(ctx);
    const size_t n = sha_trace_count(ctx);

    int all_in_range = (n > 0);
    for (size_t i = 0; i < n; i++) {
        if (s[i].block != 2) {
            all_in_range = 0;
        }
    }
    ok(all_in_range, "every recorded step belongs to block 2");
    ok(count_stage(s, n, SHA_TRACE_COMPRESSION) == 80,
       "block 2 recorded its full 80 rounds");
    /* padding and digest belong to the final block (3), so they are absent */
    ok(count_stage(s, n, SHA_TRACE_PADDING) == 0,
       "padding of block 3 not recorded");
    ok(count_stage(s, n, SHA_TRACE_DIGEST) == 0,
       "digest of block 3 not recorded");

    sha_destroy(ctx);

    /* An empty range must record nothing at all. */
    sha_create(SHA_ALGO_SHA1, &ctx);
    sha_trace_configure(ctx, 1, 10, 1);
    sha_update(ctx, msg, sizeof(msg));
    sha_final(ctx, d, sizeof(d), &w);
    ok(sha_trace_count(ctx) == 0, "out-of-range block records nothing");
    sha_destroy(ctx);
}

/* ---- 5. API rules around tracing --------------------------------------- */

static void test_api_rules(void) {
    printf("  trace API rules\n");

    sha_ctx_t *ctx = NULL;
    sha_create(SHA_ALGO_SHA1, &ctx);

    ok(sha_trace_count(ctx) == 0, "tracing is off by default");

    uint8_t d[32];
    size_t w = 0;
    sha_update(ctx, "abc", 3);
    sha_final(ctx, d, sizeof(d), &w);

    ok(sha_trace_configure(ctx, 1, 0, 0) == SHA_ERR_STATE,
       "configure after final rejected");

    sha_trace_step_t step;
    ok(sha_trace_get(ctx, 0, &step) == SHA_ERR_INVALID_ARG,
       "trace_get with no trace rejected");
    ok(sha_trace_data(ctx) == NULL, "trace_data with no trace is NULL");
    sha_destroy(ctx);

    /* Valid index / out-of-range behaviour on a real trace. */
    sha_create(SHA_ALGO_SHA1, &ctx);
    sha_trace_configure(ctx, 1, 0, 0);
    sha_update(ctx, "abc", 3);
    sha_final(ctx, d, sizeof(d), &w);

    const size_t n = sha_trace_count(ctx);
    ok(n > 0 && sha_trace_get(ctx, 0, &step) == SHA_OK,
       "trace_get(0) succeeds");
    ok(sha_trace_get(ctx, n, &step) == SHA_ERR_INVALID_ARG,
       "trace_get(count) rejected");
    ok(sha_trace_get(ctx, 0, NULL) == SHA_ERR_INVALID_ARG,
       "trace_get(NULL out) rejected");

    const sha_trace_step_t *data = sha_trace_data(ctx);
    ok(data != NULL && n > 0 && memcmp(&data[0], &step, sizeof(step)) == 0,
       "trace_data agrees with trace_get");

    /* reset() keeps the configuration but clears the previous steps. */
    sha_reset(ctx);
    ok(sha_trace_count(ctx) == 0, "reset clears recorded steps");
    sha_update(ctx, "abc", 3);
    sha_final(ctx, d, sizeof(d), &w);
    ok(sha_trace_count(ctx) == n, "re-run after reset records the same count");

    sha_destroy(ctx);
    ok(sha_trace_count(NULL) == 0, "trace_count(NULL) is 0");
}

int main(void) {
    printf("===== trace tests =====\n\n");

    static const uint8_t abc[] = "abc";
    static const uint8_t long_msg[200] = { 0 };

    printf("  digest equality\n");
    test_digest_equality("SHA-1 abc", SHA_ALGO_SHA1, abc, 3);
    test_digest_equality("SHA-256 abc", SHA_ALGO_SHA256, abc, 3);
    test_digest_equality("SHA-1 200B", SHA_ALGO_SHA1, long_msg,
                         sizeof(long_msg));
    test_digest_equality("SHA-256 200B", SHA_ALGO_SHA256, long_msg,
                         sizeof(long_msg));

    printf("  digest reconstruction\n");
    test_reconstruction("SHA-1 abc", SHA_ALGO_SHA1, abc, 3);
    test_reconstruction("SHA-256 abc", SHA_ALGO_SHA256, abc, 3);
    test_reconstruction("SHA-1 200B", SHA_ALGO_SHA1, long_msg,
                        sizeof(long_msg));
    test_reconstruction("SHA-256 200B", SHA_ALGO_SHA256, long_msg,
                        sizeof(long_msg));

    test_stage_coverage();
    test_block_range();
    test_api_rules();

    printf("\n===== summary =====\n");
    printf("passed : %d\n", g_passed);
    printf("failed : %d\n", g_failed);

    return (g_failed == 0) ? 0 : 1;
}

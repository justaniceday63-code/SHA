/*
 * test_streaming.c — incremental Update/Final behaviour and API state rules.
 *
 * The original code had no streaming API at all: sha1()/sha256() took the
 * whole message at once and malloc'd a padded copy of it. These tests pin
 * down the behaviour that replaced it.
 *
 * Exit code 0 = all passed, 1 = at least one failure.
 */
#include <stdio.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#include "shagui_native.h"
#include "test_vectors_data.h"

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

static const char HEX[] = "0123456789abcdef";

static void to_hex(const uint8_t *d, size_t n, char *out) {
    for (size_t i = 0; i < n; i++) {
        out[i * 2]     = HEX[d[i] >> 4];
        out[i * 2 + 1] = HEX[d[i] & 0x0F];
    }
    out[n * 2] = '\0';
}

/* Feed `data` in chunks of the given sizes (cycled) and return the hex
 * digest. Returns 0 on success. */
static int stream_hex(sha_algo_t algo, const uint8_t *data, size_t len,
                      const size_t *chunks, size_t nchunks,
                      char *out, size_t out_cap) {
    sha_ctx_t *ctx = NULL;
    if (sha_create(algo, &ctx) != SHA_OK) {
        return -1;
    }

    size_t off = 0;
    size_t ci  = 0;
    while (off < len) {
        size_t want = chunks[ci % nchunks];
        ci++;
        if (want == 0) {
            want = 1;
        }
        if (want > len - off) {
            want = len - off;
        }
        if (sha_update(ctx, data + off, (uint64_t)want) != SHA_OK) {
            sha_destroy(ctx);
            return -1;
        }
        off += want;
    }

    uint8_t digest[SHA256_DIGEST_LEN];
    size_t  written = 0;
    sha_status_t st = sha_final(ctx, digest, sizeof(digest), &written);
    sha_destroy(ctx);
    if (st != SHA_OK) {
        return -1;
    }

    if (written * 2 + 1 > out_cap) {
        return -1;
    }
    to_hex(digest, written, out);
    return 0;
}

/* ---- chunked streaming must equal the one-shot result ----------------- */

static void test_chunked_streaming(void) {
    static const size_t patterns[][4] = {
        { 1, 0, 0, 0 },      /* byte at a time      */
        { 3, 0, 0, 0 },      /* awkward small step  */
        { 55, 0, 0, 0 },     /* padding threshold   */
        { 63, 0, 0, 0 },     /* one short of a block*/
        { 64, 0, 0, 0 },     /* exact blocks        */
        { 65, 0, 0, 0 },     /* one over a block    */
        { 7, 100, 1, 4096 }, /* irregular           */
    };
    const size_t npatterns = sizeof(patterns) / sizeof(patterns[0]);

    printf("  chunked streaming == one-shot\n");

    for (size_t v = 0; v < sizeof(KAT) / sizeof(KAT[0]); v++) {
        const kat_t *k = &KAT[v];
        if (k->len > 64 * 1024) {
            continue;   /* long-message case is covered separately */
        }

        static uint8_t scratch[1024 * 1024];
        const uint8_t *payload = k->data;
        if (payload == NULL) {
            if (k->len == 0) {
                continue;
            }
            memset(scratch, k->fill, k->len);
            payload = scratch;
        }

        for (int a = 0; a < 2; a++) {
            const sha_algo_t algo = (a == 0) ? SHA_ALGO_SHA1 : SHA_ALGO_SHA256;
            const char *expected = (a == 0) ? k->sha1 : k->sha256;

            /* MSVC does not accept C99 compound literals, so the chunk
             * pattern is a named array. One chunk == the whole message. */
            const size_t whole[1] = { (k->len > 0) ? k->len : 1 };
            char oneshot[SHA256_DIGEST_LEN * 2 + 1];
            if (stream_hex(algo, payload, k->len, whole, 1,
                           oneshot, sizeof(oneshot)) != 0) {
                ok(0, "one-shot stream");
                continue;
            }

            for (size_t p = 0; p < npatterns; p++) {
                char chunked[SHA256_DIGEST_LEN * 2 + 1];
                char label[128];
                snprintf(label, sizeof(label), "%s / %s / pattern %zu",
                         k->name, sha_algo_name(algo), p);

                if (stream_hex(algo, payload, k->len, patterns[p], 4,
                               chunked, sizeof(chunked)) != 0) {
                    ok(0, label);
                    continue;
                }
                ok(strcmp(chunked, expected) == 0 &&
                   strcmp(chunked, oneshot) == 0, label);
            }
        }
    }
}

/* ---- long message, streamed ------------------------------------------- */

static void test_long_message(void) {
    printf("  long message (1,000,000 bytes, 64 KiB chunks)\n");

    static uint8_t buf[64 * 1024];
    memset(buf, 'a', sizeof(buf));

    for (int a = 0; a < 2; a++) {
        const sha_algo_t algo = (a == 0) ? SHA_ALGO_SHA1 : SHA_ALGO_SHA256;
        const char *expected = (a == 0)
            ? "34aa973cd4c4daa4f61eeb2bdbad27316534016f"
            : "cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0";

        sha_ctx_t *ctx = NULL;
        sha_create(algo, &ctx);

        size_t remaining = 1000000;
        while (remaining > 0) {
            size_t n = (remaining < sizeof(buf)) ? remaining : sizeof(buf);
            sha_update(ctx, buf, (uint64_t)n);
            remaining -= n;
        }

        uint8_t digest[SHA256_DIGEST_LEN];
        size_t  written = 0;
        sha_final(ctx, digest, sizeof(digest), &written);
        sha_destroy(ctx);

        char actual[SHA256_DIGEST_LEN * 2 + 1];
        to_hex(digest, written, actual);

        char label[160];
        snprintf(label, sizeof(label), "1e6 x 'a' / %s", sha_algo_name(algo));
        ok(strcmp(actual, expected) == 0, label);
    }
}

/* ---- API state machine ------------------------------------------------ */

static void test_state_rules(void) {
    printf("  API state rules\n");

    sha_ctx_t *ctx = NULL;
    uint8_t digest[SHA256_DIGEST_LEN];
    size_t written = 0;

    ok(sha_create(SHA_ALGO_SHA1, &ctx) == SHA_OK && ctx != NULL,
       "create SHA-1");
    ok(sha_update(ctx, "abc", 3) == SHA_OK, "update ok");

    ok(sha_final(ctx, digest, sizeof(digest), &written) == SHA_OK,
       "final ok");
    ok(written == SHA1_DIGEST_LEN, "SHA-1 digest is 20 bytes");

    /* Reusing a finished context must fail rather than silently reset. */
    ok(sha_final(ctx, digest, sizeof(digest), &written) == SHA_ERR_STATE,
       "second final rejected");
    ok(sha_update(ctx, "x", 1) == SHA_ERR_STATE,
       "update after final rejected");

    /* reset() must make it usable again and produce the same digest. */
    ok(sha_reset(ctx) == SHA_OK, "reset ok");
    ok(sha_update(ctx, "abc", 3) == SHA_OK, "update after reset");
    ok(sha_final(ctx, digest, sizeof(digest), &written) == SHA_OK,
       "final after reset");
    {
        char actual[SHA256_DIGEST_LEN * 2 + 1];
        to_hex(digest, written, actual);
        ok(strcmp(actual, "a9993e364706816aba3e25717850c26c9cd0d89d") == 0,
           "digest after reset matches");
    }
    sha_destroy(ctx);
}

static void test_argument_rules(void) {
    printf("  argument validation\n");

    uint8_t digest[SHA256_DIGEST_LEN];
    size_t  written = 0;

    ok(sha_create(SHA_ALGO_SHA1, NULL) == SHA_ERR_INVALID_ARG,
       "create with NULL out");
    ok(sha_create((sha_algo_t)99, NULL) == SHA_ERR_INVALID_ARG ||
           sha_create((sha_algo_t)99, NULL) == SHA_ERR_UNSUPPORTED,
       "unknown algorithm rejected");

    ok(sha_hash_buffer(SHA_ALGO_SHA1, NULL, 0, digest, sizeof(digest),
                       &written) == SHA_OK,
       "(NULL, 0) is a valid empty message");
    ok(written == 20, "empty message digest is 20 bytes");

    /* Buffer smaller than the digest must be refused, not overrun. */
    ok(sha_hash_buffer(SHA_ALGO_SHA256, "abc", 3, digest, 16, &written) ==
       SHA_ERR_INVALID_ARG,
       "undersized output buffer rejected");

    /* Length that would overflow the 64-bit bit-counter. */
    {
        sha_ctx_t *ctx = NULL;
        sha_create(SHA_ALGO_SHA1, &ctx);
        static uint8_t one_byte;
        ok(sha_update(ctx, &one_byte, ((uint64_t)1) << 62) ==
           SHA_ERR_OVERFLOW,
           "message longer than 2^61 bytes rejected");
        sha_destroy(ctx);
    }

    ok(sha_digest_length((sha_algo_t)99) == 0, "digest length of bad algo");
    ok(strcmp(sha_algo_name(SHA_ALGO_SHA256), "SHA-256") == 0,
       "algo name");
    ok(sha_abi_version() == SHAGUI_ABI_VERSION, "ABI version matches");
    ok(sha_status_string(SHA_ERR_CANCELLED) != NULL, "status string");
}

int main(void) {
    printf("===== streaming / API tests =====\n\n");

    test_chunked_streaming();
    test_long_message();
    test_state_rules();
    test_argument_rules();

    printf("\n===== summary =====\n");
    printf("passed : %d\n", g_passed);
    printf("failed : %d\n", g_failed);

    return (g_failed == 0) ? 0 : 1;
}

/*
 * test_vectors.c — known-answer tests for SHA-1 and SHA-256.
 *
 * Expectations are generated from Python hashlib (scripts/gen_kat.py), so a
 * failure here means the C code disagrees with an independent implementation
 * — not that we compared the code against itself.
 *
 * Covers the spec's required cases: empty input, "abc", the RFC 3174 string,
 * lengths straddling the 64-byte block and the 56-byte padding threshold,
 * binary data containing NUL bytes, Vietnamese UTF-8, and the FIPS one-million
 * 'a' long message.
 *
 * Exit code 0 = all passed, 1 = at least one failure. (The original
 * E:\Downloads\SHA1_256\test_sha1.c always returned 0, so it could never
 * gate a build.)
 */
#include <stdio.h>
#include <stdint.h>
#include <string.h>

#include "shagui_native.h"
#include "test_vectors_data.h"

static const char HEX[] = "0123456789abcdef";

static void to_hex(const uint8_t *digest, size_t len, char *out) {
    for (size_t i = 0; i < len; i++) {
        out[i * 2]     = HEX[digest[i] >> 4];
        out[i * 2 + 1] = HEX[digest[i] & 0x0F];
    }
    out[len * 2] = '\0';
}

/* Materialise a vector's payload. The long-message case is a repeated fill,
 * kept out of the generated header so it stays small. */
static const uint8_t *payload_for(const kat_t *k, uint8_t *scratch,
                                  size_t scratch_len) {    if (k->data != NULL) {
        return k->data;
    }
    if (k->len == 0) {
        return NULL;                    /* empty input: (NULL, 0) is valid */
    }
    if (k->len > scratch_len) {
        return NULL;
    }
    memset(scratch, k->fill, k->len);
    return scratch;
}

static int run_algo(const kat_t *k, sha_algo_t algo) {
    /* 1 MiB for the repeated-fill vector: static, so it does not eat the
     * default 1 MiB stack (which crashed MSVC with an ICE). */
    static uint8_t scratch[1024 * 1024];
    uint8_t digest[SHA256_DIGEST_LEN];
    size_t  written = 0;
    char    actual[SHA256_DIGEST_LEN * 2 + 1];
    const char *expected = (algo == SHA_ALGO_SHA1) ? k->sha1 : k->sha256;
    const size_t need = sha_digest_length(algo);

    const uint8_t *payload = payload_for(k, scratch, sizeof(scratch));
    if (payload == NULL && k->len > 0) {
        printf("    %-14s : SKIP (payload too large)\n", k->name);
        return 0;
    }

    sha_status_t st = sha_hash_buffer(algo, payload, (uint64_t)k->len,
                                      digest, sizeof(digest), &written);
    if (st != SHA_OK) {
        printf("    %-14s : ERROR %s\n", k->name, sha_status_string(st));
        return 1;
    }
    if (written != need) {
        printf("    %-14s : FAIL (wrote %zu, expected %zu)\n",
               k->name, written, need);
        return 1;
    }

    to_hex(digest, need, actual);
    if (strcmp(actual, expected) != 0) {
        printf("    %-14s : FAIL\n", k->name);
        printf("        expected %s\n", expected);
        printf("        actual   %s\n", actual);
        return 1;
    }

    printf("    %-14s : PASS\n", k->name);
    return 0;
}

int main(void) {
    const size_t total = sizeof(KAT) / sizeof(KAT[0]);
    int failed = 0;
    int passed = 0;

    printf("===== known-answer tests: %zu cases x 2 algorithms =====\n\n",
           total);

    for (size_t i = 0; i < total; i++) {
        printf("  [%s]\n", KAT[i].name);

        if (run_algo(&KAT[i], SHA_ALGO_SHA1) == 0) {
            passed++;
        } else {
            failed++;
        }

        if (run_algo(&KAT[i], SHA_ALGO_SHA256) == 0) {
            passed++;
        } else {
            failed++;
        }
    }

    printf("\n===== summary =====\n");
    printf("passed : %d\n", passed);
    printf("failed : %d\n", failed);

    return (failed == 0) ? 0 : 1;
}

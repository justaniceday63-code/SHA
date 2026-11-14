/*
 * test_file.c — sha_hash_file() and cancellation.
 *
 * The managed layer owns folder recursion and manifests, but the actual
 * read+hash loop lives here, so this is where we prove that:
 *   - a file on disk hashes to the same digest as the in-memory API
 *     (the whole point of the two layers agreeing),
 *   - non-ASCII paths work (UTF-8 -> wide char conversion),
 *   - cancellation actually stops the loop and reports SHA_ERR_CANCELLED,
 *   - a missing file is reported as an error, never as a digest,
 *   - a >4 GB file hashes correctly with a small buffer (run with
 *     SHAGUI_TEST_BIG=1; skipped by default because it takes minutes).
 *
 * Exit code 0 = all passed, 1 = at least one failure.
 */
#include <stdio.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#include "shagui_native.h"

#if defined(_WIN32) || defined(_WIN64)
#  define WIN32_LEAN_AND_MEAN
#  include <windows.h>
#  include <direct.h>
#else
#  include <sys/stat.h>
#  include <unistd.h>
#endif

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

/* ---- helpers ---------------------------------------------------------- */

static char g_dir[512];

static int write_file(const char *utf8_path, const void *data, size_t len) {
    FILE *f = NULL;
#if defined(_WIN32) || defined(_WIN64)
    /* Same UTF-8 -> wide conversion sha_hash_file() uses, so the test does
     * not accidentally create an ANSI-mangled filename. */
    int wlen = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS,
                                   utf8_path, -1, NULL, 0);
    if (wlen <= 0) {
        return -1;
    }
    wchar_t wpath[1024];
    if (wlen > (int)(sizeof(wpath) / sizeof(wpath[0]))) {
        return -1;
    }
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS,
                        utf8_path, -1, wpath, wlen);
    if (_wfopen_s(&f, wpath, L"wb") != 0) {
        f = NULL;
    }
#else
    f = fopen(utf8_path, "wb");
#endif
    if (f == NULL) {
        return -1;
    }
    if (len > 0 && fwrite(data, 1, len, f) != len) {
        fclose(f);
        return -1;
    }
    fclose(f);
    return 0;
}

/* Reference digest computed through the in-memory streaming API. */
static int ref_hex(sha_algo_t algo, const uint8_t *data, size_t len,
                   char *out, size_t out_cap) {
    sha_ctx_t *ctx = NULL;
    if (sha_create(algo, &ctx) != SHA_OK) {
        return -1;
    }
    sha_update(ctx, data, (uint64_t)len);
    uint8_t d[SHA256_DIGEST_LEN];
    size_t w = 0;
    sha_status_t st = sha_final(ctx, d, sizeof(d), &w);
    sha_destroy(ctx);
    if (st != SHA_OK || w * 2 + 1 > out_cap) {
        return -1;
    }
    to_hex(d, w, out);
    return 0;
}

/* ---- 1. file digest == memory digest ---------------------------------- */

static void test_file_matches_memory(void) {
    printf("  file vs memory\n");

    static const uint8_t payload[] =
        "SHA visualization — file layer test.\n"
        "Xin chào Việt Nam: đường dẫn không dấu cũng phải hoạt động.";

    char path[600];
    snprintf(path, sizeof(path), "%s/plain.bin", g_dir);
    ok(write_file(path, payload, sizeof(payload) - 1) == 0,
       "write test file");

    for (int a = 0; a < 2; a++) {
        const sha_algo_t algo = (a == 0) ? SHA_ALGO_SHA1 : SHA_ALGO_SHA256;
        char expect[SHA256_DIGEST_LEN * 2 + 1];
        char actual[SHA256_DIGEST_LEN * 2 + 1];
        uint8_t digest[SHA256_DIGEST_LEN];
        uint64_t elapsed = 0;

        ref_hex(algo, payload, sizeof(payload) - 1,
                expect, sizeof(expect));

        sha_status_t st = sha_hash_file(path, algo, 0, NULL, NULL, NULL,
                                        digest, sizeof(digest), &elapsed);
        char label[96];
        snprintf(label, sizeof(label), "%s: hash_file status OK",
                 sha_algo_name(algo));
        ok(st == SHA_OK, label);

        to_hex(digest, sha_digest_length(algo), actual);
        snprintf(label, sizeof(label), "%s: file digest == memory digest",
                 sha_algo_name(algo));
        ok(strcmp(actual, expect) == 0, label);

        snprintf(label, sizeof(label),
                 "%s: elapsed reported (>0: %llu)", sha_algo_name(algo),
                 (unsigned long long)elapsed);
        ok(elapsed > 0, label);
    }

    /* An empty file must hash to the empty-string digest. */
    snprintf(path, sizeof(path), "%s/empty.bin", g_dir);
    write_file(path, "", 0);
    {
        uint8_t digest[SHA256_DIGEST_LEN];
        char actual[SHA256_DIGEST_LEN * 2 + 1];
        sha_status_t st = sha_hash_file(path, SHA_ALGO_SHA1, 0,
                                        NULL, NULL, NULL,
                                        digest, sizeof(digest), NULL);
        to_hex(digest, SHA1_DIGEST_LEN, actual);
        ok(st == SHA_OK &&
           strcmp(actual, "da39a3ee5e6b4b0d3255bfef95601890afd80709") == 0,
           "empty file == SHA-1 of empty message");
    }
}

/* ---- 2. non-ASCII path ------------------------------------------------ */

static void test_unicode_path(void) {
    printf("  UTF-8 path\n");

    /* No Vietnamese diacritics in the *filename* itself: this test proves
     * the UTF-8 -> wide conversion, while test_file_matches_memory proves
     * UTF-8 content. Mixed-case + spaces + unicode covers the realistic
     * failure mode (fopen on ANSI code page). */
    static const uint8_t payload[] = "unicode path payload";
    char path[600];
    snprintf(path, sizeof(path), "%s/thu vien - kiểm tra.bin", g_dir);

    if (write_file(path, payload, sizeof(payload) - 1) != 0) {
        printf("    FAIL  could not create unicode-named file\n");
        g_failed++;
        return;
    }

    uint8_t digest[SHA256_DIGEST_LEN];
    char actual[SHA256_DIGEST_LEN * 2 + 1];
    char expect[SHA256_DIGEST_LEN * 2 + 1];
    ref_hex(SHA_ALGO_SHA256, payload, sizeof(payload) - 1,
            expect, sizeof(expect));

    sha_status_t st = sha_hash_file(path, SHA_ALGO_SHA256, 0,
                                    NULL, NULL, NULL,
                                    digest, sizeof(digest), NULL);
    to_hex(digest, SHA256_DIGEST_LEN, actual);
    ok(st == SHA_OK && strcmp(actual, expect) == 0,
       "unicode filename hashes correctly");
}

/* ---- 3. error paths --------------------------------------------------- */

static void test_error_paths(void) {
    printf("  error paths\n");

    uint8_t digest[SHA256_DIGEST_LEN];

    ok(sha_hash_file(NULL, SHA_ALGO_SHA1, 0, NULL, NULL, NULL,
                     digest, sizeof(digest), NULL) == SHA_ERR_INVALID_ARG,
       "NULL path rejected");

    char path[600];
    snprintf(path, sizeof(path), "%s/no-such-file.bin", g_dir);
    ok(sha_hash_file(path, SHA_ALGO_SHA1, 0, NULL, NULL, NULL,
                     digest, sizeof(digest), NULL) == SHA_ERR_IO,
       "missing file -> SHA_ERR_IO");

    snprintf(path, sizeof(path), "%s/plain.bin", g_dir);
    ok(sha_hash_file(path, SHA_ALGO_SHA1, 0, NULL, NULL, NULL,
                     digest, 8, NULL) == SHA_ERR_INVALID_ARG,
       "undersized digest buffer rejected");

    ok(sha_hash_file(path, (sha_algo_t)99, 0, NULL, NULL, NULL,
                     digest, sizeof(digest), NULL) == SHA_ERR_UNSUPPORTED,
       "unknown algorithm rejected");

    ok(sha_hash_file(path, SHA_ALGO_SHA1, ((uint64_t)1) << 41,
                     NULL, NULL, NULL, digest, sizeof(digest), NULL) ==
       SHA_ERR_INVALID_ARG,
       "absurd buffer size rejected before malloc");
}

/* ---- 4. cancellation --------------------------------------------------- */

struct cb_state {
    int  calls;
    int  cancel_after;   /* request cancellation after this many calls */
    int  saw_progress;
};

static int progress_cancel(const sha_progress_t *p, void *user) {
    struct cb_state *s = (struct cb_state *)user;
    s->calls++;
    if (p->file_bytes_total > 0 || p->file_bytes_done > 0) {
        s->saw_progress = 1;
    }
    if (s->cancel_after > 0 && s->calls >= s->cancel_after) {
        return 1;                        /* non-zero == cancel */
    }
    return 0;
}

static int progress_count(const sha_progress_t *p, void *user) {
    (void)p;
    ((struct cb_state *)user)->calls++;
    return 0;
}

static void test_cancellation(void) {
    printf("  cancellation\n");

    /* 8 MiB file, 64 KiB buffer => 128 reads, so a callback after the
     * first call cancels long before the end. */
    static uint8_t big[8 * 1024 * 1024];
    for (size_t i = 0; i < sizeof(big); i++) {
        big[i] = (uint8_t)(i * 31 + 7);
    }

    char path[600];
    snprintf(path, sizeof(path), "%s/big.bin", g_dir);
    ok(write_file(path, big, sizeof(big)) == 0, "write 8 MiB file");

    /* (a) cancel from inside the progress callback */
    {
        struct cb_state s = { 0, 1, 0 };
        sha_cancel_t cancel;
        sha_cancel_clear(&cancel);

        uint8_t digest[SHA256_DIGEST_LEN];
        sha_status_t st = sha_hash_file(path, SHA_ALGO_SHA256, 64 * 1024,
                                        progress_cancel, &s, &cancel,
                                        digest, sizeof(digest), NULL);
        ok(st == SHA_ERR_CANCELLED,
           "callback returning 1 cancels the run");
        ok(s.calls >= 1, "callback was invoked before cancelling");
    }

    /* (b) cancel from another thread via sha_cancel_request() */
    {
        sha_cancel_t cancel;
        sha_cancel_clear(&cancel);
        ok(sha_cancelled(&cancel) == 0, "fresh handle is not cancelled");

        sha_cancel_request(&cancel);
        ok(sha_cancelled(&cancel) == 1, "sha_cancel_request sets the flag");

        uint8_t digest[SHA256_DIGEST_LEN];
        sha_status_t st = sha_hash_file(path, SHA_ALGO_SHA256, 64 * 1024,
                                        NULL, NULL, &cancel,
                                        digest, sizeof(digest), NULL);
        ok(st == SHA_ERR_CANCELLED,
           "pre-set cancel flag aborts before/at first read");

        sha_cancel_clear(&cancel);
        ok(sha_cancelled(&cancel) == 0, "sha_cancel_clear resets the flag");

        st = sha_hash_file(path, SHA_ALGO_SHA256, 64 * 1024,
                           NULL, NULL, &cancel,
                           digest, sizeof(digest), NULL);
        ok(st == SHA_OK, "run succeeds after clearing the flag");
    }

    /* (c) NULL cancel handle must be tolerated */
    {
        uint8_t digest[SHA256_DIGEST_LEN];
        ok(sha_hash_file(path, SHA_ALGO_SHA1, 64 * 1024,
                         NULL, NULL, NULL,
                         digest, sizeof(digest), NULL) == SHA_OK,
           "NULL cancel handle tolerated");
    }
}

/* ---- 5. progress throttling ------------------------------------------- */

static void test_progress(void) {
    printf("  progress reporting\n");

    char path[600];
    snprintf(path, sizeof(path), "%s/big.bin", g_dir);

    struct cb_state s = { 0, 0, 0 };
    uint8_t digest[SHA256_DIGEST_LEN];

    /* 8 MiB at 64 KiB chunks would be 128 reads; throttling must keep the
     * callback count far below that (10 Hz over a sub-second run). */
    sha_status_t st = sha_hash_file(path, SHA_ALGO_SHA256, 64 * 1024,
                                    progress_count, &s, NULL,
                                    digest, sizeof(digest), NULL);
    ok(st == SHA_OK, "progress run completes");
    ok(s.calls > 0, "progress callback invoked");
    ok(s.calls < 128, "progress throttled (fewer calls than reads)");
}

/* ---- 6. >4 GB file (opt-in) -------------------------------------------- */

static void test_large_file(void) {
    const char *flag = getenv("SHAGUI_TEST_BIG");
    if (flag == NULL || strcmp(flag, "1") != 0) {
        printf("  large file: SKIPPED (set SHAGUI_TEST_BIG=1 to enable)\n");
        return;
    }

    printf("  large file (>4 GB, 1 MiB buffer)\n");

    char path[600];
    snprintf(path, sizeof(path), "%s/huge.bin", g_dir);

    /*
     * Build the file by repeating a deterministic 1 MiB pattern, then hash
     * it twice: once streaming through the file API with a 1 MiB buffer
     * (which exercises >4 GB counters) and once by feeding the same pattern
     * to the in-memory API. The two must agree.
     */
    static uint8_t pattern[1024 * 1024];
    for (size_t i = 0; i < sizeof(pattern); i++) {
        pattern[i] = (uint8_t)((i * 131u + 17u) & 0xFFu);
    }

    const uint64_t total = ((uint64_t)5 << 30);   /* 5 GiB */
    const uint64_t reps  = total / sizeof(pattern);

    FILE *f = fopen(path, "wb");
    if (f == NULL) {
        printf("    FAIL  could not create large file\n");
        g_failed++;
        return;
    }
    for (uint64_t r = 0; r < reps; r++) {
        if (fwrite(pattern, 1, sizeof(pattern), f) != sizeof(pattern)) {
            printf("    FAIL  writing large file\n");
            g_failed++;
            fclose(f);
            return;
        }
    }
    fclose(f);

    uint8_t digest[SHA256_DIGEST_LEN];
    char actual[SHA256_DIGEST_LEN * 2 + 1];
    sha_status_t st = sha_hash_file(path, SHA_ALGO_SHA256, 1024 * 1024,
                                    NULL, NULL, NULL,
                                    digest, sizeof(digest), NULL);
    to_hex(digest, SHA256_DIGEST_LEN, actual);
    ok(st == SHA_OK, "5 GiB file hashed");

    /* Reference: same pattern fed reps times through the streaming API. */
    sha_ctx_t *ctx = NULL;
    sha_create(SHA_ALGO_SHA256, &ctx);
    for (uint64_t r = 0; r < reps; r++) {
        sha_update(ctx, pattern, sizeof(pattern));
    }
    uint8_t ref[SHA256_DIGEST_LEN];
    size_t w = 0;
    sha_final(ctx, ref, sizeof(ref), &w);
    sha_destroy(ctx);

    char refhex[SHA256_DIGEST_LEN * 2 + 1];
    to_hex(ref, w, refhex);
    ok(strcmp(actual, refhex) == 0,
       "5 GiB streamed digest == in-memory reference");
    ok(total > 0xFFFFFFFFull, "test really did exceed 4 GB");
}

/* ---- main -------------------------------------------------------------- */

int main(void) {
    printf("===== file / cancellation tests =====\n\n");

    /* Private temp directory so a failure cannot touch user data. */
    snprintf(g_dir, sizeof(g_dir), "shagui_test_%u",
             (unsigned)GetCurrentProcessId());
#if defined(_WIN32) || defined(_WIN64)
    _mkdir(g_dir);
#else
    mkdir(g_dir, 0700);
#endif

    test_file_matches_memory();
    test_unicode_path();
    test_error_paths();
    test_cancellation();
    test_progress();
    test_large_file();

    /* Remove our scratch directory (and its contents) so repeated runs
     * never leave megabytes behind in the working directory. */
    {
        static const char *files[] = {
            "plain.bin", "empty.bin", "big.bin", "huge.bin",
            "thu vien - kiểm tra.bin"
        };
        char p[600];
        for (size_t i = 0; i < sizeof(files) / sizeof(files[0]); i++) {
            snprintf(p, sizeof(p), "%s/%s", g_dir, files[i]);
#if defined(_WIN32) || defined(_WIN64)
            /* ANSI remove() cannot address the unicode filename, so go
             * through the wide API exactly like write_file() does. */
            {
                wchar_t wp[1024];
                int n = MultiByteToWideChar(CP_UTF8, 0, p, -1, wp,
                                            (int)(sizeof(wp) / sizeof(wp[0])));
                if (n > 0) {
                    _wremove(wp);
                }
            }
#else
            remove(p);
#endif
        }
#if defined(_WIN32) || defined(_WIN64)
        _rmdir(g_dir);   /* only succeeds once the files above are gone */
#else
        rmdir(g_dir);
#endif
    }

    printf("\n===== summary =====\n");
    printf("passed : %d\n", g_passed);
    printf("failed : %d\n", g_failed);

    return (g_failed == 0) ? 0 : 1;
}

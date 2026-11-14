/*
 * sha_file.c — streaming file hashing and cancellation.
 *
 * sha_hash_file() is the only native routine that touches the filesystem;
 * the managed layer owns folder recursion, manifests and archives (see
 * docs/ARCHITECTURE.md). It exists here so that the read+hash loop — the
 * part where throughput and cancellation latency actually matter — runs
 * without a managed byte[] round-trip per buffer.
 *
 * Design notes:
 *   - Fixed-size buffer, allocated once: memory use is constant regardless
 *     of file size, and files larger than RAM work.
 *   - The file's size is sampled at open and compared with the byte count
 *     actually read. If the file changed underneath us we return
 *     SHA_ERR_CHANGED rather than a digest of a moving target.
 *   - The progress callback is throttled to ~10 Hz so a 1 ms UI dispatcher
 *     is not flooded for a 50 GB file.
 *   - elapsed_100ns times only the read+hash loop (QueryPerformanceCounter,
 *     FILETIME granularity), excluding open/close and UI work.
 */
#include "sha_internal.h"

#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#if defined(_WIN32) || defined(_WIN64)
#  define WIN32_LEAN_AND_MEAN
#  include <windows.h>
#  include <sys/stat.h>   /* _fstat64, _S_IFREG */
#else
#  include <fcntl.h>
#  include <sys/stat.h>
#  include <time.h>
#endif

/* ---------------------------------------------------------------------- */
/* Cancellation                                                           */
/* ---------------------------------------------------------------------- */

/*
 * Every mutation goes through the DLL: a plain volatile write from the
 * managed side would have no ordering guarantee against a native read on
 * another core. InterlockedExchange() gives us the release semantics we
 * need, and InterlockedCompareExchange() the acquire on the read side.
 */
void sha_cancel_request(sha_cancel_t *c) {
    if (c == NULL) {
        return;
    }
#if defined(_WIN32) || defined(_WIN64)
    InterlockedExchange(&c->flag, 1);
#else
    __atomic_store_n(&c->flag, 1, __ATOMIC_RELEASE);
#endif
}

void sha_cancel_clear(sha_cancel_t *c) {
    if (c == NULL) {
        return;
    }
#if defined(_WIN32) || defined(_WIN64)
    InterlockedExchange(&c->flag, 0);
#else
    __atomic_store_n(&c->flag, 0, __ATOMIC_RELEASE);
#endif
}

int sha_cancelled(const sha_cancel_t *c) {
    if (c == NULL) {
        return 0;
    }
#if defined(_WIN32) || defined(_WIN64)
    return InterlockedCompareExchange((volatile long *)&c->flag, 0, 0) != 0;
#else
    return __atomic_load_n(&c->flag, __ATOMIC_ACQUIRE) != 0;
#endif
}

/* ---------------------------------------------------------------------- */
/* Timing                                                                 */
/* ---------------------------------------------------------------------- */

#if !(defined(_WIN32) || defined(_WIN64))
/* Windows measures elapsed time with QPC below; this is the POSIX path. */
static uint64_t now_100ns(void) {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (uint64_t)ts.tv_sec * 10000000ull + (uint64_t)ts.tv_nsec / 100ull;
}
#endif

/* Monotonic tick source for throttling and elapsed measurement. */
static uint64_t qpc_ticks(void) {
#if defined(_WIN32) || defined(_WIN64)
    LARGE_INTEGER li;
    QueryPerformanceCounter(&li);
    return (uint64_t)li.QuadPart;
#else
    return now_100ns();
#endif
}

static uint64_t qpc_freq(void) {
#if defined(_WIN32) || defined(_WIN64)
    LARGE_INTEGER li;
    QueryPerformanceFrequency(&li);
    return (uint64_t)li.QuadPart;
#else
    return 10000000ull;
#endif
}

/* ---------------------------------------------------------------------- */
/* File hashing                                                           */
/* ---------------------------------------------------------------------- */

/* How often the progress callback may fire: 100 ms == 10 Hz. */
#define PROGRESS_INTERVAL_TICKS 100ull

static void fill_progress(sha_progress_t *p, uint64_t done, uint64_t total,
                          const char *path) {
    memset(p, 0, sizeof(*p));
    p->struct_size      = (uint32_t)sizeof(*p);
    p->stage            = SHA_STAGE_HASHING;
    p->file_bytes_done  = done;
    p->file_bytes_total = total;
    p->bytes_done       = done;
    p->bytes_total      = total;

    if (path == NULL) {
        p->path_truncated = 1;
        return;
    }
    /* UTF-8 may be cut mid-sequence; that is acceptable for a status line,
     * and the managed side re-encodes from its own copy of the path. */
    size_t len = strlen(path);
    if (len >= SHA_PROGRESS_PATH_LEN) {
        len = SHA_PROGRESS_PATH_LEN - 1;
        p->path_truncated = 1;
    }
    memcpy(p->current_path, path, len);
    p->current_path[len] = '\0';
}

sha_status_t sha_hash_file(const char *utf8_path, sha_algo_t algo,
                           uint64_t buf_size,
                           sha_progress_cb cb, void *cb_user,
                           sha_cancel_t *cancel,
                           uint8_t *out, size_t cap,
                           uint64_t *elapsed_100ns) {
    if (utf8_path == NULL || out == NULL) {
        return SHA_ERR_INVALID_ARG;
    }
    const size_t need = sha_digest_length(algo);
    if (need == 0) {
        return SHA_ERR_UNSUPPORTED;
    }
    if (cap < need) {
        return SHA_ERR_INVALID_ARG;
    }
    if (strlen(utf8_path) == 0) {
        return SHA_ERR_INVALID_ARG;
    }
    if (buf_size == 0) {
        buf_size = SHA_DEFAULT_BUFFER_SIZE;
    }
    /* Refuse absurd buffer requests up front instead of failing malloc. */
    if (buf_size > ((uint64_t)1 << 40)) {
        return SHA_ERR_INVALID_ARG;
    }

    sha_status_t status = SHA_OK;
    sha_ctx_t *ctx = NULL;
    uint8_t *buffer = NULL;
    FILE *f = NULL;

    status = sha_create(algo, &ctx);
    if (status != SHA_OK) {
        return status;
    }

    buffer = (uint8_t *)malloc((size_t)buf_size);
    if (buffer == NULL) {
        status = SHA_ERR_NOMEM;
        goto done;
    }

    /*
     * Open in binary mode through the wide-character API so non-ASCII
     * paths (Vietnamese, Chinese, ...) survive. fopen(char*) would depend
     * on the ANSI code page and silently fail outside CP_ACP.
     */
#if defined(_WIN32) || defined(_WIN64)
    {
        int wlen = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS,
                                       utf8_path, -1, NULL, 0);
        if (wlen <= 0) {
            status = SHA_ERR_UTF8;
            goto done;
        }
        wchar_t *wpath = (wchar_t *)malloc((size_t)wlen * sizeof(wchar_t));
        if (wpath == NULL) {
            status = SHA_ERR_NOMEM;
            goto done;
        }
        MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS,
                            utf8_path, -1, wpath, wlen);
        if (_wfopen_s(&f, wpath, L"rb") != 0) {
            f = NULL;
        }
        free(wpath);
    }
#else
    f = fopen(utf8_path, "rb");
#endif

    if (f == NULL) {
        /* Distinguish "missing" from "no permission" only loosely: the
         * managed layer refines this with Marshal.GetLastWin32Error(). */
        status = (errno == EACCES || errno == EPERM)
                     ? SHA_ERR_ACCESS
                     : SHA_ERR_IO;
        goto done;
    }

    /* Sample the size up front so a file that changes during the read can
     * be detected instead of producing a digest of a moving target. */
    uint64_t expected_size = 0;
    {
#if defined(_WIN32) || defined(_WIN64)
        struct _stat64 st;
        if (_fstat64(_fileno(f), &st) == 0 && (st.st_mode & _S_IFREG)) {
            expected_size = (uint64_t)st.st_size;
        }
#else
        struct stat st;
        if (fstat(fileno(f), &st) == 0 && S_ISREG(st.st_mode)) {
            expected_size = (uint64_t)st.st_size;
        }
#endif
    }

    const uint64_t start_tick = qpc_ticks();
    const uint64_t freq       = qpc_freq();
    uint64_t last_cb_tick     = start_tick;
    uint64_t total_read       = 0;
    int cancelled             = 0;

    /* Initial progress so the UI shows the path immediately. */
    if (cb != NULL) {
        sha_progress_t p;
        fill_progress(&p, 0, expected_size, utf8_path);
        if (cb(&p, cb_user) != 0) {
            cancelled = 1;
        }
        last_cb_tick = start_tick;
    }

    for (;;) {
        if (cancel != NULL && sha_cancelled(cancel)) {
            cancelled = 1;
            break;
        }

        size_t got = fread(buffer, 1, (size_t)buf_size, f);
        if (got == 0) {
            if (ferror(f)) {
                status = SHA_ERR_IO;
                break;
            }
            break;                        /* clean EOF */
        }

        status = sha_update(ctx, buffer, (uint64_t)got);
        if (status != SHA_OK) {
            break;
        }
        total_read += (uint64_t)got;

        if (cb != NULL) {
            uint64_t tick = qpc_ticks();
            if (tick - last_cb_tick >=
                (PROGRESS_INTERVAL_TICKS * freq) / 1000ull) {
                sha_progress_t p;
                fill_progress(&p, total_read, expected_size, utf8_path);
                if (cb(&p, cb_user) != 0) {
                    cancelled = 1;
                    break;
                }
                last_cb_tick = tick;
            }
        }
    }

    if (status == SHA_OK && !cancelled) {
        if (ferror(f)) {
            status = SHA_ERR_IO;
        } else if (expected_size != 0 && total_read != expected_size) {
            status = SHA_ERR_CHANGED;     /* grew or shrank mid-read */
        }
    }

    if (status == SHA_OK && !cancelled) {
        status = sha_final(ctx, out, cap, NULL);
    }

    if (cancelled && status == SHA_OK) {
        status = SHA_ERR_CANCELLED;
    }

    if (elapsed_100ns != NULL) {
        const uint64_t end_tick = qpc_ticks();
        /* Convert QPC ticks to 100 ns units without losing precision. */
        uint64_t delta = end_tick - start_tick;
        *elapsed_100ns = (freq == 0) ? 0 : (delta * 10000000ull) / freq;
    }

    /* Final 100% progress callback (or the cancellation state). */
    if (cb != NULL && status != SHA_ERR_IO && status != SHA_ERR_ACCESS) {
        sha_progress_t p;
        fill_progress(&p, total_read,
                      (status == SHA_ERR_CANCELLED) ? total_read
                                                    : expected_size,
                      utf8_path);
        p.stage = (status == SHA_ERR_CANCELLED) ? SHA_STAGE_HASHING
                                                : SHA_STAGE_FINALIZING;
        (void)cb(&p, cb_user);
    }

done:
    if (f != NULL) {
        fclose(f);
    }
    if (ctx != NULL) {
        sha_destroy(ctx);      /* wipes its own partial block */
    }
    if (buffer != NULL) {
        memset(buffer, 0, (size_t)buf_size);  /* before free, not after */
        free(buffer);
    }
    return status;
}

// LD_PRELOAD shim: logs failed fcntl() lock calls (any file) and every lock call on a catalog.db file to stderr.
// Built into the image and switched on with ASB_FCNTL_TRACE=1 (see docker/entrypoint.sh). Diagnostic only.
#define _GNU_SOURCE
#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <stdarg.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>

static int (*real_fcntl)(int, int, ...);
static int (*real_fcntl64)(int, int, ...);

static void path_of(int fd, char *buf, size_t n) {
    char link[64];
    snprintf(link, sizeof link, "/proc/self/fd/%d", fd);
    ssize_t k = readlink(link, buf, n - 1);
    if (k < 0) k = 0;
    buf[k] = 0;
}

static int spy(int (**real)(int, int, ...), const char *sym, int fd, int cmd, void *arg) {
    if (!*real) *real = dlsym(RTLD_NEXT, sym);
    int is_lock = cmd == F_SETLK || cmd == F_SETLKW || cmd == F_GETLK
               || cmd == F_OFD_SETLK || cmd == F_OFD_SETLKW || cmd == F_OFD_GETLK;
    struct flock before;
    if (is_lock) before = *(struct flock *)arg;
    int rc = (*real)(fd, cmd, arg);
    int err = errno;
    if (is_lock) {
        char path[512];
        path_of(fd, path, sizeof path);
        if (rc < 0 || strstr(path, "catalog.db")) {
            struct flock *fl = arg;
            char line[768];
            int n = snprintf(line, sizeof line,
                "fcntlspy fd=%d cmd=%d type=%d start=%lld len=%lld rc=%d errno=%d got_type=%d got_pid=%d %s\n",
                fd, cmd, (int)before.l_type, (long long)before.l_start, (long long)before.l_len,
                rc, rc < 0 ? err : 0, (int)fl->l_type, (int)fl->l_pid, path);
            if (n > 0 && write(2, line, n) < 0) { }
        }
    }
    errno = err;
    return rc;
}

int fcntl(int fd, int cmd, ...) {
    va_list ap; va_start(ap, cmd); void *arg = va_arg(ap, void *); va_end(ap);
    return spy(&real_fcntl, "fcntl", fd, cmd, arg);
}

int fcntl64(int fd, int cmd, ...) {
    va_list ap; va_start(ap, cmd); void *arg = va_arg(ap, void *); va_end(ap);
    return spy(&real_fcntl64, "fcntl64", fd, cmd, arg);
}

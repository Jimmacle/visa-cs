/*
 * A stand-in for an NI-488.2 compatible GPIB driver (ni4882.dll / libgpib.so), used to exercise VisaCs's
 * GPIB binding without hardware. It exports the subset of the NI-488.2 C API that Ni488Driver calls, with
 * the same signatures, ibsta/iberr/ibcnt semantics and error codes. Board 0 has two instruments, at
 * primary addresses 5 and 22. They answer *IDN?, ECHO? <text>, DATA? <n> (binary block), DELAY? <ms> <text>
 * (answers after a delay), NOEOI? <text> (answers without EOI, so a longer read times out with partial
 * data), SRQ (request service), *CLS and TRG? (trigger count).
 *
 * Asynchronous I/O behaves like NI-488.2: ibrda/ibwrta/ibcmda start a transfer that a library-owned thread
 * carries out (a read waits for data until the descriptor's timeout). Until the transfer is resynchronized
 * by ibwait with CMPL in the mask, ibstop, ibonl or an ibnotify callback, other calls on the descriptor fail
 * with EOIP. ibwait with mask 0 reports CMPL once the transfer has finished. ibnotify callbacks run on the
 * same library thread, are one-shot, and are rearmed with the callback's return value; calling ibnotify
 * inside a callback fails with ECAP. While autopolling has seen a request, device descriptors report RQS.
 * The termination character is compared in 7 bits unless IbcEOScmp is set, as in NI-488.2.
 *
 * Variants:
 *   (default)          NI-488.2: asynchronous calls and ibnotify.
 *   -DFAKE_NO_NOTIFY   linux-gpib: no ibnotify. AsyncIbsta/AsyncIberr/AsyncIbcnt report the transfer that
 *                      ibwait resynchronized, while ibwait itself reports only its own result (ibcnt 0).
 *   -DFAKE_NO_ASYNC    an older library: no ibrda/ibwrta/ibcmda/ibstop/ibnotify.
 * fake_ni4882_control() lets tests make ibnotify fail, lose completion notifications, slow down blocking calls,
 * let a callback's return value undo a concurrent ibnotify, hide SRQI as NI-488.2 does while autopolling, and
 * read counters.
 *
 * Build:  Linux   build-fake-gpib.sh  (cc -shared -fPIC -O2 -pthread -o libfake_ni4882.so fake_ni4882.c)
 *         Windows build-fake-gpib.cmd (cl /LD /O2 fake_ni4882.c /Fe:fake_ni4882.dll)
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#include <windows.h>
#include <process.h>
#define EXPORT __declspec(dllexport)
#define THREAD_LOCAL __declspec(thread)
#define NI488CC __stdcall
static SRWLOCK state_lock = SRWLOCK_INIT;
static CONDITION_VARIABLE state_changed = CONDITION_VARIABLE_INIT;
static void enter(void) { AcquireSRWLockExclusive(&state_lock); }
static void leave(void) { ReleaseSRWLockExclusive(&state_lock); }
static void signal_all(void) { WakeAllConditionVariable(&state_changed); }
static long long now_ms(void) { return (long long)GetTickCount64(); }
static void pause_ms(int ms) { Sleep((DWORD)ms); }
#ifndef FAKE_NO_ASYNC
/* Waits with the lock held until signalled or ms pass. */
static void wait_signal(int ms) { SleepConditionVariableSRW(&state_changed, &state_lock, (DWORD)ms, 0); }
#endif
#else
#include <pthread.h>
#include <time.h>
#include <unistd.h>
#define EXPORT __attribute__((visibility("default")))
#define THREAD_LOCAL __thread
#define NI488CC
static pthread_mutex_t state_lock = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t state_changed = PTHREAD_COND_INITIALIZER;
static void enter(void) { pthread_mutex_lock(&state_lock); }
static void leave(void) { pthread_mutex_unlock(&state_lock); }
static void signal_all(void) { pthread_cond_broadcast(&state_changed); }
static long long now_ms(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (long long)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
}
static void pause_ms(int ms) { usleep((useconds_t)ms * 1000); }
#ifndef FAKE_NO_ASYNC
static void wait_signal(int ms)
{
    struct timespec ts;
    clock_gettime(CLOCK_REALTIME, &ts);
    ts.tv_sec += ms / 1000;
    ts.tv_nsec += (long)(ms % 1000) * 1000000L;
    if (ts.tv_nsec >= 1000000000L)
    {
        ts.tv_sec++;
        ts.tv_nsec -= 1000000000L;
    }
    pthread_cond_timedwait(&state_changed, &state_lock, &ts);
}
#endif
#endif

#define ERR 0x8000
#define TIMO 0x4000
#define END 0x2000
#define SRQI 0x1000
#define RQS 0x0800
#define CMPL 0x0100
#define CIC 0x0020

#define ENOL 2
#define EARG 4
#define EABO 6
#define ENEB 7
#define EOIP 10
#define ECAP 11

#define MAX_UD 256
#define BUF 65536

typedef struct
{
    int used;
    int pad;
    int eot;
    int eos_enabled;
    int eos_char;
    int eos_bin; /* compare all 8 bits of the termination character */
    int readdr;
    int unaddr;
    int timeout_code;
} Descriptor;

typedef struct
{
    int pad;
    unsigned char input[BUF];
    size_t input_len;
    unsigned char output[BUF];
    size_t output_len;
    size_t output_pos;
    int output_eoi;         /* the last output byte carries EOI */
    long long output_ready; /* DELAY?: the answer is available from this time on */
    unsigned char stb;
    int srq_pending;
    int triggers;
    int clears;
    int remote;
} Instrument;

enum { OP_IDLE, OP_RUNNING, OP_DONE };
enum { OP_READ = 1, OP_WRITE, OP_COMMAND };

/* An asynchronous transfer; OP_DONE means finished but not yet resynchronized. */
typedef struct
{
    int state;
    int kind;
    unsigned char *buf;
    size_t cnt;
    size_t got;
    long long deadline; /* 0: no timeout */
    int status;
    int error;
} AsyncOp;

typedef int (NI488CC *NotifyCallback)(int ud, int ibsta, int iberr, long ibcntl, void *ref);

typedef struct
{
    int mask;
    NotifyCallback callback;
    void *ref;
    unsigned generation;
} Notification;

static Descriptor descriptors[MAX_UD];
static AsyncOp ops[MAX_UD];
static Notification notifications[MAX_UD];
static Instrument instruments[2] = {{.pad = 5, .output_eoi = 1}, {.pad = 22, .output_eoi = 1}};
static int ren_state;
static int board_timeout_code = 13;
static int notify_fails, drop_notifications, stop_calls, notify_resyncs, wait_resyncs, call_delay_ms;
static int override_delay_ms, overridden, autopoll_hides_srqi, serial_polls;
static THREAD_LOCAL int ibsta_value, iberr_value, ibcnt_value;
#ifndef FAKE_NO_ASYNC
static int engine_started;
static THREAD_LOCAL int async_sta, async_err, async_cnt; /* the last transfer resynchronized on this thread */
#ifndef FAKE_NO_NOTIFY
static THREAD_LOCAL int in_callback;
#endif
#endif

static int report(int status, int error, int count)
{
    ibsta_value = status;
    iberr_value = error;
    ibcnt_value = count;
    return status;
}

/* Result of a synchronous call: CMPL unless it failed. */
static int done(int status, int error, int count) { return report(status | ((status & ERR) ? 0 : CMPL), error, count); }

static int fail(int error) { return done(ERR, error, 0); }

static Instrument *find_instrument(int pad)
{
    for (int i = 0; i < 2; i++)
        if (instruments[i].pad == pad) return &instruments[i];
    return NULL;
}

static Descriptor *device_descriptor(int ud)
{
    if (ud < 16 || ud >= MAX_UD || !descriptors[ud].used) return NULL;
    return &descriptors[ud];
}

/* A transfer is in progress or waits to be resynchronized: most calls fail with EOIP. */
static int busy(int ud) { return ud >= 0 && ud < MAX_UD && ops[ud].state != OP_IDLE; }

static void respond(Instrument *ins, const char *text, size_t length)
{
    if (length + 1 > BUF) length = BUF - 1;
    memcpy(ins->output, text, length);
    ins->output[length] = '\n';
    ins->output_len = length + 1;
    ins->output_pos = 0;
    ins->output_eoi = 1;
    ins->output_ready = 0;
}

static void process(Instrument *ins)
{
    char command[256];
    size_t n = ins->input_len < sizeof(command) - 1 ? ins->input_len : sizeof(command) - 1;
    memcpy(command, ins->input, n);
    command[n] = 0;
    ins->input_len = 0;
    while (n > 0 && (command[n - 1] == '\n' || command[n - 1] == '\r')) command[--n] = 0;

    char reply[512];
    if (strcmp(command, "*IDN?") == 0)
    {
        int len = snprintf(reply, sizeof(reply), "FAKE,GPIB,%d,1.0", ins->pad);
        respond(ins, reply, (size_t)len);
    }
    else if (strncmp(command, "ECHO? ", 6) == 0)
    {
        respond(ins, command + 6, strlen(command + 6));
    }
    else if (strncmp(command, "DELAY? ", 7) == 0)
    {
        char *text = NULL;
        long delay = strtol(command + 7, &text, 10);
        if (*text == ' ') text++;
        respond(ins, text, strlen(text));
        ins->output_ready = now_ms() + delay;
    }
    else if (strncmp(command, "NOEOI? ", 7) == 0)
    {
        respond(ins, command + 7, strlen(command + 7));
        ins->output_eoi = 0;
    }
    else if (strcmp(command, "TRG?") == 0)
    {
        int len = snprintf(reply, sizeof(reply), "%d", ins->triggers);
        respond(ins, reply, (size_t)len);
    }
    else if (strncmp(command, "DATA? ", 6) == 0)
    {
        int count = atoi(command + 6);
        if (count < 0 || count > BUF - 32) count = 0;
        char header[32];
        int digits = snprintf(header, sizeof(header), "%d", count);
        int header_len = snprintf(reply, sizeof(reply), "#%d%d", digits, count);
        memcpy(ins->output, reply, (size_t)header_len);
        for (int i = 0; i < count; i++) ins->output[header_len + i] = (unsigned char)i;
        ins->output[header_len + count] = '\n';
        ins->output_len = (size_t)header_len + (size_t)count + 1;
        ins->output_pos = 0;
        ins->output_eoi = 1;
        ins->output_ready = 0;
    }
    else if (strcmp(command, "SRQ") == 0)
    {
        ins->stb |= 0x41;
        ins->srq_pending = 1;
    }
    else if (strcmp(command, "*CLS") == 0)
    {
        ins->stb = 0;
        ins->srq_pending = 0;
    }
}

/* Bytes the instrument can talk now. */
static size_t output_available(const Instrument *ins)
{
    if (ins->output_ready != 0 && now_ms() < ins->output_ready) return 0;
    return ins->output_len - ins->output_pos;
}

/* Moves up to cnt bytes of the instrument's answer to buf; *end tells whether EOI or EOS ended them. */
static size_t read_bytes(const Descriptor *d, Instrument *ins, unsigned char *buf, size_t cnt, int *end)
{
    size_t available = output_available(ins);
    size_t n = cnt < available ? cnt : available;
    *end = 0;
    if (d->eos_enabled)
    {
        for (size_t i = 0; i < n; i++)
        {
            int mask = d->eos_bin ? 0xFF : 0x7F;
            if ((ins->output[ins->output_pos + i] & mask) == (d->eos_char & mask))
            {
                n = i + 1;
                *end = 1;
                break;
            }
        }
    }
    memcpy(buf, ins->output + ins->output_pos, n);
    ins->output_pos += n;
    if (n > 0 && ins->output_pos >= ins->output_len)
    {
        if (ins->output_eoi) *end = 1; /* EOI with the last byte */
        ins->output_len = ins->output_pos = 0;
    }
    return n;
}

static int write_bytes(const Descriptor *d, Instrument *ins, const void *buf, size_t cnt)
{
    if (ins->input_len + cnt > BUF) return 0;
    memcpy(ins->input + ins->input_len, buf, cnt);
    ins->input_len += cnt;
    ins->remote = 1;
    if (d->eot) process(ins);
    signal_all(); /* an answer may complete a pending read */
    return 1;
}

/* What ibwait(ud, 0) reports. */
static int current_status(int ud)
{
    int status = 0;
    if (ud == 0)
    {
        status |= CIC;
        /* NI-488.2 reports SRQI only while automatic serial polling is off */
        if ((instruments[0].srq_pending || instruments[1].srq_pending) && !autopoll_hides_srqi) status |= SRQI;
    }
    else
    {
        Descriptor *d = device_descriptor(ud);
        Instrument *ins = d != NULL ? find_instrument(d->pad) : NULL;
        if (ins != NULL && ins->srq_pending) status |= RQS; /* autopolling has seen the request */
    }
    if (ud < 0 || ud >= MAX_UD || ops[ud].state != OP_RUNNING) status |= CMPL;
    return status;
}

EXPORT int ThreadIbsta(void) { return ibsta_value; }
EXPORT int ThreadIberr(void) { return iberr_value; }
EXPORT int ThreadIbcnt(void) { return ibcnt_value; }

EXPORT int ibdev(int board, int pad, int sad, int tmo, int eot, int eos)
{
    (void)sad;
    if (board != 0)
    {
        fail(ENEB);
        return -1;
    }
    enter();
    for (int ud = 16; ud < MAX_UD; ud++)
    {
        if (descriptors[ud].used) continue;
        Descriptor d = {1, pad, eot, (eos & 0x400) != 0, eos & 0xFF, (eos & 0x1000) != 0, 1, 0, tmo};
        descriptors[ud] = d;
        leave();
        done(0, 0, 0);
        return ud;
    }
    leave();
    fail(EARG);
    return -1;
}

EXPORT int ibonl(int ud, int v)
{
    enter();
    if (v == 0 && ud >= 0 && ud < MAX_UD)
    {
        ops[ud].state = OP_IDLE; /* going offline aborts the transfer */
        notifications[ud].mask = 0;
        notifications[ud].generation++;
        if (ud >= 16) descriptors[ud].used = 0;
        signal_all();
    }
    leave();
    return done(0, 0, 0);
}

static int write_locked(int ud, const void *buf, size_t cnt)
{
    Descriptor *d = device_descriptor(ud);
    if (d == NULL) return fail(EARG);
    if (busy(ud)) return fail(EOIP);
    Instrument *ins = find_instrument(d->pad);
    if (ins == NULL) return fail(ENOL);
    if (!write_bytes(d, ins, buf, cnt)) return fail(EARG);
    return done(0, 0, (int)cnt);
}

EXPORT int ibwrt(int ud, const void *buf, long long cnt)
{
    enter();
    int status = write_locked(ud, buf, (size_t)cnt);
    leave();
    return status;
}

static const int timeout_table[] = {0, 1, 1, 1, 1, 1, 3, 10, 30, 100, 300, 1000, 3000, 10000, 30000, 100000, 300000, 1000000};

/* Milliseconds of an ibtmo code; 0 for none. */
static int timeout_ms(int code)
{
    return code <= 0 || code >= (int)(sizeof(timeout_table) / sizeof(timeout_table[0])) ? 0 : timeout_table[code];
}

static int read_locked(int ud, void *buf, size_t cnt)
{
    Descriptor *d = device_descriptor(ud);
    if (d == NULL) return fail(EARG);
    if (busy(ud)) return fail(EOIP);
    Instrument *ins = find_instrument(d->pad);
    if (ins == NULL) return fail(ENOL);
    int limit = timeout_ms(d->timeout_code);
    long long deadline = limit > 0 ? now_ms() + limit : 0;
    while (ins->output_len > ins->output_pos && output_available(ins) == 0)
    {
        /* a delayed answer: wait for it, but no longer than the descriptor's timeout */
        if (deadline != 0 && now_ms() >= deadline) break;
        leave();
        pause_ms(1);
        enter();
    }
    if (output_available(ins) == 0) return done(ERR | TIMO, EABO, 0); /* nothing to talk: the read times out */
    int end;
    size_t n = read_bytes(d, ins, buf, cnt, &end);
    if (!end && n < cnt) return done(ERR | TIMO, EABO, (int)n); /* no EOI: the rest never comes */
    return done(end ? END : 0, 0, (int)n);
}

EXPORT int ibrd(int ud, void *buf, long long cnt)
{
    enter();
    int status = read_locked(ud, buf, (size_t)cnt);
    leave();
    return status;
}

EXPORT int ibcmd(int ud, const void *buf, long long cnt)
{
    (void)buf;
    if (ud != 0) return fail(EARG);
    enter();
    int status = busy(ud) ? fail(EOIP) : done(CIC, 0, (int)cnt);
    leave();
    return status;
}

/* The instrument behind a device descriptor that is free for a synchronous call, or NULL after failing. */
static Instrument *device_call(int ud)
{
    Descriptor *d = device_descriptor(ud);
    if (d == NULL)
    {
        fail(EARG);
        return NULL;
    }
    if (busy(ud))
    {
        fail(EOIP);
        return NULL;
    }
    Instrument *ins = find_instrument(d->pad);
    if (ins == NULL) fail(ENOL);
    else if (call_delay_ms > 0)
    {
        /* a slow device */
        leave();
        pause_ms(call_delay_ms);
        enter();
    }
    return ins;
}

EXPORT int ibclr(int ud)
{
    enter();
    Instrument *ins = device_call(ud);
    if (ins != NULL)
    {
        ins->input_len = ins->output_len = ins->output_pos = 0;
        ins->clears++;
        done(0, 0, 0);
    }
    leave();
    return ibsta_value;
}

EXPORT int ibtrg(int ud)
{
    enter();
    Instrument *ins = device_call(ud);
    if (ins != NULL)
    {
        ins->triggers++;
        done(0, 0, 0);
    }
    leave();
    return ibsta_value;
}

EXPORT int ibrsp(int ud, char *spr)
{
    enter();
    Instrument *ins = device_call(ud);
    serial_polls++;
    if (ins != NULL)
    {
        *spr = (char)ins->stb;
        ins->stb &= (unsigned char)~0x40; /* the serial poll clears RQS */
        ins->srq_pending = 0;
        done(0, 0, 1);
    }
    leave();
    return ibsta_value;
}

EXPORT int ibloc(int ud)
{
    enter();
    Descriptor *d = device_descriptor(ud);
    if (d != NULL)
    {
        Instrument *ins = find_instrument(d->pad);
        if (ins != NULL) ins->remote = 0;
    }
    leave();
    return done(0, 0, 0);
}

EXPORT int ibsic(int ud) { return ud == 0 ? done(CIC, 0, 0) : fail(EARG); }

EXPORT int ibsre(int ud, int v)
{
    if (ud != 0) return fail(EARG);
    enter();
    ren_state = v != 0;
    leave();
    return done(CIC, 0, 0);
}

EXPORT int ibtmo(int ud, int v)
{
    enter();
    int status;
    if (busy(ud)) status = fail(EOIP);
    else
    {
        if (ud == 0) board_timeout_code = v;
        else if (ud >= 16 && ud < MAX_UD) descriptors[ud].timeout_code = v;
        status = ud > 3 && ud < 16 ? fail(EARG) : done(0, 0, 0);
    }
    leave();
    return status;
}

EXPORT int ibconfig(int ud, int option, int value)
{
    if (ud >= 0 && ud <= 3) return ud == 0 ? done(0, 0, 0) : fail(ENEB);
    enter();
    Descriptor *d = device_descriptor(ud);
    int status;
    if (d == NULL) status = fail(EARG);
    else if (busy(ud)) status = fail(EOIP);
    else
    {
        switch (option)
        {
            case 0x4: d->eot = value; break;
            case 0x6: d->readdr = value; break;
            case 0xC: d->eos_enabled = value; break;
            case 0xE: d->eos_bin = value; break;
            case 0xF: d->eos_char = value; break;
            case 0x1B: d->unaddr = value; break;
            default: break;
        }
        status = done(0, 0, 0);
    }
    leave();
    return status;
}

EXPORT int ibask(int ud, int option, int *value)
{
    if (ud >= 0 && ud <= 3)
    {
        if (ud != 0) return fail(ENEB);
        *value = option == 0xA ? 1 : 0; /* system controller; PAD 0 */
        return done(0, 0, 0);
    }
    enter();
    Descriptor *d = device_descriptor(ud);
    if (d != NULL)
    {
        switch (option)
        {
            case 0x1: *value = d->pad; break;
            case 0x6: *value = d->readdr; break;
            case 0x1B: *value = d->unaddr; break;
            default: *value = 0; break;
        }
    }
    leave();
    return d == NULL ? fail(EARG) : done(0, 0, 0);
}

EXPORT int ibln(int ud, int pad, int sad, short *listen)
{
    (void)sad;
    if (ud != 0) return fail(ENEB);
    *listen = find_instrument(pad) != NULL;
    return done(0, 0, 0);
}

#ifndef FAKE_NO_ASYNC

/* Resynchronizes a finished transfer (lock held). */
static int resync(int ud)
{
    AsyncOp *op = &ops[ud];
    op->state = OP_IDLE;
    async_sta = op->status | CMPL;
    async_err = op->error;
    async_cnt = (int)op->got;
#ifdef FAKE_NO_NOTIFY
    /* linux-gpib: ibwait reports its own result; the transfer's is in AsyncIbsta/AsyncIberr/AsyncIbcnt. */
    return report(current_status(ud), 0, 0);
#else
    return report(current_status(ud) | (op->status & (ERR | TIMO | END)), op->error, (int)op->got);
#endif
}
#endif

static int wait_locked(int ud, int mask)
{
    if (ud != 0 && device_descriptor(ud) == NULL) return done(0, 0, 0);
#ifndef FAKE_NO_ASYNC
    if (mask & CMPL)
    {
        while (ops[ud].state == OP_RUNNING) wait_signal(5);
        if (ops[ud].state == OP_DONE)
        {
            wait_resyncs++;
            return resync(ud);
        }
    }
#endif
    int status = current_status(ud);
    if ((mask & TIMO) && !(status & mask & ~TIMO))
    {
        /* a real driver blocks until one of the events or the timeout */
        leave();
        pause_ms(20);
        enter();
        status = current_status(ud);
        if (!(status & mask & ~TIMO)) status |= TIMO;
    }
    return report(status, 0, 0);
}

EXPORT int ibwait(int ud, int mask)
{
    enter();
    int status = wait_locked(ud, mask);
    leave();
    return status;
}

EXPORT int ibgts(int ud, int v) { (void)v; return ud == 0 ? done(CIC, 0, 0) : fail(EARG); }
EXPORT int ibcac(int ud, int v) { (void)v; return ud == 0 ? done(CIC, 0, 0) : fail(EARG); }

EXPORT int ibpct(int ud)
{
    enter();
    int valid = device_descriptor(ud) != NULL;
    leave();
    return valid ? done(0, 0, 0) : fail(EARG);
}

EXPORT int iblines(int ud, short *result)
{
    if (ud != 0) return fail(EARG);
    enter();
    *result = (short)(0x00FF | (ren_state ? 0x1000 : 0));
    leave();
    return done(CIC, 0, 0);
}

#ifndef FAKE_NO_ASYNC
static void finish(AsyncOp *op, int status, int error)
{
    op->state = OP_DONE;
    op->status = status;
    op->error = error;
}

/* Carries a running transfer forward (library thread, lock held). */
static void progress(int ud, long long now)
{
    AsyncOp *op = &ops[ud];
    Descriptor *d = device_descriptor(ud);
    Instrument *ins = d != NULL ? find_instrument(d->pad) : NULL;
    switch (op->kind)
    {
        case OP_COMMAND:
            op->got = op->cnt;
            finish(op, CIC, 0);
            return;
        case OP_WRITE:
            if (ins == NULL) finish(op, ERR, ENOL);
            else if (!write_bytes(d, ins, op->buf, op->cnt)) finish(op, ERR, EARG);
            else
            {
                op->got = op->cnt;
                finish(op, 0, 0);
            }
            return;
        default:
            if (ins == NULL)
            {
                finish(op, ERR, ENOL);
                return;
            }
            if (output_available(ins) > 0)
            {
                int end;
                op->got += read_bytes(d, ins, op->buf + op->got, op->cnt - op->got, &end);
                if (end || op->got == op->cnt)
                {
                    finish(op, end ? END : 0, 0);
                    return;
                }
            }
            if (op->deadline != 0 && now >= op->deadline) finish(op, ERR | TIMO, EABO);
            return;
    }
}

#ifndef FAKE_NO_NOTIFY
/* Invokes the callback if its events are true (library thread, lock held; released during the callback). */
static void dispatch(int ud)
{
    Notification *n = &notifications[ud];
    int status = current_status(ud);
    if ((status & n->mask) == 0) return;
    int error = 0, count = 0;
    AsyncOp *op = &ops[ud];
    if ((n->mask & CMPL) && op->state == OP_DONE)
    {
        if (drop_notifications > 0)
        {
            /* lose the notification: the transfer stays unsynchronized */
            drop_notifications--;
            n->mask = 0;
            return;
        }
        op->state = OP_IDLE; /* the callback resynchronizes the transfer */
        notify_resyncs++;
        status |= op->status & (ERR | TIMO | END);
        error = op->error;
        count = (int)op->got;
    }
    NotifyCallback callback = n->callback;
    void *ref = n->ref;
    unsigned generation = n->generation;
    n->mask = 0; /* one-shot until the callback rearms it */
    leave();
    report(status, error, count);
    in_callback = 1;
    int rearm = callback(ud, status, error, (long)count, ref);
    in_callback = 0;
    if (override_delay_ms > 0)
    {
        /* the worst case for callers: the library takes a while to return from the callback and then rearms with
           its return value, undoing an ibnotify another thread made meanwhile */
        pause_ms(override_delay_ms);
        enter();
        if (n->generation != generation && n->mask != rearm) overridden++;
        n->mask = rearm;
        return;
    }
    enter();
    if (n->generation == generation && rearm != 0) n->mask = rearm; /* unless ibnotify replaced it meanwhile */
}
#endif

/* The library-owned thread: carries out transfers and invokes ibnotify callbacks. */
static void engine_run(void)
{
    enter();
    for (;;)
    {
        int active = 0;
        long long now = now_ms();
        for (int ud = 0; ud < MAX_UD; ud++)
        {
            if (ops[ud].state != OP_RUNNING) continue;
            progress(ud, now);
            if (ops[ud].state == OP_RUNNING) active = 1;
            else signal_all(); /* wakes ibwait(CMPL) */
        }
#ifndef FAKE_NO_NOTIFY
        for (int ud = 0; ud < MAX_UD; ud++)
        {
            if (notifications[ud].mask == 0) continue;
            dispatch(ud);
            if (notifications[ud].mask != 0) active = 1;
        }
#endif
        wait_signal(active ? 1 : 1000);
    }
}

#ifdef _WIN32
static unsigned __stdcall engine_main(void *arg)
{
    (void)arg;
    engine_run();
    return 0;
}

static void start_engine(void)
{
    if (engine_started) return;
    uintptr_t thread = _beginthreadex(NULL, 0, engine_main, NULL, 0, NULL);
    if (thread == 0) return;
    CloseHandle((HANDLE)thread);
    engine_started = 1;
}
#else
static void *engine_main(void *arg)
{
    (void)arg;
    engine_run();
    return NULL;
}

static void start_engine(void)
{
    if (engine_started) return;
    pthread_t thread;
    if (pthread_create(&thread, NULL, engine_main, NULL) != 0) return;
    pthread_detach(thread);
    engine_started = 1;
}
#endif

static int start_async(int ud, int kind, const void *buf, long long cnt)
{
    enter();
    int status;
    if (kind == OP_COMMAND ? ud != 0 : device_descriptor(ud) == NULL) status = fail(EARG);
    else if (busy(ud)) status = fail(EOIP);
    else
    {
        AsyncOp *op = &ops[ud];
        int code = ud == 0 ? board_timeout_code : descriptors[ud].timeout_code;
        op->state = OP_RUNNING;
        op->kind = kind;
        op->buf = (unsigned char *)buf; /* used until the transfer finishes: the caller keeps it alive */
        op->cnt = (size_t)cnt;
        op->got = 0;
        op->deadline = timeout_ms(code) > 0 ? now_ms() + timeout_ms(code) : 0;
        start_engine();
        signal_all();
        status = report(current_status(ud), 0, 0); /* no CMPL: the transfer is in progress */
    }
    leave();
    return status;
}

EXPORT int ibrda(int ud, void *buf, long long cnt) { return start_async(ud, OP_READ, buf, cnt); }
EXPORT int ibwrta(int ud, const void *buf, long long cnt) { return start_async(ud, OP_WRITE, buf, cnt); }
EXPORT int ibcmda(int ud, const void *buf, long long cnt) { return start_async(ud, OP_COMMAND, buf, cnt); }

EXPORT int ibstop(int ud)
{
    enter();
    stop_calls++;
    int status;
    if (ud != 0 && device_descriptor(ud) == NULL) status = fail(EARG);
    else if (ops[ud].state == OP_RUNNING)
    {
        AsyncOp *op = &ops[ud];
        op->state = OP_IDLE; /* aborted and resynchronized */
        async_sta = ERR | CMPL;
        async_err = EABO;
        async_cnt = (int)op->got;
        status = report(current_status(ud) | ERR, EABO, (int)op->got);
        signal_all();
    }
    else if (ops[ud].state == OP_DONE) status = resync(ud);
    else status = done(0, 0, 0);
    leave();
    return status;
}

#ifdef FAKE_NO_NOTIFY
EXPORT int AsyncIbsta(void) { return async_sta; }
EXPORT int AsyncIberr(void) { return async_err; }
EXPORT int AsyncIbcnt(void) { return async_cnt; }
#else
EXPORT int ibnotify(int ud, int mask, NotifyCallback callback, void *ref)
{
    if (in_callback) return fail(ECAP); /* NI-488.2: callbacks rearm through their return value */
    enter();
    int status;
    int valid = ud == 0 ? (mask & (ERR | RQS)) == 0 : (mask & ~(CMPL | TIMO | END | RQS)) == 0;
    if (ud != 0 && device_descriptor(ud) == NULL) status = fail(EARG);
    else if (notify_fails) status = fail(ECAP);
    else if (!valid || (mask != 0 && callback == NULL)) status = fail(EARG);
    else
    {
        Notification *n = &notifications[ud];
        n->mask = mask;
        n->callback = callback;
        n->ref = ref;
        n->generation++;
        if (mask != 0)
        {
            start_engine();
            signal_all();
        }
        status = report(current_status(ud), 0, 0);
    }
    leave();
    return status;
}
#endif
#endif

/*
 * Test hooks: 1 = make ibnotify fail with ECAP while value is non-zero, 2 = lose the next <value> completion
 * notifications, 3 = number of ibstop calls, 4 = transfers not yet resynchronized, 5 = transfers resynchronized
 * by an ibnotify callback, 6 = transfers resynchronized by ibwait, 7 = make ibclr, ibtrg and ibrsp take <value> ms,
 * 8 = return from callbacks <value> ms late and then rearm with their return value even if ibnotify was called
 * meanwhile (0: keep a newer ibnotify), 9 = number of ibnotify armings undone that way, 10 = hide SRQI from the
 * board while non-zero (automatic serial polling), 11 = number of ibrsp calls.
 * Options 1, 2, 7, 8 and 10 return the previous setting.
 */
EXPORT int fake_ni4882_control(int option, int value)
{
    enter();
    int result = 0;
    switch (option)
    {
        case 1: result = notify_fails; notify_fails = value; break;
        case 2: result = drop_notifications; drop_notifications = value; break;
        case 3: result = stop_calls; break;
        case 4:
            for (int ud = 0; ud < MAX_UD; ud++)
                if (ops[ud].state != OP_IDLE) result++;
            break;
        case 5: result = notify_resyncs; break;
        case 6: result = wait_resyncs; break;
        case 7: result = call_delay_ms; call_delay_ms = value; break;
        case 8: result = override_delay_ms; override_delay_ms = value; break;
        case 9: result = overridden; break;
        case 10: result = autopoll_hides_srqi; autopoll_hides_srqi = value; break;
        case 11: result = serial_polls; break;
        default: result = -1; break;
    }
    leave();
    return result;
}

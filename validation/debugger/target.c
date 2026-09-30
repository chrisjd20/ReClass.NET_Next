#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#ifdef _WIN32
#include <windows.h>
#else
#include <pthread.h>
#include <stdatomic.h>
#include <unistd.h>
#include <sys/prctl.h>
#endif

extern void fixture_modify(volatile int32_t *object);
extern void fixture_hook(volatile int32_t *object);
extern int32_t fixture_bias;
static volatile int32_t objects[2] = {100, 200};
#ifdef _WIN32
static volatile LONG run_worker;
static HANDLE worker;
static DWORD WINAPI worker_main(void *arg)
#else
static atomic_int run_worker;
static pthread_t worker;
static int worker_exists;
static void *worker_main(void *arg)
#endif
{
    (void)arg;
#ifdef _WIN32
    while (InterlockedCompareExchange(&run_worker, 0, 0))
#else
    while (atomic_load(&run_worker))
#endif
    {
        fixture_modify(&objects[0]);
        fixture_modify(&objects[1]);
#ifdef _WIN32
        Sleep(100);
#else
        usleep(100000);
#endif
    }
    return 0;
}

static void stop_worker(void)
{
#ifdef _WIN32
    InterlockedExchange(&run_worker, 0);
    if (worker) { WaitForSingleObject(worker, INFINITE); CloseHandle(worker); worker = 0; }
#else
    atomic_store(&run_worker, 0);
    if (worker_exists) { pthread_join(worker, 0); worker_exists = 0; }
#endif
}

int main(void)
{
    char command[128];
    setvbuf(stdout, 0, _IONBF, 0);
#ifndef _WIN32
    /* Controlled fixture explicitly permits a sibling debugger in the check container.
       This changes this fixture's policy only; the application never changes host policy. */
    prctl(PR_SET_PTRACER, PR_SET_PTRACER_ANY, 0, 0, 0);
#endif
    printf("pid=%lu object1=%p object2=%p modify=%p hook=%p bias=%p\n",
#ifdef _WIN32
        (unsigned long)GetCurrentProcessId(),
#else
        (unsigned long)getpid(),
#endif
        (void *)&objects[0], (void *)&objects[1], (void *)fixture_modify, (void *)fixture_hook, (void *)&fixture_bias);
    puts("commands: modify, hook, thread, stop, values, reset, exit");
    while (fgets(command, sizeof command, stdin))
    {
        command[strcspn(command, "\r\n")] = 0;
        if (!strcmp(command, "exit")) break;
        if (!strcmp(command, "modify")) { fixture_modify(&objects[0]); fixture_modify(&objects[1]); }
        else if (!strcmp(command, "hook")) { fixture_hook(&objects[0]); fixture_hook(&objects[1]); }
        else if (!strcmp(command, "stop")) stop_worker();
        else if (!strcmp(command, "reset")) { stop_worker(); objects[0] = 100; objects[1] = 200; fixture_bias = 3; }
        else if (!strcmp(command, "thread"))
        {
            stop_worker();
#ifdef _WIN32
            InterlockedExchange(&run_worker, 1);
            worker = CreateThread(0, 0, worker_main, 0, 0, 0);
            if (!worker) { InterlockedExchange(&run_worker, 0); fprintf(stderr, "CreateThread failed: %lu\n", (unsigned long)GetLastError()); }
#else
            atomic_store(&run_worker, 1);
            int error = pthread_create(&worker, 0, worker_main, 0);
            if (error) { atomic_store(&run_worker, 0); fprintf(stderr, "pthread_create failed: %d\n", error); }
            else worker_exists = 1;
#endif
        }
        else if (strcmp(command, "values")) puts("unknown command");
        printf("values=%d,%d bias=%d\n", objects[0], objects[1], fixture_bias);
    }
    stop_worker();
    return 0;
}

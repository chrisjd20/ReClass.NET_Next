#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>

/* Mono tracks only this launcher as its Process.Start child. The fixture remains
   a descendant with inherited stdio, and its ptrace stops belong to the debugger. */
int main(int argc, char **argv)
{
    if (argc != 2) { fprintf(stderr, "usage: debugger-launcher <fixture-path>\n"); return 2; }
    pid_t fixture = fork();
    if (fixture < 0) { perror("fixture fork"); return 1; }
    if (fixture == 0)
    {
        execl(argv[1], argv[1], (char *)0);
        perror("fixture exec");
        _exit(127);
    }
    for (;;)
    {
        int status;
        pid_t result = waitpid(fixture, &status, 0);
        if (result < 0)
        {
            if (errno == EINTR) continue;
            perror("fixture wait"); return 1;
        }
        if (WIFEXITED(status)) return WEXITSTATUS(status);
        if (WIFSIGNALED(status))
        {
            fprintf(stderr, "fixture terminated by signal %d\n", WTERMSIG(status));
            return 128 + WTERMSIG(status);
        }
        /* Never mistake an intermediate stop for process exit. */
    }
}

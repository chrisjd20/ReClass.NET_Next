#include <errno.h>
#include <stdio.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>

/* Mono owns only this launcher. Native ReClass owns ptrace events for the
   grandchild. Arguments after the executable are passed through unchanged. */
int main(int argc, char **argv)
{
    if (argc < 2) { fprintf(stderr, "usage: breakout-launcher <game> [arguments...]\n"); return 2; }
    pid_t game = fork();
    if (game < 0) { perror("game fork"); return 1; }
    if (game == 0) { execv(argv[1], argv + 1); perror("game exec"); _exit(127); }
    for (;;) {
        int status;
        pid_t result = waitpid(game, &status, 0);
        if (result < 0) { if (errno == EINTR) continue; perror("game wait"); return 1; }
        if (WIFEXITED(status)) return WEXITSTATUS(status);
        if (WIFSIGNALED(status)) return 128 + WTERMSIG(status);
    }
}

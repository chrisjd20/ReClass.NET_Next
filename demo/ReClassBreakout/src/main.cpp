#include "frontend.h"
#include "game.h"
#include <cstdlib>
#include <cstring>
#include <iostream>
#ifdef __linux__
#include <cerrno>
#include <sys/prctl.h>
#endif

int main(int argc, char** argv) {
    int room = 0;
    bool check = false;
    for (int i = 1; i < argc; ++i) {
        if (std::strcmp(argv[i], "--self-check") == 0) check = true;
        else if (std::strcmp(argv[i], "--room") == 0 && i + 1 < argc) {
            char* end = nullptr;
            const long parsed = std::strtol(argv[++i], &end, 10);
            if (!end || *end || parsed < 1 || parsed > 12) {
                std::cerr << "--room requires a room number from 1 to 12.\n"; return 2;
            }
            room = static_cast<int>(parsed);
        } else if (std::strcmp(argv[i], "--help") == 0 || std::strcmp(argv[i], "-h") == 0) {
            std::cout << "ReClass: Breakout\nUsage: ReClassBreakout [--room 1..12] [--self-check]\n"
                         "F1..F12 select rooms; Ctrl+P pauses; Ctrl+R resets gameplay data.\n";
            return 0;
        } else { std::cerr << "Unknown or incomplete option: " << argv[i] << '\n'; return 2; }
    }
    if (check) {
        std::string report;
        const bool good = breakout::selfCheck(report);
        std::cout << report << '\n';
        return good ? 0 : 1;
    }
    std::string diagnostic;
#ifdef __linux__
    // This deliberately inspectable tutorial opts its own process into same-user
    // sibling attachment. Never modify kernel/host ptrace policy.
    if (prctl(PR_SET_PTRACER, PR_SET_PTRACER_ANY, 0, 0, 0) != 0)
        diagnostic = std::string("Sibling debugging permission failed: ") + std::strerror(errno) +
                     ". Attach with your system's normal debugging permissions.";
    else diagnostic = "This process allows same-user sibling debugging (PR_SET_PTRACER_ANY).";
#else
    diagnostic = "Attach the matching x64 ReClass build to this process.";
#endif
    return breakout::runFrontend(room, diagnostic);
}

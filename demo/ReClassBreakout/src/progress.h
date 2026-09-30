#pragma once
#include <array>
#include <string>

namespace breakout {
struct Progress {
    std::array<bool, 12> completed{};
    std::array<int, 12> steps{};
    int room = 1;
    int textSize = 16;
    bool addresses = false;
    bool hex = false;
    bool hints = false;
    bool solutions = false;
    bool readout = false;
    bool drawer = true;
    bool crt = true;
    std::string path;
    std::string diagnostic;
    void load();
    bool save();
};
}

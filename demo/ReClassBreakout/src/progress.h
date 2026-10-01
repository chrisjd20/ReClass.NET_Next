#pragma once
#include <array>
#include <string>

namespace breakout {
struct Progress {
    static constexpr int Rooms = 14;
    std::array<bool, Rooms> completed{};
    std::array<int, Rooms> steps{};
    int room = 1;
    int textSize = 16;
    bool addresses = false;
    bool hex = false;
    bool readout = false;
    bool drawer = true;
    bool crt = true;
    std::string path;
    std::string diagnostic;
    void load();
    bool save();
};
}

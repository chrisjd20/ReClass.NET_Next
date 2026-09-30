#pragma once
// Pixel-art sprites generated at startup from the ASCII tables in sprites.cpp.
// No image assets ship with the demo.
#include "raylib.h"

namespace breakout {
struct Sprites {
    Texture2D player[2]{}, drone[2]{}, sentinel{}, turret{}, floor[2]{}, wall{}, hazard{};
    void load();
    void unload();
};
}

#include "sprites.h"
#include <initializer_list>

namespace breakout {
namespace {
const char* const player0Rows[16] = {
    "................",
    "....kkkkkkkk....",
    "...kttttttttk...",
    "..kttwttttttTk..",
    "..ktkkkkkkkktk..",
    "..ktkbbkkbbktk..",
    "..ktkkkkkkkktk..",
    "..kTttttttttTk..",
    "...kkkkkkkkkk...",
    "..kgGggggggGgk..",
    ".kgkgyyggyygkgk.",
    ".kgkggggggggkgk.",
    "..k.kGGGGGGk.k..",
    "....kgk..kgk....",
    "....kGk..kGk....",
    "...kkkk..kkkk...",
};
const char* const player1Rows[16] = {
    "................",
    "....kkkkkkkk....",
    "...kttttttttk...",
    "..kttwttttttTk..",
    "..ktkkkkkkkktk..",
    "..ktkbbkkbbktk..",
    "..ktkkkkkkkktk..",
    "..kTttttttttTk..",
    "...kkkkkkkkkk...",
    "..kgGggggggGgk..",
    ".kgkgyyggyygkgk.",
    ".kgkggggggggkgk.",
    "..k.kGGGGGGk.k..",
    "...kgk....kgk...",
    "...kGk....kGk...",
    "..kkkk....kkkk..",
};
const char* const drone0Rows[16] = {
    "................",
    ".gggg......gggg.",
    "...k........k...",
    "...k.kkkkkk.k...",
    "...kkyyyyyykk...",
    "....kyYYYYyk....",
    "....kYkrrkYk....",
    "....kYrwrrYk....",
    "....kYkrrkYk....",
    "....kyYYYYyk....",
    "...kkyyyyyykk...",
    "...k.kkkkkk.k...",
    "...k........k...",
    ".gggg......gggg.",
    "................",
    "................",
};
const char* const drone1Rows[16] = {
    "................",
    "..gg........gg..",
    "...k........k...",
    "...k.kkkkkk.k...",
    "...kkyyyyyykk...",
    "....kyYYYYyk....",
    "....kYkrrkYk....",
    "....kYrrwrYk....",
    "....kYkrrkYk....",
    "....kyYYYYyk....",
    "...kkyyyyyykk...",
    "...k.kkkkkk.k...",
    "...k........k...",
    "..gg........gg..",
    "................",
    "................",
};
const char* const sentinelRows[16] = {
    "................",
    "...kkkkkkkkkk...",
    "..kRrrrrrrrrRk..",
    "..krkkkkkkkkrk..",
    "..krkyykkyykrk..",
    "..krkkkkkkkkrk..",
    "..kRrrrrrrrrRk..",
    ".kkkkkkkkkkkkkk.",
    "kgGkRrrrrrrRkGgk",
    "kgGkrRRRRRRrkGgk",
    "kgGkRrrrrrrRkGgk",
    ".kkkkkkkkkkkkkk.",
    "...kRRk..kRRk...",
    "...krRk..kRrk...",
    "..kkkkk..kkkkk..",
    "................",
};
const char* const turretRows[16] = {
    "................",
    "......kkkk......",
    "......kggk......",
    "......kggk......",
    "....kkkggkkk....",
    "...kGGgggggGk...",
    "..kGgrrrrrrgGk..",
    "..kGgrwwrrrgGk..",
    "..kGgrrrrrrgGk..",
    "..kGgrrrrrrgGk..",
    "...kGGgggggGk...",
    "..kkkkkkkkkkkk..",
    ".kGGGGGGGGGGGGk.",
    ".kgggggggggggggk",
    ".kkkkkkkkkkkkkk.",
    "................",
};
Color paletteColor(char key) {
    switch (key) {
    case 'k': return {10, 14, 22, 255}; case 't': return {72, 219, 181, 255}; case 'T': return {34, 132, 112, 255};
    case 'w': return {236, 242, 250, 255}; case 'b': return {111, 169, 248, 255}; case 'g': return {98, 114, 136, 255};
    case 'G': return {58, 70, 88, 255}; case 'r': return {236, 92, 98, 255}; case 'R': return {150, 44, 56, 255};
    case 'y': return {249, 191, 87, 255}; case 'Y': return {170, 118, 40, 255}; case 'v': return {176, 132, 255, 255};
    case 'c': return {140, 240, 255, 255}; case 'o': return {255, 140, 60, 255};
    default: return {0, 0, 0, 0};
    }
}
Texture2D fromRows(const char* const rows[16]) {
    Image image = GenImageColor(16, 16, Color{0, 0, 0, 0});
    for (int y = 0; y < 16; ++y)
        for (int x = 0; x < 16 && rows[y][x]; ++x) ImageDrawPixel(&image, x, y, paletteColor(rows[y][x]));
    Texture2D texture = LoadTextureFromImage(image);
    UnloadImage(image);
    SetTextureFilter(texture, TEXTURE_FILTER_POINT);
    return texture;
}
// Deterministic hash noise keeps the generated tiles identical on every run.
unsigned noise(int x, int y, unsigned seed) {
    unsigned h = static_cast<unsigned>(x) * 374761393u + static_cast<unsigned>(y) * 668265263u + seed * 2246822519u;
    h = (h ^ (h >> 13)) * 1274126177u;
    return h ^ (h >> 16);
}
Texture2D floorTile(unsigned seed) {
    Image image = GenImageColor(32, 32, Color{22, 31, 44, 255});
    for (int y = 0; y < 32; ++y)
        for (int x = 0; x < 32; ++x) {
            const int shade = static_cast<int>(noise(x, y, seed) % 7) - 3;
            ImageDrawPixel(&image, x, y, Color{static_cast<unsigned char>(22 + shade), static_cast<unsigned char>(31 + shade), static_cast<unsigned char>(44 + shade), 255});
        }
    ImageDrawRectangleLines(&image, {0, 0, 32, 32}, 1, Color{31, 44, 60, 255});
    ImageDrawLine(&image, 1, 1, 30, 1, Color{36, 50, 68, 255});
    for (int corner = 0; corner < 4; ++corner)
        ImageDrawPixel(&image, corner % 2 ? 28 : 3, corner / 2 ? 28 : 3, Color{50, 64, 84, 255});
    if (seed % 2) ImageDrawRectangle(&image, 10, 14, 12, 4, Color{27, 38, 53, 255});
    Texture2D texture = LoadTextureFromImage(image);
    UnloadImage(image);
    SetTextureFilter(texture, TEXTURE_FILTER_POINT);
    return texture;
}
Texture2D wallTile() {
    Image image = GenImageColor(32, 32, Color{34, 44, 60, 255});
    ImageDrawRectangle(&image, 0, 0, 32, 6, Color{58, 72, 94, 255});
    ImageDrawRectangle(&image, 0, 26, 32, 6, Color{20, 27, 38, 255});
    for (int x = 0; x < 32; x += 8) ImageDrawRectangle(&image, x, 10, 5, 12, Color{27, 36, 50, 255});
    ImageDrawPixel(&image, 4, 3, Color{111, 169, 248, 255});
    Texture2D texture = LoadTextureFromImage(image);
    UnloadImage(image);
    SetTextureFilter(texture, TEXTURE_FILTER_POINT);
    return texture;
}
Texture2D hazardTile() {
    Image image = GenImageColor(32, 32, Color{0, 0, 0, 0});
    for (int y = 0; y < 32; ++y)
        for (int x = 0; x < 32; ++x)
            if (((x + y) / 8) % 2 == 0) ImageDrawPixel(&image, x, y, Color{249, 191, 87, 255});
            else ImageDrawPixel(&image, x, y, Color{24, 24, 28, 255});
    Texture2D texture = LoadTextureFromImage(image);
    UnloadImage(image);
    SetTextureFilter(texture, TEXTURE_FILTER_POINT);
    return texture;
}
}

void Sprites::load() {
    player[0] = fromRows(player0Rows); player[1] = fromRows(player1Rows);
    drone[0] = fromRows(drone0Rows); drone[1] = fromRows(drone1Rows);
    sentinel = fromRows(sentinelRows);
    turret = fromRows(turretRows);
    floor[0] = floorTile(1); floor[1] = floorTile(2); wall = wallTile(); hazard = hazardTile();
}
void Sprites::unload() {
    for (auto texture : {player[0], player[1], drone[0], drone[1], sentinel, turret, floor[0], floor[1], wall, hazard})
        if (texture.id) UnloadTexture(texture);
}
}

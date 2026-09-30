#pragma once
// Immediate-mode drawing helpers shared by the frontend and the scene renderer.
#include "raylib.h"
#include <string>
#include <vector>

namespace breakout::ui {
inline const Color Background{8, 12, 20, 255}, Panel{17, 25, 38, 255}, Inset{12, 19, 29, 255};
inline const Color Border{43, 58, 77, 255}, Ink{236, 242, 250, 255}, Muted{150, 168, 192, 255};
inline const Color Teal{72, 219, 181, 255}, Gold{249, 191, 87, 255}, Red{248, 111, 117, 255};
inline const Color Blue{111, 169, 248, 255}, Violet{176, 132, 255, 255};
enum class Face { Body, Bold, Mono, Heading };

extern float scale;
void loadFonts();
void unloadFonts();
Vector2 mouse();
void scissor(Rectangle rect);
std::string displayText(std::string value);
float textWidth(const std::string& value, float size, Face face = Face::Body);
void text(const std::string& value, float x, float y, float size, Color color = Ink, Face face = Face::Body);
void heading(const std::string& value, float x, float y, float size, Color color = Ink);
std::vector<std::string> wrap(const std::string& value, float width, float size, Face face = Face::Body);
float paragraph(const std::string& value, float x, float y, float width, float size, Color color = Ink, Face face = Face::Body);
void panel(Rectangle rect, Color fill = Panel);

struct Ui {
    bool blocked = false, hot = false;
    Rectangle clip{0, 0, 100000, 100000};
    std::string tooltip;
    bool hovered(Rectangle rect) const;
    bool button(Rectangle rect, const std::string& label, float size,
                bool selected = false, const std::string& tip = {}, bool enabled = true);
    // Returns the newly selected tab, or -1.
    int tabs(Rectangle rect, const std::vector<std::string>& labels, int selected, float size);
};
struct Scroll {
    float offset = 0, content = 0;
    void begin(Rectangle rect, bool blocked);
    void end(Rectangle rect, float endY);
};
} // namespace breakout::ui

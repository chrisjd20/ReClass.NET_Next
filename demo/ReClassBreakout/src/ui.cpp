#include "ui.h"
#include "selection.h"
#include "fonts.generated.h"
#include <algorithm>
#include <cmath>

namespace breakout::ui {
namespace {
Font bodyFont{}, boldFont{}, monoFont{};
Font typeface(Face face) {
    if (face == Face::Heading) return GetFontDefault();
    if (face == Face::Bold) return boldFont;
    if (face == Face::Mono) return monoFont;
    return bodyFont;
}
float spacing(Face face) { return face == Face::Heading ? 1.0f : .15f; }
Font loadTypeface(const unsigned char* bytes, int count) {
    auto font = LoadFontFromMemory(".ttf", bytes, count, 64, nullptr, 0);
    if (font.texture.id != GetFontDefault().texture.id) SetTextureFilter(font.texture, TEXTURE_FILTER_BILINEAR);
    return font;
}
}

float scale = 1;
void loadFonts() {
    bodyFont = loadTypeface(fonts::Body, static_cast<int>(sizeof(fonts::Body)));
    boldFont = loadTypeface(fonts::Bold, static_cast<int>(sizeof(fonts::Bold)));
    monoFont = loadTypeface(fonts::Mono, static_cast<int>(sizeof(fonts::Mono)));
}
void unloadFonts() {
    for (const auto& font : {bodyFont, boldFont, monoFont})
        if (font.texture.id != GetFontDefault().texture.id) UnloadFont(font);
}
Vector2 mouse() { const auto p = GetMousePosition(); return {p.x / scale, p.y / scale}; }
void scissor(Rectangle rect) {
    // Camera zoom does not transform raylib's scissor rectangle.
    BeginScissorMode(static_cast<int>(std::floor(rect.x * scale)), static_cast<int>(std::floor(rect.y * scale)),
                     static_cast<int>(std::ceil(rect.width * scale)), static_cast<int>(std::ceil(rect.height * scale)));
}
// Keep text consistent with the retained bitmap headings and ASCII font atlas.
// Normalize display only; clipboard instructions retain the original lesson text.
std::string displayText(std::string value) {
    static const std::vector<std::pair<std::string, std::string>> replacements{{"→", "->"}, {"←", "<-"}, {"’", "'"}, {"×", "x"}, {"…", "..."}, {"–", "-"}, {"—", "-"}, {"“", "\""}, {"”", "\""}};
    for (const auto& replacement : replacements) {
        std::size_t at = 0;
        while ((at = value.find(replacement.first, at)) != std::string::npos) {
            value.replace(at, replacement.first.size(), replacement.second);
            at += replacement.second.size();
        }
    }
    return value;
}
float textWidth(const std::string& value, float size, Face face) {
    return MeasureTextEx(typeface(face), displayText(value).c_str(), size, spacing(face)).x;
}
void text(const std::string& value, float x, float y, float size, Color color, Face face) {
    DrawTextEx(typeface(face), displayText(value).c_str(), {x, y}, size, spacing(face), color);
}
void heading(const std::string& value, float x, float y, float size, Color color) {
    text(value, x, y, size, color, Face::Heading);
}
std::vector<std::string> wrap(const std::string& value, float width, float size, Face face) {
    std::vector<std::string> result;
    std::string line, word;
    const auto putWord = [&]() {
        if (word.empty()) return;
        if (!line.empty()) {
            if (textWidth(line + ' ' + word, size, face) > width) { result.push_back(line); line.clear(); }
            else line += ' ';
        }
        for (char ch : word) {
            if (textWidth(line + ch, size, face) > width && !line.empty()) { result.push_back(line); line.clear(); }
            line += ch;
        }
        word.clear();
    };
    for (char ch : displayText(value)) {
        if (ch == ' ' || ch == '\t') putWord();
        else if (ch == '\n') { putWord(); result.push_back(line); line.clear(); }
        else word += ch;
    }
    putWord();
    if (!line.empty() || result.empty()) result.push_back(line);
    return result;
}
float paragraph(const std::string& value, float x, float y, float width, float size, Color color, Face face) {
    const auto lines = wrap(value, std::max(25.0f, width), size, face);
    const float lineHeight = size * 1.48f;
    if (recorder) {
        const auto joins = lineJoins(displayText(value), lines);
        for (std::size_t i = 0; i < lines.size(); ++i) {
            selectableText(lines[i], x, y, size, color, face, y - size * .24f, lineHeight, joins[i]);
            y += lineHeight;
        }
        return y;
    }
    for (const auto& line : lines) { text(line, x, y, size, color, face); y += lineHeight; }
    return y;
}
void panel(Rectangle rect, Color fill) {
    DrawRectangleRounded({rect.x, rect.y + 4, rect.width, rect.height}, .035f, 8, Color{3, 6, 12, 140});
    DrawRectangleRounded(rect, .025f, 4, fill);
    DrawRectangleRoundedLinesEx(rect, .025f, 4, 1, Border);
    DrawLineEx({rect.x + 14, rect.y + 1}, {rect.x + rect.width - 14, rect.y + 1}, 1, Color{67, 87, 109, 150});
}

bool Ui::hovered(Rectangle rect) const {
    return !blocked && CheckCollisionPointRec(mouse(), rect) && CheckCollisionPointRec(mouse(), clip);
}
bool Ui::button(Rectangle rect, const std::string& label, float size, bool selected, const std::string& tip, bool enabled) {
    const bool hover = hovered(rect);
    if (hover) hot = true;
    const Color fill = selected ? Color{35, 88, 81, 255} : hover && enabled ? Color{49, 66, 87, 255} : Color{31, 44, 61, 255};
    DrawRectangleRounded(rect, .16f, 4, fill);
    DrawRectangleRoundedLinesEx(rect, .16f, 4, 1, selected ? Teal : Border);
    const float fitted = std::min(size, (rect.width - 12) / std::max(1.0f, textWidth(label, size, Face::Bold)) * size);
    text(label, rect.x + (rect.width - textWidth(label, fitted, Face::Bold)) * .5f,
         rect.y + (rect.height - fitted) * .5f, fitted, enabled ? selected ? Teal : Ink : Muted, Face::Bold);
    if (hover && !tip.empty()) tooltip = tip;
    return hover && enabled && clicked();
}
int Ui::tabs(Rectangle rect, const std::vector<std::string>& labels, int selected, float size) {
    int result = -1;
    const float width = rect.width / std::max<std::size_t>(1, labels.size());
    for (std::size_t i = 0; i < labels.size(); ++i) {
        const Rectangle tab{rect.x + width * static_cast<float>(i), rect.y, width, rect.height};
        const bool active = static_cast<int>(i) == selected, hover = hovered(tab);
        if (hover) hot = true;
        const auto& label = labels[i];
        text(label, tab.x + (tab.width - textWidth(label, size, Face::Bold)) * .5f, tab.y + (tab.height - size) * .5f - 1,
             size, active ? Teal : hover ? Ink : Muted, Face::Bold);
        DrawRectangleRec({tab.x + 6, tab.y + tab.height - 3, tab.width - 12, 3}, active ? Teal : hover ? Border : Color{0, 0, 0, 0});
        if (hover && !active && clicked()) result = static_cast<int>(i);
    }
    DrawLineEx({rect.x, rect.y + rect.height}, {rect.x + rect.width, rect.y + rect.height}, 1, Border);
    return result;
}

void Scroll::begin(Rectangle rect, bool blocked) {
    const float maximum = std::max(0.0f, content - rect.height + 12);
    if (!blocked && CheckCollisionPointRec(mouse(), rect)) offset -= GetMouseWheelMove() * 48;
    offset = std::clamp(offset, 0.0f, maximum);
    scissor(rect);
}
void Scroll::end(Rectangle rect, float endY) {
    content = endY + offset - rect.y;
    EndScissorMode();
    if (content > rect.height) {
        const float track = rect.height - 8;
        const float thumb = std::max(25.0f, track * rect.height / content);
        const float top = rect.y + 4 + (track - thumb) * offset / std::max(1.0f, content - rect.height + 12);
        DrawRectangleRounded({rect.x + rect.width - 5, rect.y + 4, 3, track}, .4f, 4, Border);
        DrawRectangleRounded({rect.x + rect.width - 5, top, 3, thumb}, .4f, 4, Muted);
    }
}
} // namespace breakout::ui

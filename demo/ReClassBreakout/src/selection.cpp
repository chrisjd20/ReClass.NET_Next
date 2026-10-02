#include "selection.h"
#include <algorithm>
#include <cctype>
#include <cmath>
#include <limits>
#include <utility>

namespace breakout::ui {
Selection* recorder = nullptr;
bool releaseConsumed = false;

bool clicked() { return IsMouseButtonReleased(MOUSE_BUTTON_LEFT) && !releaseConsumed; }

namespace {
// The run of non-space characters around index: one word or address. Chips (bold labels) count as one word.
std::pair<int, int> wordAround(const std::string& text, int index, Face face) {
    const int length = static_cast<int>(text.size());
    if (face == Face::Bold) return {0, length};
    int from = std::clamp(index, 0, length), to = from;
    if (from > 0 && (from == length || text[static_cast<std::size_t>(from)] == ' ')) --from;
    while (from > 0 && text[static_cast<std::size_t>(from - 1)] != ' ') --from;
    while (to < length && text[static_cast<std::size_t>(to)] != ' ') ++to;
    while (from < to && text[static_cast<std::size_t>(from)] == ' ') ++from;
    return {from, std::max(from, to)};
}
bool before(int pieceA, int indexA, int pieceB, int indexB) { return pieceA < pieceB || (pieceA == pieceB && indexA < indexB); }
float prefix(const std::string& text, int count, float size, Face face) {
    return count <= 0 ? 0.0f : textWidth(text.substr(0, static_cast<std::size_t>(count)), size, face);
}
}

// Last frame's pieces moved by however far the region has scrolled since they were drawn.
float Selection::shift() const { return piecesOffset - liveOffset; }

void Selection::input(Rectangle region, float& offset, bool enabled) {
    area = region;
    liveOffset = offset;
    if (!enabled) { pressing = false; moved = false; hasLastPoint = false; return; }
    const Vector2 point = mouse();
    // A quick drag can move the pointer in the same frame as the press; where it rested a frame ago is
    // closer to where the button went down.
    const Vector2 pressPoint = hasLastPoint ? lastPoint : point;
    lastPoint = point; hasLastPoint = true;
    // A little slack at the sides: some panes start their text right at the edge.
    const Rectangle reach{area.x - 8, area.y, area.width + 16, area.height};
    const bool inside = CheckCollisionPointRec(pressPoint, reach) || CheckCollisionPointRec(point, reach);
    if (IsMouseButtonPressed(MOUSE_BUTTON_LEFT) && inside) {
        const Caret caret = hit(pressPoint);
        const bool doubleClick = GetTime() - lastClick < .35 && std::fabs(point.x - lastClickAt.x) < 4 && std::fabs(point.y - lastClickAt.y) < 4;
        lastClick = GetTime(); lastClickAt = point;
        if (doubleClick && caret.piece >= 0) {
            // Double-click selects one word, address or chip.
            const auto& piece = pieces[static_cast<std::size_t>(caret.piece)];
            const auto word = wordAround(piece.text, caret.index, piece.face);
            anchor = {caret.piece, word.first};
            focus = {caret.piece, word.second};
            has = true; pressing = false; moved = false;
            return;
        }
        pressing = true; moved = false; pressAt = pressPoint; pressCaret = caret;
    }
    if (pressing && IsMouseButtonDown(MOUSE_BUTTON_LEFT)) {
        if (!moved && (std::fabs(point.x - pressAt.x) > 4 || std::fabs(point.y - pressAt.y) > 4)) {
            moved = true; anchor = pressCaret; has = anchor.piece >= 0;
        }
        if (moved) {
            // Past the edge, scroll towards the pointer; faster the further out it is.
            if (point.y < area.y + 16) offset = std::max(0.0f, offset - std::min(40.0f, (area.y + 16 - point.y) * .5f + 2));
            else if (point.y > area.y + area.height - 16) offset += std::min(40.0f, (point.y - area.y - area.height + 16) * .5f + 2);
            liveOffset = offset;
            const Vector2 clamped{std::clamp(point.x, area.x, area.x + area.width), std::clamp(point.y, area.y + 1, area.y + area.height - 1)};
            const Caret caret = hit(clamped);
            if (caret.piece >= 0) { focus = caret; if (anchor.piece < 0) anchor = caret; has = true; }
        }
    }
    if (pressing && IsMouseButtonReleased(MOUSE_BUTTON_LEFT)) {
        // A plain click clears the selection; a drag keeps it.
        if (!moved) has = false;
        pressing = false; moved = false;
    }
}

void Selection::record(float offset) {
    building.clear();
    buildingOffset = offset;
    recorder = this;
}

void Selection::finish() {
    if (recorder == this) recorder = nullptr;
    pieces.swap(building);
    piecesOffset = buildingOffset;
}

void Selection::add(const std::string& display, float x, float top, float height, float size, Face face, Join join) {
    const int index = static_cast<int>(building.size());
    if (!empty()) {
        Caret start = anchor, end = focus;
        if (before(end.piece, end.index, start.piece, start.index)) std::swap(start, end);
        if (index >= start.piece && index <= end.piece) {
            const int length = static_cast<int>(display.size());
            const int from = index == start.piece ? std::min(start.index, length) : 0;
            const int to = index == end.piece ? std::min(end.index, length) : length;
            float left = x + prefix(display, from, size, face), right = x + prefix(display, to, size, face);
            // Cover the space before a piece that continues the same line, so a selection reads as one band.
            if (index > start.piece && join == Join::Space && !building.empty() && std::fabs(building.back().top - top) < 1)
                left = std::min(left, building.back().x + textWidth(building.back().text, building.back().size, building.back().face));
            if (right > left) DrawRectangleRec({left, top, right - left, height}, Fade(Blue, .38f));
        }
    }
    building.push_back({display, x, top, height, size, face, join});
}

Selection::Caret Selection::hit(Vector2 point) const {
    if (pieces.empty()) return {};
    const float dy = shift();
    // The row under the point, or the nearest row above or below it.
    int best = -1; float bestDistance = std::numeric_limits<float>::max();
    for (std::size_t i = 0; i < pieces.size(); ++i) {
        const auto& p = pieces[i];
        const float top = p.top + dy, bottom = top + p.height;
        const float distance = point.y < top ? top - point.y : point.y > bottom ? point.y - bottom : 0;
        if (distance < bestDistance) { bestDistance = distance; best = static_cast<int>(i); }
    }
    const float rowTop = pieces[static_cast<std::size_t>(best)].top;
    // Within that row, the piece under or nearest to the point, then the nearest character edge.
    int chosen = -1; float chosenDistance = std::numeric_limits<float>::max();
    for (std::size_t i = 0; i < pieces.size(); ++i) {
        const auto& p = pieces[i];
        if (std::fabs(p.top - rowTop) > 1) continue;
        const float width = textWidth(p.text, p.size, p.face);
        const float distance = point.x < p.x ? p.x - point.x : point.x > p.x + width ? point.x - p.x - width : 0;
        if (distance < chosenDistance) { chosenDistance = distance; chosen = static_cast<int>(i); }
    }
    const auto& p = pieces[static_cast<std::size_t>(chosen)];
    int index = 0; float nearest = std::numeric_limits<float>::max();
    for (int n = 0; n <= static_cast<int>(p.text.size()); ++n) {
        const float edge = p.x + prefix(p.text, n, p.size, p.face);
        if (std::fabs(edge - point.x) < nearest) { nearest = std::fabs(edge - point.x); index = n; }
    }
    return {chosen, index};
}

bool Selection::over(Vector2 point) const {
    if (!CheckCollisionPointRec(point, area)) return false;
    for (const auto& p : pieces) {
        if (point.y >= p.top && point.y <= p.top + p.height && point.x >= p.x && point.x <= p.x + textWidth(p.text, p.size, p.face)) return true;
    }
    return false;
}

std::string Selection::copied() const {
    if (empty() || pieces.empty()) return {};
    Caret start = anchor, end = focus;
    if (before(end.piece, end.index, start.piece, start.index)) std::swap(start, end);
    std::string result;
    const int last = std::min(end.piece, static_cast<int>(pieces.size()) - 1);
    for (int i = std::max(0, start.piece); i <= last; ++i) {
        const auto& p = pieces[static_cast<std::size_t>(i)];
        const int length = static_cast<int>(p.text.size());
        const int from = i == start.piece ? std::min(start.index, length) : 0;
        const int to = i == end.piece ? std::min(end.index, length) : length;
        if (i > start.piece) result += p.join == Join::Newline ? "\n" : p.join == Join::Space ? " " : "";
        if (to > from) result += p.text.substr(static_cast<std::size_t>(from), static_cast<std::size_t>(to - from));
    }
    // Trim spaces the line breaks left at the ends.
    while (!result.empty() && (result.back() == ' ' || result.back() == '\n')) result.pop_back();
    std::size_t lead = 0;
    while (lead < result.size() && (result[lead] == ' ' || result[lead] == '\n')) ++lead;
    return result.substr(lead);
}

std::string Selection::copyAt(Vector2 point) {
    if (!empty()) return copied();
    if (!CheckCollisionPointRec(point, area)) return {};
    // Called after this frame's drawing, so the pieces are where they are on screen now.
    const float dy = 0;
    for (std::size_t i = 0; i < pieces.size(); ++i) {
        const auto& p = pieces[i];
        if (point.y >= p.top + dy && point.y <= p.top + dy + p.height && point.x >= p.x && point.x <= p.x + textWidth(p.text, p.size, p.face)) {
            // The word under the pointer: in a code line that is the address or value, not the whole line.
            int index = 0; float nearest = std::numeric_limits<float>::max();
            for (int n = 0; n <= static_cast<int>(p.text.size()); ++n) {
                const float edge = p.x + prefix(p.text, n, p.size, p.face);
                if (std::fabs(edge - point.x) < nearest) { nearest = std::fabs(edge - point.x); index = n; }
            }
            const auto word = wordAround(p.text, index, p.face);
            anchor = {static_cast<int>(i), word.first}; focus = {static_cast<int>(i), word.second}; has = true;
            return p.text.substr(static_cast<std::size_t>(word.first), static_cast<std::size_t>(word.second - word.first));
        }
    }
    return {};
}

std::vector<Join> lineJoins(const std::string& source, const std::vector<std::string>& lines) {
    std::vector<Join> joins;
    std::size_t at = 0;
    for (std::size_t k = 0; k < lines.size(); ++k) {
        if (k == 0) joins.push_back(Join::Newline);
        else {
            // What separated this line from the pieces one in the source.
            bool newline = false, space = false;
            while (at < source.size() && std::isspace(static_cast<unsigned char>(source[at]))) { if (source[at] == '\n') newline = true; else space = true; ++at; }
            joins.push_back(newline ? Join::Newline : space ? Join::Space : Join::Glue);
        }
        // Consume this line's visible characters from the source.
        std::size_t count = 0;
        for (char ch : lines[k]) if (!std::isspace(static_cast<unsigned char>(ch))) ++count;
        while (count > 0 && at < source.size()) { if (!std::isspace(static_cast<unsigned char>(source[at]))) --count; ++at; }
    }
    return joins;
}

void selectableText(const std::string& value, float x, float y, float size, Color color, Face face, float top, float height, Join join) {
    if (recorder && !value.empty()) recorder->add(displayText(value), x, top, height, size, face, join);
    text(value, x, y, size, color, face);
}
} // namespace breakout::ui

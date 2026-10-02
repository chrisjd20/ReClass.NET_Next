#pragma once
// Mouse text selection for the immediate-mode side panel and the Explain overlay.
// Drawing helpers report each piece of text they draw while a Selection is recording;
// the next frame hit-tests those pieces, so selection survives without a retained layout.
#include "ui.h"
#include <string>
#include <vector>

namespace breakout::ui {
// How a piece of text joins the one before it when copied.
enum class Join { Newline, Space, Glue };

class Selection {
public:
    // Handles the mouse for this region using last frame's pieces. Call before the region's
    // Scroll::begin; dragging past the top or bottom edge scrolls by adjusting offset.
    void input(Rectangle area, float& offset, bool enabled);
    // Pieces drawn between record() and finish() belong to this region.
    void record(float offset);
    void finish();
    // Records one drawn piece and paints its highlight. Call just before drawing the text.
    // top/height are the line box used for hit testing and the highlight.
    void add(const std::string& display, float x, float top, float height, float size, Face face, Join join);
    bool empty() const { return !has || anchor.piece == focus.piece && anchor.index == focus.index; }
    bool dragging() const { return pressing && moved; }
    bool over(Vector2 point) const;
    // Selects the piece under the point (when nothing is selected) and returns the text to copy.
    std::string copyAt(Vector2 point);
    std::string copied() const;
    void clear() { has = false; pressing = false; moved = false; }

private:
    struct Piece { std::string text; float x, top, height, size; Face face; Join join; };
    struct Caret { int piece = -1, index = 0; };
    // pieces: the last completely drawn frame, at scroll offset piecesOffset. building: this frame's, so far.
    std::vector<Piece> pieces, building;
    float piecesOffset = 0, buildingOffset = 0, liveOffset = 0;
    Rectangle area{};
    Caret anchor, focus, pressCaret;
    bool has = false, pressing = false, moved = false;
    Vector2 pressAt{}, lastClickAt{}, lastPoint{};
    bool hasLastPoint = false;
    double lastClick = -1;
    Caret hit(Vector2 point) const;
    float shift() const;
};

// The selection that drawing helpers report to, or null when nothing is recording.
extern Selection* recorder;
// True on the frame a selection drag ends, so the release doesn't also click a button under it.
extern bool releaseConsumed;
// Left-button release that is a click, not the end of a text selection drag.
bool clicked();
// How each wrapped line joins the previous one: a newline where the source had one, a space where
// wrapping broke between words, nothing where a long word was split.
std::vector<Join> lineJoins(const std::string& source, const std::vector<std::string>& lines);
// Draws text and, while recording, makes it selectable.
void selectableText(const std::string& value, float x, float y, float size, Color color, Face face, float top, float height, Join join);
} // namespace breakout::ui

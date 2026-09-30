#include "frontend.h"
#include "game.h"
#include "progress.h"
#include "tutorial.h"
#include "fonts.generated.h"
#include "raylib.h"
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <iomanip>
#include <iostream>
#include <sstream>
#include <string>
#include <vector>
#ifdef _WIN32
#include <process.h>
// MinGW exposes the relocated PE image base as a linker symbol. Keeping this
// declaration independent of windows.h avoids collisions with raylib types.
extern "C" unsigned char __ImageBase;
#else
#include <dlfcn.h>
#include <unistd.h>
#endif

namespace breakout {
namespace {
const Color Background{11, 17, 27, 255}, Panel{20, 29, 42, 255}, Inset{14, 23, 34, 255};
const Color Border{43, 58, 77, 255}, Ink{236, 242, 250, 255}, Muted{171, 187, 208, 255};
const Color Teal{72, 219, 181, 255}, Gold{249, 191, 87, 255}, Red{248, 111, 117, 255};
const Color Blue{111, 169, 248, 255};
enum class Face { Body, Bold, Mono, Heading };
Font bodyFont{}, boldFont{}, monoFont{};
float uiScale = 1;
Font typeface(Face face) {
    if (face == Face::Heading) return GetFontDefault();
    if (face == Face::Bold) return boldFont;
    if (face == Face::Mono) return monoFont;
    return bodyFont;
}
float spacing(Face face) { return face == Face::Heading ? 1.0f : .15f; }
Vector2 mousePosition() { const auto p = GetMousePosition(); return {p.x / uiScale, p.y / uiScale}; }
void scissor(Rectangle rect) {
    // Camera zoom does not transform raylib's scissor rectangle.
    BeginScissorMode(static_cast<int>(std::floor(rect.x * uiScale)), static_cast<int>(std::floor(rect.y * uiScale)),
                     static_cast<int>(std::ceil(rect.width * uiScale)), static_cast<int>(std::ceil(rect.height * uiScale)));
}
Font loadTypeface(const unsigned char* bytes, int count) {
    auto font = LoadFontFromMemory(".ttf", bytes, count, 64, nullptr, 0);
    if (font.texture.id != GetFontDefault().texture.id) SetTextureFilter(font.texture, TEXTURE_FILTER_BILINEAR);
    return font;
}
int processId() {
#ifdef _WIN32
    return static_cast<int>(_getpid());
#else
    return static_cast<int>(getpid());
#endif
}
std::string hexAddress(std::uintptr_t address) {
    std::ostringstream out;
    out << "0x" << std::hex << std::uppercase << address;
    return out.str();
}
std::string moduleReference(std::uintptr_t address) {
    std::uintptr_t base = 0;
    std::string name;
#ifdef _WIN32
    base = reinterpret_cast<std::uintptr_t>(&__ImageBase);
    name = "ReClassBreakout.exe";
#else
    Dl_info info{};
    if (dladdr(reinterpret_cast<const void*>(address), &info) && info.dli_fbase && info.dli_fname) {
        base = reinterpret_cast<std::uintptr_t>(info.dli_fbase);
        name = info.dli_fname;
        const auto separator = name.find_last_of("/\\");
        if (separator != std::string::npos) name.erase(0, separator + 1);
    }
#endif
    if (!base || address < base || name.empty()) return "module offset unavailable";
    return name + "+" + hexAddress(address - base);
}
std::string quoted(std::string value) {
    for (auto& ch : value) if (ch == '\n' || ch == '\r' || ch == '"') ch = ' ';
    return '"' + value + '"';
}
// Keep text consistent with the retained bitmap headings and ASCII font atlas.
// Normalize display only;
// clipboard instructions retain the original lesson text.
std::string displayText(std::string value) {
    static const std::vector<std::pair<std::string, std::string>> replacements{{"→", "->"}, {"’", "'"}, {"×", "x"}, {"…", "..."}, {"–", "-"}, {"—", "-"}, {"“", "\""}, {"”", "\""}};
    for (const auto& replacement : replacements) {
        std::size_t at = 0;
        while ((at = value.find(replacement.first, at)) != std::string::npos) {
            value.replace(at, replacement.first.size(), replacement.second);
            at += replacement.second.size();
        }
    }
    return value;
}
float textWidth(const std::string& value, float size, Face face = Face::Body) {
    return MeasureTextEx(typeface(face), displayText(value).c_str(), size, spacing(face)).x;
}
void text(const std::string& value, float x, float y, float size, Color color = Ink, Face face = Face::Body) {
    DrawTextEx(typeface(face), displayText(value).c_str(), {x, y}, size, spacing(face), color);
}
void heading(const std::string& value, float x, float y, float size, Color color = Ink) {
    text(value, x, y, size, color, Face::Heading);
}
std::vector<std::string> wrap(const std::string& value, float width, float size, Face face = Face::Body) {
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
        if (ch == ' ' || ch == '\t') {
            if (!word.empty()) {
                putWord();
            }
        } else if (ch == '\n') {
            if (!word.empty()) {
                putWord();
            }
            result.push_back(line); line.clear();
        } else word += ch;
    }
    if (!word.empty()) {
        putWord();
    }
    if (!line.empty() || result.empty()) result.push_back(line);
    return result;
}
float paragraph(const std::string& value, float x, float y, float width, float size, Color color = Ink, Face face = Face::Body) {
    const auto lines = wrap(value, std::max(25.0f, width), size, face);
    const float lineHeight = size * 1.48f;
    for (const auto& line : lines) { text(line, x, y, size, color, face); y += lineHeight; }
    return y;
}
void panel(Rectangle rect) {
    DrawRectangleRounded({rect.x, rect.y + 4, rect.width, rect.height}, .035f, 8, Color{5, 10, 18, 120});
    DrawRectangleRounded(rect, .025f, 4, Panel);
    DrawRectangleRoundedLinesEx(rect, .025f, 4, 1, Border);
    DrawLineEx({rect.x + 14, rect.y + 1}, {rect.x + rect.width - 14, rect.y + 1}, 1, Color{67, 87, 109, 150});
}
struct Ui {
    bool blocked = false;
    Rectangle clip{0, 0, 100000, 100000};
    std::string tooltip;
    bool button(Rectangle rect, const std::string& label, float size,
                bool selected = false, const std::string& tip = {}, bool enabled = true) {
        const bool hover = !blocked && CheckCollisionPointRec(mousePosition(), rect) && CheckCollisionPointRec(mousePosition(), clip);
        const Color fill = selected ? Color{35, 88, 81, 255} : hover && enabled ? Color{49, 66, 87, 255} : Color{31, 44, 61, 255};
        DrawRectangleRounded(rect, .16f, 4, fill);
        DrawRectangleRoundedLinesEx(rect, .16f, 4, 1, selected ? Teal : Border);
        const float fitted = std::min(size, (rect.width - 12) / std::max(1.0f, textWidth(label, size, Face::Bold)) * size);
        text(label, rect.x + (rect.width - textWidth(label, fitted, Face::Bold)) * .5f,
             rect.y + (rect.height - fitted) * .5f, fitted, enabled ? selected ? Teal : Ink : Muted, Face::Bold);
        if (hover && !tip.empty()) tooltip = tip;
        return hover && enabled && IsMouseButtonReleased(MOUSE_BUTTON_LEFT);
    }
};
struct Scroll {
    float offset = 0, content = 0;
    void begin(Rectangle rect, bool blocked) {
        const float maximum = std::max(0.0f, content - rect.height + 12);
        if (!blocked && CheckCollisionPointRec(mousePosition(), rect))
            offset -= GetMouseWheelMove() * 48;
        offset = std::clamp(offset, 0.0f, maximum);
        scissor(rect);
    }
    void end(Rectangle rect, float endY) {
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
};
std::string actionName(Action action) {
    switch (action) {
    case Action::FireOnce: return "fire"; case Action::Reload: return "reload";
    case Action::ChargeOnce: return "charge"; case Action::DrainOnce: return "drain";
    case Action::ActivateReactor: return "reactor"; case Action::AdvanceTick: return "tick";
    case Action::ToggleKeycard: return "keycard"; case Action::TogglePower: return "power";
    case Action::ToggleAlarm: return "alarm"; case Action::EvaluateDoor: return "door";
    case Action::SwapWeapon: return "swap"; case Action::TakeOneHit: return "hit_player";
    case Action::HitEnemy: return "hit_enemy"; case Action::EnemyFire: return "enemy_fire";
    case Action::StartTrial: return "trial"; case Action::ToggleTurret: return "turret";
    case Action::AcknowledgeProject: return "project"; case Action::AcknowledgeDiscovery: return "discovery";
    case Action::AcknowledgeTrace: return "trace"; case Action::AcknowledgeRestart: return "restart";
    }
    return "action";
}
std::string actionShortcut(Action action) {
    switch (action) {
    case Action::FireOnce: return "Ctrl+F"; case Action::Reload: return "Ctrl+L";
    case Action::ChargeOnce: return "Ctrl+B"; case Action::DrainOnce: return "Ctrl+D";
    case Action::ActivateReactor: case Action::EvaluateDoor: return "Ctrl+O";
    case Action::AdvanceTick: return "Ctrl+T (held movement also steps)";
    case Action::ToggleKeycard: return "Ctrl+K"; case Action::TogglePower: return "Ctrl+G";
    case Action::ToggleAlarm: return "Ctrl+A"; case Action::SwapWeapon: return "Ctrl+W";
    case Action::TakeOneHit: return "Ctrl+H"; case Action::HitEnemy: return "Ctrl+J";
    case Action::EnemyFire: return "Ctrl+E"; case Action::StartTrial: return "Ctrl+Y";
    case Action::AcknowledgeProject: case Action::AcknowledgeDiscovery:
    case Action::AcknowledgeTrace: case Action::AcknowledgeRestart: return "Ctrl+M";
    default: return "Click once to perform this action";
    }
}
std::string instructions(const Lesson& lesson) {
    std::ostringstream out;
    out << "Room " << lesson.id << ": " << lesson.title << "\n\nObjective: " << lesson.objective
        << "\n\n" << lesson.concept << '\n';
    for (std::size_t i = 0; i < lesson.steps.size(); ++i)
        out << "\n" << i + 1 << ". " << lesson.steps[i].title << "\n" << lesson.steps[i].text
            << "\nExpected: " << lesson.steps[i].expected << '\n';
    out << "\nHints:\n";
    for (const auto& hint : lesson.hints) out << hint << '\n';
    out << "\nSolution:\n";
    for (const auto& solution : lesson.solutions) out << solution << '\n';
    out << "\nRestoration: " << lesson.restoration
        << "\nReset room resets gameplay data only; use Restore original or Restore all for code.\n";
    return out.str();
}
void identity(const Game& game) {
    const auto teaching = game.teaching();
    const auto* world = game.world();
    std::cout << "BREAKOUT pid=" << processId()
              << " root=" << hexAddress(teaching.worldRoot)
              << " player=" << hexAddress(reinterpret_cast<std::uintptr_t>(world->player))
              << " enemy=" << hexAddress(reinterpret_cast<std::uintptr_t>(world->enemies[0]))
              << " ammo_site=" << hexAddress(teaching.ammoSite)
              << " damage_site=" << hexAddress(teaching.damageSite)
              << " vault_entry=" << hexAddress(teaching.vaultEntry)
              << " vault_end=" << hexAddress(teaching.vaultEndpoint)
              << " signature=" << hexAddress(teaching.signature) << std::endl;
}
void logAction(const Game& game, std::uint64_t& sequence, const std::string& name) {
    const auto state = game.acceptance();
    const auto scene = game.renderSnapshot();
    const auto* world = game.world();
    std::cout << "ACTION seq=" << ++sequence << " name=" << name << " room=" << state.room
              << " paused=" << state.paused << " health=" << scene.health << " ammo=" << scene.ammo
              << " x=" << scene.playerX << " y=" << scene.playerY << " charge=" << scene.charge
              << " player=" << hexAddress(reinterpret_cast<std::uintptr_t>(world->player))
              << " enemy=" << hexAddress(reinterpret_cast<std::uintptr_t>(world->enemies[0]))
              << " ticks=" << state.ticks << " shots=" << state.shots << " hits=" << state.hits
              << " enemy_shots=" << state.enemyShots << " targets=" << state.targetsRemaining
              << " swaps=" << state.weaponSwaps << " primary=" << state.outcome.primaryObserved
              << " restored=" << state.outcome.restorationObserved << " manual=" << state.outcome.manualAcknowledged
              << " complete=" << state.outcome.complete << " status=" << quoted(game.status())
              << " outcome=" << quoted(state.outcome.detail) << std::endl;
}
Vector2 movement() {
    return {static_cast<float>(IsKeyDown(KEY_RIGHT) || IsKeyDown(KEY_D)) - static_cast<float>(IsKeyDown(KEY_LEFT) || IsKeyDown(KEY_A)),
            static_cast<float>(IsKeyDown(KEY_DOWN) || IsKeyDown(KEY_S)) - static_cast<float>(IsKeyDown(KEY_UP) || IsKeyDown(KEY_W))};
}
} // namespace

int runFrontend(int initialRoom, const std::string& attachmentDiagnostic) {
    Progress progress;
    progress.load();
    Game game;
    game.setRoom(initialRoom ? initialRoom : progress.room);
    const auto& lessons = Lessons();
    if (lessons.size() != Game::roomCount) { std::cerr << "Expected twelve compiled lessons.\n"; return 1; }
    SetConfigFlags(FLAG_WINDOW_RESIZABLE | FLAG_MSAA_4X_HINT);
    InitWindow(1280, 800, "ReClass: Breakout");
    if (!IsWindowReady()) { std::cerr << "Could not open the desktop game window.\n"; return 1; }
    SetWindowMinSize(800, 560);
    SetExitKey(KEY_NULL);
    SetTargetFPS(60);
    bodyFont = loadTypeface(fonts::Body, static_cast<int>(sizeof(fonts::Body)));
    boldFont = loadTypeface(fonts::Bold, static_cast<int>(sizeof(fonts::Bold)));
    monoFont = loadTypeface(fonts::Mono, static_cast<int>(sizeof(fonts::Mono)));
    identity(game);
    bool menu = false, help = false, quit = false, playFocused = true, dirty = true, referenceExpanded = false;
    Scroll fieldScroll, tutorialScroll, controlScroll, menuScroll, helpScroll;
    std::uint64_t sequence = 0;
    double lastSave = GetTime(), toastUntil = 0, flashUntil = 0;
    std::string toast, previousTooltip;
    double tooltipSince = 0;
    const auto notify = [&](const std::string& value) { toast = value; toastUntil = GetTime() + 3; };
    const auto selectRoom = [&](int room) {
        game.setRoom(room); progress.room = room; dirty = true;
        menu = false; playFocused = true;
        referenceExpanded = false;
        fieldScroll = {}; tutorialScroll = {}; controlScroll = {};
        logAction(game, sequence, "room");
        if (!lessons[room - 1].restoration.empty())
            notify("Before changing patched code, use Restore original / Restore all in ReClass.");
    };
    const auto perform = [&](Action action, bool aimed = false) {
        if (!aimed) game.clearAim();
        if (action == Action::AdvanceTick) { const auto move = movement(); game.advanceTick(move.x, move.y); }
        else game.perform(action);
        if (action == Action::FireOnce) flashUntil = GetTime() + .12;
        logAction(game, sequence, actionName(action));
    };
    const auto reset = [&]() {
        game.resetRoom(); playFocused = true;
        notify("Gameplay data reset. Externally patched code stays patched: restore it in ReClass.");
        logAction(game, sequence, "reset");
    };
    while (!quit && !WindowShouldClose()) {
        // Scale the entire UI, including hit targets, instead of stretching
        // columns while leaving text tiny on a maximized/high-resolution window.
        uiScale = std::clamp(std::min(GetScreenWidth() / 1280.0f, GetScreenHeight() / 800.0f), 1.0f, 2.5f);
        if (!IsWindowFocused()) game.focusLost();
        const bool ctrl = IsKeyDown(KEY_LEFT_CONTROL) || IsKeyDown(KEY_RIGHT_CONTROL);
        if (ctrl && IsKeyPressed(KEY_Q)) quit = true;
        for (int i = 0; i < Game::roomCount; ++i) if (IsKeyPressed(KEY_F1 + i)) selectRoom(i + 1);
        if (IsKeyPressed(KEY_ESCAPE)) { menu = !menu; help = false; game.setPaused(true); }
        if (IsKeyPressed(KEY_SLASH) && !ctrl) help = !help;
        if (ctrl && IsKeyPressed(KEY_R)) reset();
        if (ctrl && IsKeyPressed(KEY_P)) { game.setPaused(!game.paused()); logAction(game, sequence, "pause"); }
        if (ctrl && !help) {
            if (IsKeyPressed(KEY_F)) perform(Action::FireOnce);
            if (IsKeyPressed(KEY_H)) perform(Action::TakeOneHit);
            if (IsKeyPressed(KEY_J)) perform(Action::HitEnemy);
            if (IsKeyPressed(KEY_E)) perform(Action::EnemyFire);
            if (IsKeyPressed(KEY_B)) perform(Action::ChargeOnce);
            if (IsKeyPressed(KEY_D)) perform(Action::DrainOnce);
            if (IsKeyPressed(KEY_W)) perform(Action::SwapWeapon);
            if (IsKeyPressed(KEY_O)) perform(game.room() == 2 ? Action::ActivateReactor : Action::EvaluateDoor);
            if (IsKeyPressed(KEY_T)) perform(Action::AdvanceTick);
            if (IsKeyPressed(KEY_L)) perform(Action::Reload);
            if (IsKeyPressed(KEY_Y)) perform(Action::StartTrial);
            if (IsKeyPressed(KEY_K)) perform(Action::ToggleKeycard);
            if (IsKeyPressed(KEY_G)) perform(Action::TogglePower);
            if (IsKeyPressed(KEY_A)) perform(Action::ToggleAlarm);
            if (IsKeyPressed(KEY_M)) {
                if (game.room() == 5) perform(Action::AcknowledgeProject);
                else if (game.room() == 9) perform(Action::AcknowledgeDiscovery);
                else if (game.room() == 11) perform(Action::AcknowledgeTrace);
                else if (game.room() == 12) perform(Action::AcknowledgeRestart);
            }
        }
        const float screenW = GetScreenWidth() / uiScale, screenH = GetScreenHeight() / uiScale;
        const float margin = 16 + std::max(0.0f, (screenW - 1660) * .5f), gap = 14, top = 96, bottom = screenH - 32;
        const float area = screenW - margin * 2 - gap * 2;
        const float playW = area * .33f, valuesW = area * .26f, tutorialW = area - playW - valuesW;
        Rectangle playPanel{margin, top, playW, bottom - top};
        Rectangle valuesPanel{margin + playW + gap, top, valuesW, bottom - top};
        Rectangle tutorialPanel{valuesPanel.x + valuesW + gap, top, tutorialW, bottom - top};
        const float canvasH = std::min(playPanel.width * .5f, playPanel.height * .34f);
        Rectangle canvas{playPanel.x + 12, playPanel.y + 48, playPanel.width - 24, canvasH};
        const float scale = std::min(canvas.width / 800.0f, canvas.height / 400.0f);
        Rectangle worldCanvas{canvas.x + (canvas.width - 800 * scale) * .5f, canvas.y + (canvas.height - 400 * scale) * .5f, 800 * scale, 400 * scale};
        const bool canvasClick = !menu && !help && IsMouseButtonPressed(MOUSE_BUTTON_LEFT) && CheckCollisionPointRec(mousePosition(), worldCanvas);
        if (IsMouseButtonPressed(MOUSE_BUTTON_LEFT)) playFocused = canvasClick;
        if (canvasClick) {
            const auto mouse = mousePosition();
            game.setAim((mouse.x - worldCanvas.x) / scale, (mouse.y - worldCanvas.y) / scale);
            perform(Action::FireOnce, true);
        }
        if (!menu && !help && playFocused && !ctrl && IsKeyPressed(KEY_SPACE)) {
            const auto mouse = mousePosition();
            if (CheckCollisionPointRec(mouse, worldCanvas)) game.setAim((mouse.x - worldCanvas.x) / scale, (mouse.y - worldCanvas.y) / scale);
            else game.clearAim();
            perform(Action::FireOnce, true);
        }
        const auto move = !menu && !help && playFocused && !ctrl ? movement() : Vector2{0, 0};
        game.update(GetFrameTime(), move.x, move.y);
        const auto outcome = game.outcome();
        if (outcome.complete && !progress.completed[game.room() - 1]) {
            progress.completed[game.room() - 1] = true; dirty = true;
            notify("Observable outcome recorded. Continue freely or explore another room.");
        }
        if (dirty && GetTime() - lastSave > .6) { progress.save(); dirty = false; lastSave = GetTime(); }
        const auto& lesson = lessons[game.room() - 1];
        auto& step = progress.steps[game.room() - 1];
        step = std::clamp(step, 0, std::max(0, static_cast<int>(lesson.steps.size()) - 1));
        const float font = static_cast<float>(progress.textSize);
        Ui ui;
        BeginDrawing();
        ClearBackground(Background);
        BeginMode2D(Camera2D{{0, 0}, {0, 0}, 0, uiScale});
        heading("RECLASS", margin, 15, 14, Teal);
        heading("BREAKOUT", margin, 34, 28, Ink);
        text("MEMORY TRAINING FACILITY", 170, 19, 12, Muted);
        text("PID " + std::to_string(processId()) + " | ReClassBreakout | x64", 170, 41, screenW < 1000 ? 12 : 16, Ink);
        const float headerRight = screenW - margin;
        if (ui.button({headerRight - 304, 17, 90, 34}, "Rooms", 15, menu, "Escape: return to the room menu")) { menu = !menu; help = false; game.setPaused(true); }
        if (ui.button({headerRight - 204, 17, 65, 34}, "Help", 15, help, "?: attachment help and keyboard shortcuts")) { help = !help; menu = false; }
        if (ui.button({headerRight - 130, 17, 36, 34}, "A-", 15, false, "Decrease tutorial text size")) { progress.textSize = std::max(13, progress.textSize - 1); dirty = true; }
        if (ui.button({headerRight - 88, 17, 36, 34}, "A+", 15, false, "Increase tutorial text size")) { progress.textSize = std::min(22, progress.textSize + 1); dirty = true; }
        if (ui.button({headerRight - 44, 17, 44, 34}, "Quit", 13, false, "Ctrl+Q: save progress and close the process")) quit = true;
        text("Room " + std::to_string(game.room()) + " / 12  /  " + lesson.title, margin, 71, 15, Ink, Face::Bold);
        const std::string phase = game.paused() ? "SIMULATION PAUSED" : "SIMULATION RUNNING";
        text(phase, screenW - margin - textWidth(phase, 13, Face::Bold), 68, 13, game.paused() ? Gold : Teal, Face::Bold);
        ui.blocked = menu || help;
        if (!menu && !help) {
            panel(playPanel); panel(valuesPanel); panel(tutorialPanel);
            heading("TRAINING FLOOR", playPanel.x + 12, playPanel.y + 16, 14, Teal);
            if (playPanel.width >= 330) {
                const std::string focusLabel = playFocused ? "FOCUSED" : "CLICK TO AIM";
                text(focusLabel, playPanel.x + playPanel.width - textWidth(focusLabel, 11) - 12, playPanel.y + 17, 11, Muted);
            } else DrawCircleV({playPanel.x + playPanel.width - 18, playPanel.y + 23}, 3, playFocused ? Teal : Muted);
            DrawRectangleRounded(canvas, .025f, 4, Inset);
            scissor(canvas);
            const auto map = [&](float x, float y) { return Vector2{worldCanvas.x + x * scale, worldCanvas.y + y * scale}; };
            for (int x = 0; x <= 800; x += 50) DrawLineV(map(static_cast<float>(x), 0), map(static_cast<float>(x), 400), Color{27, 43, 60, 255});
            for (int y = 0; y <= 400; y += 50) DrawLineV(map(0, static_cast<float>(y)), map(800, static_cast<float>(y)), Color{27, 43, 60, 255});
            const auto scene = game.renderSnapshot();
            if (game.room() == 3) {
                DrawRectangleRec({map(45, 145).x, map(45, 145).y, 640 * scale, 70 * scale}, Color{30, 56, 70, 255});
                text("TRIAL CORRIDOR", worldCanvas.x + 12, worldCanvas.y + 12, 11, Blue);
            }
            for (const auto& object : scene.objects) {
                const auto position = map(object.x, object.y);
                const float radius = std::max(5.0f, object.radius * scale);
                const Color color = !object.active ? Color{54, 75, 80, 255} : object.kind == SceneObject::Kind::Enemy ? Red :
                    object.kind == SceneObject::Kind::Door ? scene.doorOpen ? Teal : Gold : object.kind == SceneObject::Kind::Reactor ? Blue :
                    object.kind == SceneObject::Kind::Exit ? Teal : Gold;
                if (object.kind == SceneObject::Kind::Door || object.kind == SceneObject::Kind::Exit) {
                    DrawRectangleRounded({position.x - radius, position.y - radius * 1.7f, radius * 2, radius * 3.4f}, .1f, 4, color);
                    DrawRectangleLinesEx({position.x - radius - 3, position.y - radius * 1.7f - 3, radius * 2 + 6, radius * 3.4f + 6}, 1, color);
                } else if (object.kind == SceneObject::Kind::Enemy) {
                    DrawRectangleRounded({position.x - radius, position.y - radius, radius * 2, radius * 2}, .2f, 4, color);
                    DrawCircleV({position.x - radius * .35f, position.y - radius * .15f}, 2, Background);
                    DrawCircleV({position.x + radius * .35f, position.y - radius * .15f}, 2, Background);
                } else {
                    DrawCircleV(position, radius, Fade(color, .22f));
                    DrawCircleLines(static_cast<int>(position.x), static_cast<int>(position.y), radius, color);
                    if (object.active) DrawCircleV(position, radius * .36f, color);
                    else { DrawLineEx({position.x - radius * .5f, position.y - radius * .5f}, {position.x + radius * .5f, position.y + radius * .5f}, 2, Muted); }
                }
                if (!object.label.empty() && (object.kind != SceneObject::Kind::Target || game.room() == 6))
                    text(object.label, position.x - textWidth(object.label, 10) * .5f, position.y + radius + 5, 10, Muted);
            }
            if (scene.playerValid && std::isfinite(scene.playerX) && std::isfinite(scene.playerY)) {
                const auto robot = map(scene.playerX, scene.playerY);
                const float radius = std::max(7.0f, 17 * scale);
                DrawCircleV(robot, radius + 4, Fade(Teal, .12f));
                DrawRectangleRounded({robot.x - radius, robot.y - radius, radius * 2, radius * 2}, .25f, 4, Teal);
                DrawRectangleRec({robot.x - radius * .7f, robot.y - radius * .4f, radius * 1.4f, radius * .5f}, Background);
                DrawCircleV({robot.x - radius * .3f, robot.y - radius * .16f}, 1.5f, Blue);
                DrawCircleV({robot.x + radius * .3f, robot.y - radius * .16f}, 1.5f, Blue);
                if (playFocused && CheckCollisionPointRec(mousePosition(), canvas)) {
                    const auto mouse = mousePosition();
                    const float angle = std::atan2(mouse.y - robot.y, mouse.x - robot.x);
                    DrawLineEx(robot, {robot.x + std::cos(angle) * (radius + 9), robot.y + std::sin(angle) * (radius + 9)}, 3, Ink);
                    if (GetTime() < flashUntil) DrawLineEx(robot, mouse, 2, Fade(Gold, .6f));
                    DrawCircleLines(static_cast<int>(mouse.x), static_cast<int>(mouse.y), 6, Teal);
                }
                text("YOU", robot.x - 10, robot.y + radius + 6, 10, Teal);
            } else text("INVALID PLAYER POINTER / POSITION", canvas.x + 10, canvas.y + canvas.height * .5f, 13, Red);
            EndScissorMode();
            float y = canvas.y + canvas.height + 12;
            text("HEALTH " + std::to_string(scene.health), playPanel.x + 12, y, 14, Ink, Face::Bold);
            text("AMMO " + std::to_string(scene.ammo), playPanel.x + playPanel.width * .52f, y, 14, Gold, Face::Bold);
            y += 23;
            if (game.room() == 2 || game.room() == 10) {
                text("CHARGE", playPanel.x + 12, y, 11, Muted);
                const Rectangle bar{playPanel.x + 80, y + 2, playPanel.width - 95, 10};
                DrawRectangleRounded(bar, .4f, 4, Border);
                if (std::isfinite(scene.charge)) DrawRectangleRounded({bar.x, bar.y, bar.width * std::clamp(scene.charge / 100, 0.0f, 1.0f), bar.height}, .4f, 4, Blue);
                y += 22;
            }
            if (game.room() == 3) {
                std::ostringstream trial;
                trial << std::fixed << std::setprecision(2) << "Time " << scene.remainingTime << "s   Exit distance " << scene.corridorDistance;
                y = paragraph(trial.str(), playPanel.x + 12, y, playPanel.width - 24, 13, Blue) + 5;
            }
            const float two = (playPanel.width - 30) * .5f;
            if (ui.button({playPanel.x + 12, y, two, 34}, game.paused() ? "Resume simulation" : "Pause simulation", 14, !game.paused(), "Ctrl+P: simulation only; ReClass debugger pause suspends the entire window")) {
                game.setPaused(!game.paused()); logAction(game, sequence, "pause");
            }
            if (ui.button({playPanel.x + 18 + two, y, two, 34}, "Advance one tick", 14, false, actionShortcut(Action::AdvanceTick))) perform(Action::AdvanceTick);
            y += 45;
            Rectangle controls{playPanel.x + 12, y, playPanel.width - 24, std::max(55.0f, playPanel.y + playPanel.height - y - 79)};
            ui.clip = controls;
            controlScroll.begin(controls, false);
            float cy = controls.y - controlScroll.offset;
            text("CONTROLLED ACTIONS", controls.x, cy, 12, Muted, Face::Bold); cy += 25;
            const auto actions = game.actions();
            int column = 0;
            for (const auto& action : actions) {
                if (!action.available || action.action == Action::AdvanceTick) continue;
                Rectangle button{controls.x + column * (two + 6), cy, two, 34};
                if (ui.button(button, action.label, 13, false, actionShortcut(action.action))) perform(action.action);
                if (column == 1) cy += 41;
                column = 1 - column;
            }
            if (column) cy += 41;
            cy += 7;
            cy = paragraph(game.status(), controls.x, cy, controls.width - 10, 13, Muted) + 10;
            text("OBJECTIVE", controls.x, cy, 12, Teal, Face::Bold); cy += 21;
            cy = paragraph(lesson.objective, controls.x, cy, controls.width - 10, font, Ink) + 12;
            cy = paragraph(outcome.detail, controls.x, cy, controls.width - 10, 13, outcome.complete ? Teal : Gold) + 12;
            cy = paragraph("Observed results do not prove which editing technique you used. Manual acknowledgements record guided steps.", controls.x, cy, controls.width - 10, 12, Muted) + 12;
            controlScroll.end(controls, cy);
            ui.clip = {0, 0, screenW, screenH};
            const float foot = playPanel.y + playPanel.height - 65;
            DrawLineEx({playPanel.x + 12, foot - 8}, {playPanel.x + playPanel.width - 12, foot - 8}, 1, Border);
            if (ui.button({playPanel.x + 12, foot, playPanel.width - 24, 31}, "Reset room - gameplay data only", 13, false, "Ctrl+R: code patches must be restored in ReClass")) reset();
            text("WASD / arrows  |  Aim + click / Space", playPanel.x + 12, foot + 40, 11, Muted);

            heading("LIVE MEMORY", valuesPanel.x + 12, valuesPanel.y + 16, 14, Teal);
            text("Refreshes while paused", valuesPanel.x + 12, valuesPanel.y + 39, 11, Muted);
            const float vbutton = (valuesPanel.width - 30) * .5f;
            if (ui.button({valuesPanel.x + 12, valuesPanel.y + 59, vbutton, 31}, "Reveal address", 12, progress.addresses, "Reveal current address, field type and pointer path")) { progress.addresses = !progress.addresses; dirty = true; }
            if (ui.button({valuesPanel.x + 18 + vbutton, valuesPanel.y + 59, vbutton, 31}, "Hex values", 12, progress.hex, "Compare raw field bytes with the typed value")) { progress.hex = !progress.hex; dirty = true; }
            Rectangle fieldsBody{valuesPanel.x + 12, valuesPanel.y + 103, valuesPanel.width - 24, valuesPanel.height - 118};
            ui.clip = fieldsBody;
            fieldScroll.begin(fieldsBody, false);
            float fy = fieldsBody.y - fieldScroll.offset;
            auto visibleFields = game.fields();
            std::vector<std::string> priority;
            switch (game.room()) {
            case 1: case 8: case 12: priority = {"Player ammo", "Player health"}; break;
            case 2: priority = {"Player charge", "Door decision"}; break;
            case 3: priority = {"Player speed", "Player X", "Player Y", "Remaining time", "Corridor distance"}; break;
            case 4: case 11: priority = {"Player keycard", "Player flags", "Player clearance", "Door decision"}; break;
            case 5: priority = {"Player callsign", "Player clearance", "Door decision"}; break;
            case 6: priority = {"Player equipped", "Player weapon name", "Player weapon damage", "Player projectile speed", "Player cooldown", "Player Inventory", "Player slots[0]", "Player slots[1]"}; break;
            case 7: priority = {"Player health", "Player ammo"}; break;
            case 9: priority = {"Player health", "Player faction", "Enemy health", "Enemy faction"}; break;
            case 10: priority = {"Player health", "Player ammo", "Player faction", "Enemy health", "Enemy ammo", "Enemy faction"}; break;
            }
            const auto rank = [&](const FieldSnapshot& field) { return std::find(priority.begin(), priority.end(), field.label) - priority.begin(); };
            std::stable_sort(visibleFields.begin(), visibleFields.end(), [&](const FieldSnapshot& a, const FieldSnapshot& b) { return rank(a) < rank(b); });
            for (const auto& field : visibleFields) {
                const float start = fy;
                fy = paragraph(field.label, fieldsBody.x, fy, fieldsBody.width - 56, font, field.valid ? Ink : Red, Face::Bold);
                const std::string copy = field.roundTrip.empty() ? field.value : field.roundTrip;
                if (ui.button({fieldsBody.x + fieldsBody.width - 49, start, 42, 24}, "Copy", 11, false, "Copy exact round-trip value: " + copy)) { SetClipboardText(copy.c_str()); notify("Copied " + field.label + " = " + copy); }
                fy = paragraph(field.type == "float32" ? copy : field.value, fieldsBody.x, fy + 6, fieldsBody.width - 10, font + 1, field.valid ? Gold : Red, Face::Mono);
                fy = paragraph(field.type, fieldsBody.x, fy + 3, fieldsBody.width - 10, 12, Muted);
                if (progress.hex && !field.rawHex.empty()) fy = paragraph("Raw: " + field.rawHex, fieldsBody.x, fy + 4, fieldsBody.width - 10, 12, Blue, Face::Mono);
                if (progress.addresses) {
                    const std::string address = hexAddress(field.address);
                    fy = paragraph(address, fieldsBody.x, fy + 4, fieldsBody.width - 60, 13, Teal, Face::Mono);
                    if (ui.button({fieldsBody.x + fieldsBody.width - 49, fy - 22, 42, 22}, "Copy", 11, false, "Copy current absolute field address")) { SetClipboardText(address.c_str()); notify("Copied address for " + field.label); }
                    fy = paragraph(field.path, fieldsBody.x, fy + 2, fieldsBody.width - 10, 12, Muted);
                    if (field.label == "Module root")
                        fy = paragraph("Module: " + moduleReference(field.address), fieldsBody.x, fy + 3, fieldsBody.width - 10, 12, Blue);
                }
                fy += 11;
                DrawLineEx({fieldsBody.x, fy}, {fieldsBody.x + fieldsBody.width - 10, fy}, 1, Border); fy += 13;
            }
            const auto teaching = game.teaching();
            if (teaching.ammoPatched || teaching.damagePatched || teaching.vaultPatched) {
                fy = paragraph("CODE BYTES DIFFER FROM STARTUP", fieldsBody.x, fy, fieldsBody.width - 10, 13, Gold) + 7;
                fy = paragraph("Use Restore original or Restore all in ReClass. Reset room does not restore code.", fieldsBody.x, fy, fieldsBody.width - 10, 13, Ink) + 15;
            }
            if (progress.addresses) {
                text("TEACHING SITES", fieldsBody.x, fy, 12, Teal, Face::Bold); fy += 22;
                const std::vector<std::pair<std::string, std::uintptr_t>> sites{{"Module root pointer", teaching.worldRoot}, {"Ammo patch site", teaching.ammoSite}, {"Damage patch site", teaching.damageSite}, {"Vault entry", teaching.vaultEntry}, {"Vault endpoint", teaching.vaultEndpoint}, {"Stable signature", teaching.signature}};
                for (const auto& site : sites) {
                    fy = paragraph(site.first, fieldsBody.x, fy, fieldsBody.width - 10, 12, Muted);
                    const auto address = hexAddress(site.second);
                    fy = paragraph(address, fieldsBody.x, fy + 3, fieldsBody.width - 60, 13, Teal, Face::Mono);
                    if (ui.button({fieldsBody.x + fieldsBody.width - 49, fy - 22, 42, 22}, "Copy", 11, false, "Copy current teaching-site address")) { SetClipboardText(address.c_str()); notify("Copied " + site.first); }
                    const auto relative = moduleReference(site.second);
                    fy = paragraph("Module: " + relative, fieldsBody.x, fy + 3, fieldsBody.width - 10, 12, Blue);
                    if (ui.button({fieldsBody.x, fy + 3, fieldsBody.width - 10, 23}, "Copy module + offset", 11, false, "Copy " + relative, relative != "module offset unavailable")) { SetClipboardText(relative.c_str()); notify("Copied module offset for " + site.first); }
                    fy += 29;
                    fy += 12;
                }
                fy = paragraph("Persistent definitions use module offsets or signatures; absolute addresses change with ASLR.", fieldsBody.x, fy, fieldsBody.width - 10, 12, Muted) + 10;
                if (!teaching.signaturePattern.empty()) fy = paragraph("Pattern: " + teaching.signaturePattern, fieldsBody.x, fy, fieldsBody.width - 10, 12, Blue) + 10;
            }
            fieldScroll.end(fieldsBody, fy);
            ui.clip = {0, 0, screenW, screenH};

            heading("TUTORIAL", tutorialPanel.x + 12, tutorialPanel.y + 16, 14, Teal);
            const std::string badge = progress.completed[game.room() - 1] ? "RECORDED" : "EXPLORE";
            text(badge, tutorialPanel.x + tutorialPanel.width - textWidth(badge, 11) - 12, tutorialPanel.y + 17, 11, progress.completed[game.room() - 1] ? Teal : Muted);
            const float tbutton = (tutorialPanel.width - 30) * .5f;
            if (ui.button({tutorialPanel.x + 12, tutorialPanel.y + 43, tbutton, 30}, "Show hint", 13, progress.hints)) { progress.hints = !progress.hints; dirty = true; }
            if (ui.button({tutorialPanel.x + 18 + tbutton, tutorialPanel.y + 43, tbutton, 30}, "Show solution", 13, progress.solutions)) { progress.solutions = !progress.solutions; dirty = true; }
            Rectangle tutorialBody{tutorialPanel.x + 12, tutorialPanel.y + 88, tutorialPanel.width - 24, tutorialPanel.height - 188};
            ui.clip = tutorialBody;
            tutorialScroll.begin(tutorialBody, false);
            float ty = tutorialBody.y - tutorialScroll.offset;
            ty = paragraph(lesson.title, tutorialBody.x, ty, tutorialBody.width - 10, font + 6, Ink, Face::Bold) + 12;
            const auto conceptBreak = lesson.concept.find("\n\n");
            const auto introduction = lesson.concept.substr(0, conceptBreak);
            ty = paragraph(introduction, tutorialBody.x, ty, tutorialBody.width - 10, font, Muted) + 18;
            if (!lesson.steps.empty()) {
                const auto& current = lesson.steps[step];
                ty = paragraph("STEP " + std::to_string(step + 1) + " / " + std::to_string(lesson.steps.size()), tutorialBody.x, ty, tutorialBody.width - 10, 12, Teal) + 7;
                ty = paragraph(current.title, tutorialBody.x, ty, tutorialBody.width - 10, font + 2, Ink, Face::Bold) + 12;
                ty = paragraph(current.text, tutorialBody.x, ty, tutorialBody.width - 10, font, Ink) + 16;
                text("EXPECTED OBSERVATION", tutorialBody.x, ty, 12, Gold, Face::Bold); ty += 22;
                ty = paragraph(current.expected, tutorialBody.x, ty, tutorialBody.width - 10, font, Muted) + 18;
            }
            if (progress.hints) {
                text("HINTS", tutorialBody.x, ty, 12, Blue, Face::Bold); ty += 23;
                for (const auto& hint : lesson.hints) ty = paragraph(hint, tutorialBody.x, ty, tutorialBody.width - 10, font, Blue) + 12;
                ty += 5;
            }
            if (progress.solutions) {
                text("WORKED SOLUTION", tutorialBody.x, ty, 12, Teal, Face::Bold); ty += 23;
                for (const auto& solution : lesson.solutions) ty = paragraph(solution, tutorialBody.x, ty, tutorialBody.width - 10, font, Ink) + 12;
                ty += 5;
            }
            if (!lesson.restoration.empty()) {
                text("RESTORATION", tutorialBody.x, ty, 12, Gold, Face::Bold); ty += 23;
                ty = paragraph(lesson.restoration, tutorialBody.x, ty, tutorialBody.width - 10, font, Gold) + 12;
            }
            if (conceptBreak != std::string::npos) {
                if (ui.button({tutorialBody.x, ty, tutorialBody.width - 10, 34},
                              referenceExpanded ? "Hide controls and common mistakes" : "Controls and common mistakes", 13, referenceExpanded))
                    referenceExpanded = !referenceExpanded;
                ty += 46;
                if (referenceExpanded)
                    ty = paragraph(lesson.concept.substr(conceptBreak + 2), tutorialBody.x, ty, tutorialBody.width - 10, font, Muted) + 14;
            }
            ty = paragraph("ReClass debugger pause suspends the entire process, including this window. Keep the offline HTML or Markdown guide open for those steps.", tutorialBody.x, ty, tutorialBody.width - 10, 13, Muted) + 14;
            tutorialScroll.end(tutorialBody, ty);
            ui.clip = {0, 0, screenW, screenH};
            const float nav = tutorialPanel.y + tutorialPanel.height - 91;
            DrawLineEx({tutorialPanel.x + 12, nav - 9}, {tutorialPanel.x + tutorialPanel.width - 12, nav - 9}, 1, Border);
            if (ui.button({tutorialPanel.x + 12, nav, tbutton, 32}, "Back", 14, false, "Tutorial navigation never requires an outcome", step > 0)) { --step; tutorialScroll = {}; dirty = true; }
            if (ui.button({tutorialPanel.x + 18 + tbutton, nav, tbutton, 32}, "Next", 14, false, "Tutorial navigation never requires an outcome", step + 1 < static_cast<int>(lesson.steps.size()))) { ++step; tutorialScroll = {}; dirty = true; }
            if (ui.button({tutorialPanel.x + 12, nav + 42, tutorialPanel.width - 24, 31}, "Copy instructions", 13, false, "Copy the complete lesson, hints, solution, and restoration text")) { const auto copy = instructions(lesson); SetClipboardText(copy.c_str()); notify("Complete room instructions copied."); }
        } else if (menu) {
            Rectangle surface{margin, top, screenW - margin * 2, bottom - top};
            panel(surface);
            heading("CHOOSE YOUR NEXT ROOM", surface.x + 20, surface.y + 18, 21, Ink);
            text("Every room is available. Progress records observations and tutorial position, never memory edits or code patches.", surface.x + 20, surface.y + 51, 13, Muted);
            const int cols = screenW >= 1100 ? 3 : 2;
            const float tileW = (surface.width - 40 - (cols - 1) * 14) / cols;
            Rectangle body{surface.x + 20, surface.y + 83, surface.width - 40, surface.height - 155};
            menuScroll.begin(body, false);
            float endY = body.y;
            for (int i = 0; i < 12; ++i) {
                Rectangle tile{body.x + (i % cols) * (tileW + 14), body.y + (i / cols) * 115 - menuScroll.offset, tileW, 101};
                const bool hover = CheckCollisionPointRec(mousePosition(), tile);
                DrawRectangleRounded(tile, .07f, 4, hover ? Color{38, 54, 73, 255} : Inset);
                DrawRectangleRoundedLinesEx(tile, .07f, 4, 1, i + 1 == game.room() ? Teal : Border);
                text((i + 1 < 10 ? "0" : "") + std::to_string(i + 1), tile.x + 14, tile.y + 15, 24, progress.completed[i] ? Teal : Blue);
                paragraph(lessons[i].title, tile.x + 56, tile.y + 16, tile.width - 70, 17, Ink);
                text(progress.completed[i] ? "OBSERVED + RECORDED" : "READY TO EXPLORE", tile.x + 14, tile.y + 67, 11, progress.completed[i] ? Teal : Muted);
                text("F" + std::to_string(i + 1), tile.x + tile.width - 42, tile.y + 68, 11, Muted);
                if (hover && CheckCollisionPointRec(mousePosition(), body) && IsMouseButtonReleased(MOUSE_BUTTON_LEFT)) selectRoom(i + 1);
                endY = std::max(endY, tile.y + tile.height + 14);
            }
            menuScroll.end(body, endY);
            const float footer = surface.y + surface.height - 55;
            ui.blocked = false;
            if (ui.button({surface.x + 20, footer, 210, 34}, "Return to current room", 14)) menu = false;
            paragraph("Before leaving a patching lesson, use Restore original / Restore all. Restarting the executable creates a fresh process.", surface.x + 245, footer, surface.width - 265, 13, Gold);
        } else if (help) {
            Rectangle surface{margin, top, screenW - margin * 2, bottom - top};
            panel(surface);
            heading("ATTACH, INSPECT, EXPERIMENT", surface.x + 20, surface.y + 20, 22, Teal);
            Rectangle helpBody{surface.x + 20, surface.y + 61, surface.width - 40, surface.height - 124};
            helpScroll.begin(helpBody, false);
            float hy = helpBody.y - helpScroll.offset;
            hy = paragraph("Attach ReClass to ReClassBreakout (PID " + std::to_string(processId()) + "). Use the same platform's x64 executable. Enable address reveals to find the live root pointer and teaching sites.", surface.x + 20, hy, surface.width - 40, font, Ink) + 12;
            hy = paragraph(attachmentDiagnostic, surface.x + 20, hy, surface.width - 40, font, Gold) + 15;
            hy = paragraph("Simulation pause keeps rendering and values live. ReClass debugger pause suspends the process and its window. After focus loss the simulation stays paused until you resume it.", surface.x + 20, hy, surface.width - 40, font, Muted) + 20;
            const float col = (surface.width - 60) * .5f;
            text("MOVEMENT AND NAVIGATION", surface.x + 20, hy, 13, Teal);
            text("DETERMINISTIC ACTIONS", surface.x + 40 + col, hy, 13, Teal); hy += 27;
            const float leftEnd = paragraph("WASD / arrows: move when the playfield has focus\nMouse: aim; click / Space: fire in the playfield\nF1..F12: select any room\nCtrl+P: pause / resume simulation\nCtrl+T: advance exactly one tick (hold movement)\nCtrl+R: reset gameplay data\nCtrl+M: acknowledge the room's manual exercise\nEscape: room menu    ?: this help    Ctrl+Q: quit\nMouse wheel: scroll values, instructions, actions\nA- / A+: text size; preferences save locally", surface.x + 20, hy, col, font, Ink);
            const float rightY = paragraph("Ctrl+F: Fire once    Ctrl+L: Reload\nCtrl+H: Take one hit    Ctrl+J: Hit enemy\nCtrl+E: Enemy fire    Ctrl+W: Swap weapon\nCtrl+B: Recharge once    Ctrl+D: Drain once\nCtrl+Y: Start trial\nCtrl+K: Keycard    Ctrl+G: Power    Ctrl+A: Alarm\nCtrl+O: Evaluate door / Activate reactor", surface.x + 40 + col, hy, col, font, Ink);
            const float rightEnd = paragraph("Data reset never restores patched code. Use Restore original / Restore all in ReClass. Saved definitions must match the platform and executable image; loading a definition does not apply it.", surface.x + 40 + col, rightY + 20, col, font, Gold);
            helpScroll.end(helpBody, std::max(leftEnd, rightEnd) + 14);
            ui.blocked = false;
            if (ui.button({surface.x + 20, surface.y + surface.height - 51, 180, 33}, "Back to room", 14)) help = false;
        }
        if (!progress.diagnostic.empty()) text(progress.diagnostic, margin, screenH - 20, 11, Gold);
        else text("Local progress only  |  No automatic patches  |  ReClass debugger pause freezes this window", margin, screenH - 20, 11, Muted);
        if (ui.tooltip != previousTooltip || IsMouseButtonPressed(MOUSE_BUTTON_LEFT)) {
            previousTooltip = ui.tooltip; tooltipSince = GetTime();
        }
        if (!ui.tooltip.empty() && GetTime() - tooltipSince > .55) {
            const float tooltipW = std::min(screenW - 40, std::max(80.0f, textWidth(ui.tooltip, 13) + 24));
            const auto lines = wrap(ui.tooltip, tooltipW - 24, 13);
            const float tooltipH = static_cast<float>(lines.size()) * 18 + 16;
            const float tx = std::clamp(mousePosition().x + 14.0f, 12.0f, screenW - tooltipW - 12);
            const float tyy = std::clamp(mousePosition().y + 22.0f, 12.0f, screenH - tooltipH - 12);
            DrawRectangleRounded({tx, tyy, tooltipW, tooltipH}, .06f, 4, Color{37, 53, 73, 255});
            paragraph(ui.tooltip, tx + 12, tyy + 8, tooltipW - 24, 13, Ink);
        }
        if (GetTime() < toastUntil && !toast.empty()) {
            const float width = std::min(screenW - 40, textWidth(toast, 13) + 28);
            const auto lines = wrap(toast, width - 24, 13);
            const float height = static_cast<float>(lines.size()) * 18 + 16;
            const Rectangle rect{(screenW - width) * .5f, screenH - height - 35, width, height};
            DrawRectangleRounded(rect, .1f, 4, Color{35, 76, 69, 255});
            paragraph(toast, rect.x + 12, rect.y + 8, width - 24, 13, Ink);
        }
        EndMode2D();
        EndDrawing();
    }
    progress.room = game.room(); progress.save();
    for (const auto& font : {bodyFont, boldFont, monoFont})
        if (font.texture.id != GetFontDefault().texture.id) UnloadFont(font);
    CloseWindow();
    return 0;
}
} // namespace breakout

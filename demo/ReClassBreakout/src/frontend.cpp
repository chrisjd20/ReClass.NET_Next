#include "frontend.h"
#include "game.h"
#include "progress.h"
#include "tutorial.h"
#include "scene.h"
#include "ui.h"
#include "raylib.h"
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <set>
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
using namespace ui;
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
std::string actionName(Action action) {
    switch (action) {
    case Action::FireOnce: return "fire";
    case Action::ChargeOnce: return "charge"; case Action::DrainOnce: return "drain";
    case Action::ActivateReactor: return "reactor"; case Action::AdvanceTick: return "tick";
    case Action::ToggleAlarm: return "alarm"; case Action::EvaluateDoor: return "door";
    case Action::SwapWeapon: return "swap"; case Action::TakeOneHit: return "hit_player";
    case Action::HitEnemy: return "hit_enemy"; case Action::EnemyFire: return "enemy_fire";
    case Action::StartTrial: return "trial"; case Action::StartCountdown: return "countdown";
    case Action::RebootRelay: return "relay";
    }
    return "action";
}
std::string plain(std::string value) {
    value.erase(std::remove(value.begin(), value.end(), '`'), value.end());
    return value;
}
std::string instructions(const Lesson& lesson) {
    std::ostringstream out;
    out << "Room " << lesson.id << ": " << lesson.title << "\nGoal: " << plain(lesson.goal) << "\n";
    for (std::size_t i = 0; i < lesson.steps.size(); ++i)
        out << "\n" << i + 1 << ". [" << (lesson.steps[i].where == "game" ? "IN GAME" : "IN RECLASS") << "] " << plain(lesson.steps[i].text) << '\n';
    if (!lesson.restore.empty()) out << "\nBefore leaving: " << plain(lesson.restore) << '\n';
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
              << " signature=" << hexAddress(teaching.signature)
              << " badge_site=" << hexAddress(teaching.badgeSite) << std::endl;
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
              << " world=" << hexAddress(reinterpret_cast<std::uintptr_t>(world))
              << " ticks=" << state.ticks << " shots=" << state.shots << " hits=" << state.hits
              << " enemy_shots=" << state.enemyShots << " targets=" << state.targetsRemaining
              << " swaps=" << state.weaponSwaps << " links=" << state.links << " primary=" << state.outcome.primaryObserved
              << " restored=" << state.outcome.restorationObserved
              << " complete=" << state.outcome.complete << " status=" << quoted(game.status())
              << " outcome=" << quoted(state.outcome.detail) << std::endl;
}
void capture(const char* path) {
    Image image = LoadImageFromScreen();
    ImageFormat(&image, PIXELFORMAT_UNCOMPRESSED_R8G8B8);
    std::ofstream file(path, std::ios::binary);
    file << "P6\n" << image.width << ' ' << image.height << "\n255\n";
    file.write(static_cast<const char*>(image.data), static_cast<std::streamsize>(image.width) * image.height * 3);
    UnloadImage(image);
}
Vector2 movement() {
    return {static_cast<float>(IsKeyDown(KEY_RIGHT) || IsKeyDown(KEY_D)) - static_cast<float>(IsKeyDown(KEY_LEFT) || IsKeyDown(KEY_A)),
            static_cast<float>(IsKeyDown(KEY_DOWN) || IsKeyDown(KEY_S)) - static_cast<float>(IsKeyDown(KEY_UP) || IsKeyDown(KEY_W))};
}
void checkmark(float x, float y, float size, Color color) {
    DrawLineEx({x, y + size * .55f}, {x + size * .38f, y + size * .9f}, 2.2f, color);
    DrawLineEx({x + size * .38f, y + size * .9f}, {x + size, y + size * .1f}, 2.2f, color);
}
// Lesson text: `labels` become chips, lines after a ':' newline (assembly) use a monospace face.
float rich(const std::string& value, float x, float y, float width, float size, Color color, bool draw = true) {
    const float lineHeight = size * 1.6f;
    float cx = x;
    bool code = false;
    const auto newline = [&]() { cx = x; y += lineHeight; };
    std::size_t at = 0;
    while (at <= value.size()) {
        const auto end = value.find('\n', at);
        const std::string line = value.substr(at, end == std::string::npos ? std::string::npos : end - at);
        if (code) {
            if (draw) {
                DrawRectangleRec({x - 2, y - 2, width, lineHeight}, Color{12, 19, 29, 255});
                text(line, x + 6, y + 1, size - 1, Teal, Face::Mono);
            }
            newline();
        } else {
            // Tokenize into words and backtick chips; chips never break.
            std::size_t i = 0;
            while (i < line.size()) {
                if (line[i] == ' ') { ++i; continue; }
                std::string token; bool chip = false;
                if (line[i] == '`') {
                    const auto close = line.find('`', i + 1);
                    token = line.substr(i + 1, (close == std::string::npos ? line.size() : close) - i - 1);
                    i = close == std::string::npos ? line.size() : close + 1;
                    chip = true;
                } else {
                    const auto next = line.find_first_of(" `", i);
                    token = line.substr(i, (next == std::string::npos ? line.size() : next) - i);
                    i = next == std::string::npos ? line.size() : next;
                }
                // Keep punctuation that directly follows a chip attached to it.
                std::string tail;
                if (chip) while (i < line.size() && line[i] != ' ' && line[i] != '`') tail += line[i++];
                const float tokenW = (chip ? textWidth(token, size - 1, Face::Bold) + 10 : textWidth(token, size)) + (tail.empty() ? 0 : textWidth(tail, size));
                if (cx > x && cx + tokenW > x + width) newline();
                if (chip) {
                    const float chipW = textWidth(token, size - 1, Face::Bold) + 10;
                    if (draw) {
                        DrawRectangleRounded({cx, y - 1, chipW, size + 4}, .35f, 4, Color{26, 52, 60, 255});
                        DrawRectangleRoundedLinesEx({cx, y - 1, chipW, size + 4}, .35f, 4, 1, Fade(Teal, .6f));
                        text(token, cx + 5, y + 1, size - 1, Teal, Face::Bold);
                    }
                    cx += chipW;
                    if (!tail.empty()) { if (draw) text(tail, cx, y, size, color); cx += textWidth(tail, size); }
                } else {
                    if (draw) text(token, cx, y, size, color);
                    cx += textWidth(token, size);
                }
                cx += textWidth(" ", size);
            }
            if (!line.empty() && line.back() == ':' && end != std::string::npos) code = true;
            newline();
        }
        if (end == std::string::npos) break;
        at = end + 1;
    }
    return y;
}
std::string firstLine(const std::string& value, float width, float size) {
    const auto lines = wrap(plain(value.substr(0, value.find('\n'))), width, size);
    return lines.empty() ? std::string() : lines.front() + (lines.size() > 1 ? "..." : "");
}

// Screen regions, in logical UI units, recomputed each frame.
struct Layout {
    Rectangle top{}, viewport{}, banner{}, world{}, hud{}, drawer{};
    static constexpr float TopH = 50, BannerH = 42, HudH = 48, Margin = 10, Rail = 40;
    static Layout compute(float screenW, float screenH, bool drawerOpen) {
        Layout l;
        const float drawerW = drawerOpen ? std::clamp(screenW * .32f, 340.0f, 500.0f) : Rail;
        l.top = {0, 0, screenW, TopH};
        l.viewport = {Margin, TopH, screenW - drawerW - Margin * 3, screenH - TopH - Margin};
        l.banner = {l.viewport.x, l.viewport.y, l.viewport.width, BannerH};
        l.hud = {l.viewport.x, l.viewport.y + l.viewport.height - HudH, l.viewport.width, HudH};
        l.world = {l.viewport.x, l.viewport.y + BannerH, l.viewport.width, l.viewport.height - BannerH - HudH};
        l.drawer = {l.viewport.x + l.viewport.width + Margin, TopH, drawerW, screenH - TopH - Margin};
        return l;
    }
};
std::vector<std::string> memoryPriority(int room) {
    switch (room) {
    case 1: case 8: case 13: return {"Player ammo", "Player health"};
    case 2: return {"Player charge"};
    case 3: return {"Player X", "Player speed", "Player Y", "Remaining time"};
    case 4: return {"Player flags", "Player keycard", "Door decision"};
    case 5: return {"Player Inventory", "Player equipped", "Player weapon damage", "Player weapon name"};
    case 6: return {"Module root", "Relay power", "World.room", "World.player"};
    case 7: return {"Player health"};
    case 9: return {"Player callsign", "Player clearance", "Door decision"};
    case 10: return {"Player health", "Player clearance", "Player faction", "Enemy health"};
    case 11: return {"Player health", "Player ammo", "Enemy ammo", "Enemy health"};
    case 12: return {"Player keycard", "Player clearance", "Player flags", "Door decision"};
    default: return {};
    }
}
void bar(Rectangle rect, float fraction, Color color, Color back = Border) {
    DrawRectangleRounded(rect, .5f, 4, back);
    if (std::isfinite(fraction) && fraction > 0)
        DrawRectangleRounded({rect.x, rect.y, rect.width * std::clamp(fraction, 0.0f, 1.0f), rect.height}, .5f, 4, color);
}
// Tracks what happened since the current tutorial step began, so steps the
// game can observe tick themselves off.
struct StepTracker {
    int room = -1, step = -1;
    RenderSnapshot base;
    std::set<std::string> writes, actions;
    bool sawPatch = false;
    double satisfiedAt = -1;
    void begin(int newRoom, int newStep, const RenderSnapshot& snapshot, bool patched) {
        room = newRoom; step = newStep; base = snapshot; writes.clear(); actions.clear();
        sawPatch = patched; satisfiedAt = -1;
    }
    bool satisfied(const std::string& check, const RenderSnapshot& now, const OutcomeSnapshot& outcome, bool patched) const {
        const auto colon = check.find(':');
        const std::string kind = check.substr(0, colon), arg = colon == std::string::npos ? "" : check.substr(colon + 1);
        if (kind == "fired") return now.shots > base.shots;
        if (kind == "hit") return now.hits > base.hits;
        if (kind == "patched") return patched;
        if (kind == "restored") return sawPatch && !patched;
        if (kind == "primary") return outcome.primaryObserved;
        if (kind == "complete") return outcome.complete;
        if (kind == "moved") return std::fabs(now.playerX - base.playerX) + std::fabs(now.playerY - base.playerY) >= 60;
        if (kind == "moved_right") return now.playerX >= base.playerX + 30;
        if (kind == "moved_left") return now.playerX <= base.playerX - 30;
        if (kind == "relay") return now.links >= std::atoi(arg.c_str());
        if (kind == "write") return writes.count(arg) != 0;
        if (kind == "action") return actions.count(arg) != 0;
        if (kind == "zone") return std::any_of(now.zones.begin(), now.zones.end(), [&](const Zone& z) { return z.id == arg && z.occupied; });
        return false;
    }
};
} // namespace

int runFrontend(int initialRoom, const std::string& attachmentDiagnostic, bool unlockAll) {
    Progress progress;
    progress.load();
    // A room opens once the room before it is complete; completed rooms stay open.
    const auto unlocked = [&](int room) { return unlockAll || room == 0 || progress.completed[room] || progress.completed[room - 1]; };
    const auto latestUnlocked = [&]() { int room = 0; while (room + 1 < Game::roomCount && unlocked(room + 1)) ++room; return room; };
    Game game;
    {
        int start = initialRoom >= 0 ? initialRoom : progress.room;
        if (!unlocked(start)) {
            if (initialRoom >= 0) std::cerr << "Room " << start << " is locked: complete room " << start - 1 << " first.\n";
            start = latestUnlocked();
        }
        game.setRoom(start);
    }
    const auto& lessons = Lessons();
    if (static_cast<int>(lessons.size()) != Game::roomCount) { std::cerr << "Expected fourteen compiled lessons.\n"; return 1; }
    SetConfigFlags(FLAG_WINDOW_RESIZABLE | FLAG_MSAA_4X_HINT);
    InitWindow(1280, 800, "ReClass: Breakout");
    if (!IsWindowReady()) { std::cerr << "Could not open the desktop game window.\n"; return 1; }
    SetWindowMinSize(960, 600);
    SetExitKey(KEY_NULL);
    SetTargetFPS(60);
    loadFonts();
    Scene scene;
    scene.load();
    identity(game);
    bool menu = false, help = false, quit = false, dirty = true, previousHot = false, recipeOpen = false;
    int tab = 0;
    Scroll fieldScroll, tutorialScroll, menuScroll, helpScroll;
    std::uint64_t sequence = 0;
    double lastSave = GetTime(), toastUntil = 0, failUntil = 0, goUntil = 0, completeUntil = 0;
    std::string toast, previousTooltip;
    double tooltipSince = 0;
    RenderSnapshot last = game.renderSnapshot();
    scene.reset(last);
    bool sceneReset = false, aimed = false, ctrlBefore = false;
    int lastFail = last.failSerial;
    Vector2 aimWorld{0, 0};
    StepTracker tracker;
    // Developer screenshots: RECLASS_BREAKOUT_CAPTURE=file.ppm [RECLASS_BREAKOUT_CAPTURE_FRAME=N].
    const char* captureTarget = std::getenv("RECLASS_BREAKOUT_CAPTURE");
    int captureFrame = captureTarget && *captureTarget ? 90 : 0;
    if (const char* frames = std::getenv("RECLASS_BREAKOUT_CAPTURE_FRAME"); captureFrame && frames) captureFrame = std::max(1, std::atoi(frames));
    const auto notify = [&](const std::string& value) { toast = value; toastUntil = GetTime() + 3.5; };
    const auto selectRoom = [&](int room) {
        if (!unlocked(room)) { notify("Room " + std::to_string(room) + " is locked. Complete room " + std::to_string(room - 1) + " to open it."); return; }
        game.setRoom(room); progress.room = room; dirty = true;
        menu = false; recipeOpen = false; sceneReset = true;
        fieldScroll = {}; tutorialScroll = {};
        logAction(game, sequence, "room");
    };
    const auto offered = [&](Action action) {
        const auto actions = game.actions();
        return std::any_of(actions.begin(), actions.end(), [action](const auto& d) { return d.action == action; });
    };
    const auto perform = [&](Action action, bool withAim = false) {
        if (!withAim) game.clearAim();
        aimed = withAim;
        if (action == Action::AdvanceTick) { const auto move = movement(); game.advanceTick(move.x, move.y); }
        else game.perform(action);
        if (action == Action::EvaluateDoor || action == Action::ActivateReactor) scene.attempt(game.room(), game.outcome().primaryObserved);
        tracker.actions.insert(actionName(action));
        logAction(game, sequence, actionName(action));
    };
    const auto reset = [&]() {
        game.resetRoom(); sceneReset = true;
        notify("Room restarted. Patched code stays patched: restore it in ReClass.");
        logAction(game, sequence, "reset");
    };
    while (!quit && !WindowShouldClose()) {
        // Scale the entire UI, including hit targets, instead of stretching
        // regions while leaving text tiny on a maximized/high-resolution window.
        ui::scale = std::clamp(std::min(GetScreenWidth() / 1280.0f, GetScreenHeight() / 800.0f), 1.0f, 2.5f);
        // Anything that changed since the end of the previous frame was written
        // by another process: celebrate it in the game view.
        const RenderSnapshot before = game.renderSnapshot();
        for (const auto& key : scene.observeExternal(last, before, game.room())) tracker.writes.insert(key);
        sceneReset = false; aimed = false;
        // A tap shorter than one frame (synthetic input, slow software GL) never
        // shows up in IsKeyPressed; raylib's press queue keeps it, in order.
        std::vector<int> queued;
        for (int key = GetKeyPressed(); key != 0; key = GetKeyPressed()) queued.push_back(key);
        const auto pressed = [&](int key) { return IsKeyPressed(key) || std::find(queued.begin(), queued.end(), key) != queued.end(); };
        // A chord's Ctrl can land one frame before its key and be released already.
        const bool ctrlNow = IsKeyDown(KEY_LEFT_CONTROL) || IsKeyDown(KEY_RIGHT_CONTROL) || pressed(KEY_LEFT_CONTROL) || pressed(KEY_RIGHT_CONTROL);
        const bool ctrl = ctrlNow || ctrlBefore;
        ctrlBefore = ctrlNow;
        const bool shift = IsKeyDown(KEY_LEFT_SHIFT) || IsKeyDown(KEY_RIGHT_SHIFT) || pressed(KEY_LEFT_SHIFT) || pressed(KEY_RIGHT_SHIFT);
        if (ctrl && pressed(KEY_Q)) quit = true;
        for (int i = 0; i < 12; ++i) if (pressed(KEY_F1 + i)) selectRoom(shift ? (i == 0 ? 13 : i == 11 ? 0 : i + 1) : i + 1);
        if (pressed(KEY_ESCAPE)) { menu = !menu; help = false; }
        if (pressed(KEY_SLASH) && !ctrl) help = !help;
        if (pressed(KEY_TAB) && !ctrl) { progress.drawer = !progress.drawer; dirty = true; }
        if (ctrl && pressed(KEY_R)) reset();
        // Hidden deterministic hooks for the acceptance harness; players use the world.
        if (ctrl && pressed(KEY_P)) { game.setPaused(!game.paused()); logAction(game, sequence, "pause"); }
        if (ctrl && !help) {
            const std::vector<std::pair<int, Action>> hooks{{KEY_F, Action::FireOnce}, {KEY_H, Action::TakeOneHit}, {KEY_J, Action::HitEnemy},
                {KEY_E, Action::EnemyFire}, {KEY_B, Action::ChargeOnce}, {KEY_D, Action::DrainOnce}, {KEY_W, Action::SwapWeapon},
                {KEY_T, Action::AdvanceTick}, {KEY_Y, Action::StartTrial}, {KEY_A, Action::ToggleAlarm},
                {KEY_N, Action::RebootRelay}};
            for (const auto& hook : hooks) if (pressed(hook.first)) perform(hook.second);
            if (pressed(KEY_O)) perform(game.room() == 2 ? Action::ActivateReactor : Action::EvaluateDoor);
        }
        const bool overlay = menu || help;
        Zone reach;
        const bool canUse = !overlay && game.interaction(reach);
        if (canUse && !ctrl && pressed(KEY_E)) perform(reach.action);
        const RenderSnapshot ready = game.renderSnapshot();
        const bool startStep = game.room() == 3 && (progress.completed[3] || progress.steps[3] + 1 >= static_cast<int>(lessons[3].steps.size()));
        const bool canStart = startStep && !overlay && !ready.trialRunning && ready.countdown <= 0 && (ready.fall == 0 || ready.downed);
        if (canStart && !ctrl && pressed(KEY_ENTER)) perform(Action::StartCountdown);
        const float screenW = GetScreenWidth() / ui::scale, screenH = GetScreenHeight() / ui::scale;
        const auto layout = Layout::compute(screenW, screenH, progress.drawer);
        const auto view = SceneView::fit(layout.viewport, layout.world);
        const auto pointer = mouse();
        const bool inWorld = !overlay && CheckCollisionPointRec(pointer, layout.world) && !previousHot;
        const bool armed = offered(Action::FireOnce);
        if (inWorld) aimWorld = view.toWorld(pointer);
        if (armed && inWorld && IsMouseButtonPressed(MOUSE_BUTTON_LEFT)) {
            game.setAim(aimWorld.x, aimWorld.y);
            perform(Action::FireOnce, true);
        }
        if (armed && !overlay && !ctrl && pressed(KEY_SPACE)) {
            if (inWorld) game.setAim(aimWorld.x, aimWorld.y); else game.clearAim();
            perform(Action::FireOnce, inWorld);
        }
        const auto move = !overlay && !ctrl ? movement() : Vector2{0, 0};
        game.update(GetFrameTime(), move.x, move.y);
        const RenderSnapshot snap = game.renderSnapshot();
        if (sceneReset) scene.reset(snap);
        else scene.observeInternal(before, snap, game.room(), aimWorld, aimed);
        if (snap.trialRunning && !last.trialRunning) goUntil = GetTime() + .9;
        if (snap.failSerial != lastFail) { lastFail = snap.failSerial; failUntil = GetTime() + 4; }
        last = snap;
        scene.update(GetFrameTime(), snap);
        const int room = game.room();
        const auto& lesson = lessons[room];
        const auto outcome = game.outcome();
        const auto teaching = game.teaching();
        const bool patched = teaching.ammoPatched || teaching.damagePatched || teaching.vaultPatched;
        auto& step = progress.steps[room];
        const int stepCount = static_cast<int>(lesson.steps.size());
        step = std::clamp(step, 0, stepCount);
        if (tracker.room != room || tracker.step != step) tracker.begin(room, step, snap, patched);
        if (patched) tracker.sawPatch = true;
        // Observable steps tick themselves off; a short delay lets the check be seen.
        if (step < stepCount && !lesson.steps[step].check.empty()) {
            if (tracker.satisfiedAt < 0 && tracker.satisfied(lesson.steps[step].check, snap, outcome, patched)) tracker.satisfiedAt = GetTime();
            if (tracker.satisfiedAt >= 0 && GetTime() - tracker.satisfiedAt > .7) { ++step; dirty = true; }
        }
        if (outcome.complete && step < stepCount) { step = stepCount; dirty = true; }
        // Only the room's real outcome finishes it; steps can't be skipped past the end.
        if (step >= stepCount && room != 0 && !outcome.complete && !progress.completed[room]) { step = stepCount - 1; }
        const bool finished = step >= stepCount && (room == 0 || outcome.complete || progress.completed[room]);
        if (finished && !progress.completed[room] && (room == 0 || outcome.complete)) {
            progress.completed[room] = true; dirty = true; completeUntil = GetTime() + 3;
        }
        if (dirty && GetTime() - lastSave > .6) { progress.save(); dirty = false; lastSave = GetTime(); }
        const float font = static_cast<float>(progress.textSize);
        Ui ui;
        BeginDrawing();
        ClearBackground(Background);
        scene.render(view, ui::scale, snap, room, aimWorld, inWorld && armed);
        BeginMode2D(Camera2D{{0, 0}, {0, 0}, 0, ui::scale});

        // Top bar.
        DrawRectangleRec(layout.top, Color{12, 18, 28, 255});
        DrawLineEx({0, layout.top.height - 1}, {screenW, layout.top.height - 1}, 1, Border);
        heading("RECLASS", 14, 9, 10, Teal);
        heading("BREAKOUT", 14, 21, 20, Ink);
        text("PID " + std::to_string(processId()) + "  |  ReClassBreakout  |  x64", 150, 18, 13, Muted);
        const float right = screenW - Layout::Margin;
        if (ui.button({right - 300, 9, 86, 32}, "Rooms", 14, menu, "Escape: room select")) { menu = !menu; help = false; }
        if (ui.button({right - 208, 9, 62, 32}, "Help", 14, help, "?: controls")) { help = !help; menu = false; }
        if (ui.button({right - 140, 9, 36, 32}, "A-", 14, false, "Smaller text")) { progress.textSize = std::max(13, progress.textSize - 1); dirty = true; }
        if (ui.button({right - 98, 9, 36, 32}, "A+", 14, false, "Larger text")) { progress.textSize = std::min(22, progress.textSize + 1); dirty = true; }
        if (ui.button({right - 56, 9, 56, 32}, "Quit", 13, false, "Ctrl+Q: save progress and quit")) quit = true;

        // Game view.
        scene.present(view, progress.crt);
        DrawRectangleRoundedLinesEx(layout.viewport, .01f, 4, 1.5f, Border);
        ui.blocked = overlay;
        {
            const auto& b = layout.banner;
            DrawRectangleGradientV(static_cast<int>(b.x), static_cast<int>(b.y), static_cast<int>(b.width), static_cast<int>(b.height), Color{6, 10, 18, 235}, Color{6, 10, 18, 120});
            heading((room < 10 ? "ROOM 0" : "ROOM ") + std::to_string(room) + "  -  " + lesson.chapter, b.x + 14, b.y + 9, 10, Gold);
            text(lesson.title, b.x + 14, b.y + 19, 18, Ink, Face::Bold);
            float pillX = b.x + b.width - 12;
            const auto pill = [&](const std::string& value, Color color) {
                const float width = textWidth(value, 12, Face::Bold) + 22;
                pillX -= width;
                DrawRectangleRounded({pillX, b.y + 10, width, 22}, .5f, 6, Fade(color, .18f));
                DrawRectangleRoundedLinesEx({pillX, b.y + 10, width, 22}, .5f, 6, 1, color);
                text(value, pillX + 11, b.y + 14, 12, color, Face::Bold);
                pillX -= 8;
            };
            if (progress.completed[room]) pill("COMPLETE", Teal);
            if (scene.edits()) pill("MEMORY EDITS " + std::to_string(scene.edits()), Violet);
            if (patched) pill("CODE PATCHED", Gold);
            if (game.paused()) pill("FROZEN (Ctrl+P)", Muted);
        }
        const float worldMid = layout.world.x + layout.world.width * .5f;
        if (GetTime() - scene.lastEdit().at < 3.0) {
            const float age = static_cast<float>(GetTime() - scene.lastEdit().at);
            const float alpha = std::min(1.0f, (3.0f - age) / .6f);
            const std::string title = "MEMORY WRITE DETECTED";
            const float width = std::max(textWidth(title, 16, Face::Bold), textWidth(scene.lastEdit().text, 13, Face::Mono)) + 40;
            const Rectangle box{worldMid - width * .5f, layout.world.y + 10, width, 52};
            DrawRectangleRounded(box, .2f, 6, Fade(Color{30, 18, 52, 255}, .92f * alpha));
            DrawRectangleRoundedLinesEx(box, .2f, 6, 1.5f, Fade(Violet, alpha));
            text(title, box.x + (box.width - textWidth(title, 16, Face::Bold)) * .5f, box.y + 7, 16, Fade(Violet, alpha), Face::Bold);
            text(scene.lastEdit().text, box.x + (box.width - textWidth(scene.lastEdit().text, 13, Face::Mono)) * .5f, box.y + 29, 13, Fade(Ink, alpha), Face::Mono);
        }
        const float worldCenterY = layout.world.y + layout.world.height * .45f;
        if (snap.countdown > 0) {
            const std::string count = std::to_string(static_cast<int>(std::ceil(snap.countdown)));
            const float pulse = 1 + .3f * (snap.countdown - std::floor(snap.countdown));
            text(count, worldMid - textWidth(count, 90 * pulse, Face::Bold) * .5f, worldCenterY - 45 * pulse, 90 * pulse, Gold, Face::Bold);
        } else if (GetTime() < goUntil) {
            text("GO!", worldMid - textWidth("GO!", 90, Face::Bold) * .5f, worldCenterY - 45, 90, Teal, Face::Bold);
        }
        if ((GetTime() < failUntil || snap.downed) && !snap.fail.empty() && snap.fall >= (room == 3 ? .5f : 0.0f)) {
            const float width = std::min(layout.world.width - 40, textWidth(snap.fail, 16, Face::Bold) + 40);
            const auto lines = wrap(snap.fail, width - 40, 16, Face::Bold);
            const Rectangle box{worldMid - width * .5f, worldCenterY + 50, width, static_cast<float>(lines.size()) * 24 + 20};
            DrawRectangleRounded(box, .2f, 6, Fade(Color{60, 16, 22, 255}, .92f));
            DrawRectangleRoundedLinesEx(box, .2f, 6, 1.5f, Red);
            paragraph(snap.fail, box.x + 20, box.y + 10, width - 40, 16, Ink, Face::Bold);
        }
        if (GetTime() < completeUntil) {
            const std::string done = "ROOM COMPLETE";
            text(done, worldMid - textWidth(done, 54, Face::Bold) * .5f, worldCenterY - 27, 54, Teal, Face::Bold);
        }
        if (canStart) {
            const std::string label = snap.downed ? "Try again  (Enter)" : snap.primary ? "Run again  (Enter)" : "Start run  (Enter)";
            const float width = 240;
            if (ui.button({worldMid - width * .5f, layout.world.y + layout.world.height - 58, width, 42}, label, 17, true, "Back to the start line, then 3-2-1-GO")) perform(Action::StartCountdown);
        }
        if (canUse) {
            const std::string prompt = reach.label;
            const float width = textWidth(prompt, 15, Face::Bold) + 58;
            const Rectangle box{worldMid - width * .5f, layout.world.y + layout.world.height - 40, width, 30};
            DrawRectangleRounded(box, .4f, 6, Fade(Color{10, 16, 26, 255}, .9f));
            DrawRectangleRoundedLinesEx(box, .4f, 6, 1.5f, Teal);
            DrawRectangleRounded({box.x + 6, box.y + 5, 22, 20}, .3f, 4, Teal);
            text("E", box.x + 12, box.y + 7, 15, Background, Face::Bold);
            text(prompt, box.x + 38, box.y + 7, 15, Ink, Face::Bold);
        }
        {
            // HUD: vitals and the room's gauge.
            const auto& h = layout.hud;
            DrawRectangleGradientV(static_cast<int>(h.x), static_cast<int>(h.y), static_cast<int>(h.width), static_cast<int>(h.height), Color{6, 10, 18, 160}, Color{6, 10, 18, 245});
            DrawLineEx({h.x, h.y}, {h.x + h.width, h.y}, 1, Fade(Teal, .35f));
            float x = h.x + 14;
            const float y = h.y + 17;
            text("HP", x, y, 12, Red, Face::Bold);
            bar({x + 24, y + 2, 120, 10}, snap.health / 100.0f, snap.health > 100 ? Gold : Red);
            text(std::to_string(snap.health), x + 150, y - 1, 14, snap.health > 100 ? Gold : Ink, Face::Bold);
            x += 195;
            text("AMMO", x, y, 12, Gold, Face::Bold);
            const int pips = static_cast<int>(std::clamp<long long>(snap.ammo, 0, 12));
            for (int i = 0; i < 12; ++i) DrawRectangleRounded({x + 42 + static_cast<float>(i) * 8, y, 5, 13}, .4f, 2, i < pips ? Gold : Border);
            text(std::to_string(snap.ammo) + (snap.ammo > 12 ? "!" : ""), x + 42 + 12 * 8 + 6, y - 1, 14, snap.ammo > 12 ? Violet : Ink, Face::Bold);
            x += 185;
            std::string gauge;
            if (room == 2) {
                text("CHARGE", x, y, 12, Blue, Face::Bold);
                // Segments, not a scaled bar: see the reactor gauge in scene.cpp.
                const int lit = static_cast<int>(std::clamp(snap.charge, 0.0, 100.0) / 5);
                for (int i = 0; i < 20; ++i)
                    DrawRectangle(static_cast<int>(x + 56 + static_cast<float>(i) * 7), static_cast<int>(y + 2), 5, 10, i < lit ? (snap.charge >= 90 ? Gold : Blue) : Border);
                DrawRectangle(static_cast<int>(x + 56 + 140 * .6f), static_cast<int>(y), 2, 14, Red);
                DrawRectangle(static_cast<int>(x + 56 + 140 * .9f), static_cast<int>(y), 2, 14, Gold);
            } else if (room == 3) {
                std::ostringstream trial; trial << std::fixed << std::setprecision(2) << "TIME " << snap.remainingTime << "s";
                gauge = trial.str();
            } else if (room == 5) gauge = snap.weaponName + "  DMG " + std::to_string(snap.weaponDamage);
            else if (room == 6) gauge = "RELAY LINKS " + std::to_string(std::min(3, snap.links)) + " / 3";
            else if (room == 1) gauge = "DRONES LEFT " + std::to_string(game.acceptance().targetsRemaining);
            if (!gauge.empty()) text(gauge, x, y - 1, 14, Blue, Face::Bold);
            if (ui.button({h.x + h.width - 132, h.y + 9, 120, 30}, "Restart room", 13, false, "Ctrl+R: reset this room's gameplay data. Patched code is not restored.")) reset();
            if (progress.completed[room] && room + 1 < Game::roomCount &&
                ui.button({h.x + h.width - 282, h.y + 9, 140, 30}, "Next room  ->", 13, true, lessons[room + 1].title)) selectRoom(room + 1);
        }

        // Drawer.
        const auto& d = layout.drawer;
        panel(d);
        if (!progress.drawer) {
            if (ui.button({d.x + 5, d.y + 8, d.width - 10, 30}, "<", 16, false, "Tab: show the steps")) { progress.drawer = true; dirty = true; }
            DrawTextPro(GetFontDefault(), "STEPS  -  TAB", {d.x + d.width * .5f + 5, d.y + 56}, {0, 0}, 90, 10, 1, Muted);
        } else {
            const std::vector<std::string> tabs{"STEPS", "MEMORY"};
            if (tab >= static_cast<int>(tabs.size())) tab = 0;
            const int picked = ui.tabs({d.x + 8, d.y + 6, d.width - 56, 34}, tabs, tab, 13);
            if (picked >= 0) tab = picked;
            if (ui.button({d.x + d.width - 42, d.y + 8, 32, 28}, ">", 15, false, "Tab: hide the drawer for a larger game view")) { progress.drawer = false; dirty = true; }
            // The steps tab keeps a fixed footer for the next room in every room.
            const bool nextFooter = tab == 0 && room + 1 < Game::roomCount;
            const Rectangle body{d.x + 14, d.y + 50, d.width - 22, d.height - (nextFooter ? 116.0f : 60.0f)};
            if (tab == 0) {
                ui.clip = body;
                tutorialScroll.begin(body, overlay);
                float ty = body.y - tutorialScroll.offset;
                const float w = body.width - 12;
                // Goal.
                const float goalTop = ty;
                ty = paragraph("GOAL", body.x + 12, ty + 10, w - 24, 11, Teal, Face::Bold);
                ty = rich(lesson.goal, body.x + 12, ty + 2, w - 24, font, Ink) + 6;
                DrawRectangleRoundedLinesEx({body.x, goalTop, w, ty - goalTop}, .05f, 4, 1, progress.completed[room] ? Teal : Border);
                ty += 12;
                if (finished) {
                    const bool hasNext = room + 1 < Game::roomCount;
                    const float cardTop = ty;
                    const float learnedEnd = rich(lesson.learned, body.x + 12, cardTop + 40, w - 24, font - 1, Ink, false);
                    const float cardBottom = learnedEnd + 8 + (hasNext ? 46 : 4);
                    DrawRectangleRounded({body.x, cardTop, w, cardBottom - cardTop}, .05f, 4, Color{18, 46, 41, 255});
                    DrawRectangleRoundedLinesEx({body.x, cardTop, w, cardBottom - cardTop}, .05f, 4, 1.5f, Teal);
                    checkmark(body.x + 12, cardTop + 14, 14, Teal);
                    text(room == 9 ? "YOU ESCAPED THE FACILITY" : "ROOM COMPLETE", body.x + 34, cardTop + 12, 16, Teal, Face::Bold);
                    rich(lesson.learned, body.x + 12, cardTop + 40, w - 24, font - 1, Ink);
                    if (hasNext && ui.button({body.x + 12, learnedEnd + 8, w - 24, 36}, "Next room: " + lessons[room + 1].title + "  ->", 15, true)) selectRoom(room + 1);
                    ty = cardBottom + 14;
                }
                // Checklist: done steps collapse, the current step expands, later steps dim.
                for (int i = 0; i < stepCount; ++i) {
                    const auto& current = lesson.steps[i];
                    const bool game = current.where == "game";
                    const Color tagColor = game ? Teal : Violet;
                    const std::string number = std::to_string(i + 1);
                    if (i < step) {
                        const Rectangle row{body.x, ty, w, 24};
                        if (ui.hovered(row)) {
                            DrawRectangleRounded(row, .3f, 4, Color{26, 38, 54, 255});
                            ui.tooltip = "Go back to this step";
                            if (IsMouseButtonReleased(MOUSE_BUTTON_LEFT)) { step = i; dirty = true; }
                        }
                        checkmark(body.x + 6, ty + 5, 12, Teal);
                        text(firstLine(current.text, w - 34, 13), body.x + 26, ty + 4, 13, Muted);
                        ty += 26;
                    } else if (i == step) {
                        const float top = ty + 4;
                        float cy = top + 12;
                        DrawCircleV({body.x + 22, cy + 10}, 12, tagColor);
                        text(number, body.x + 22 - textWidth(number, 13, Face::Bold) * .5f, cy + 3, 13, Background, Face::Bold);
                        const std::string tag = game ? "IN GAME" : "IN RECLASS";
                        const float tagW = textWidth(tag, 11, Face::Bold) + 16;
                        DrawRectangleRounded({body.x + 42, cy + 1, tagW, 18}, .5f, 4, Fade(tagColor, .2f));
                        text(tag, body.x + 50, cy + 4, 11, tagColor, Face::Bold);
                        cy = rich(current.text, body.x + 14, cy + 30, w - 28, font, Ink) + 6;
                        if (!current.why.empty()) cy = rich(current.why, body.x + 14, cy, w - 28, font - 2, Muted) + 4;
                        if (!current.see.empty()) {
                            text("YOU SHOULD SEE", body.x + 14, cy + 2, 10, Gold, Face::Bold);
                            cy = rich(current.see, body.x + 14, cy + 18, w - 28, font - 2, Gold) + 6;
                        }
                        if (current.loop > 0) {
                            // Narrowing rarely finishes in one round; make going around again obvious.
                            const std::string note = "Still more than a handful of results? Repeat steps " + std::to_string(current.loop) + "-" + number + ". Several rounds are normal.";
                            cy = paragraph(note, body.x + 14, cy, w - 28, 13, Gold) + 4;
                            if (ui.button({body.x + 14, cy, 190, 28}, "Repeat from step " + std::to_string(current.loop), 12, false, "Go back for another narrowing round")) {
                                step = current.loop - 1; tracker.room = -1; dirty = true;
                            }
                            cy += 38;
                        }
                        if (!current.check.empty()) {
                            const bool met = tracker.satisfiedAt >= 0;
                            if (met) { checkmark(body.x + 14, cy + 2, 14, Teal); text("Done!", body.x + 34, cy + 2, 13, Teal, Face::Bold); }
                            else {
                                const float pulse = .5f + .5f * std::sin(static_cast<float>(GetTime()) * 4);
                                DrawCircleV({body.x + 20, cy + 9}, 4, Fade(tagColor, .4f + .6f * pulse));
                                text(game ? "Checks itself when you do it" : "Checks itself when the game sees the change", body.x + 32, cy + 2, 12, Muted);
                                if (current.check != "complete" && ui.button({body.x + w - 70, cy - 2, 58, 24}, "Skip", 11, false, "Mark this step done yourself")) { ++step; dirty = true; }
                            }
                            cy += 30;
                        } else {
                            if (ui.button({body.x + 14, cy, 120, 32}, "Done", 14, true, "Go to the next step")) { ++step; dirty = true; }
                            cy += 42;
                        }
                        DrawRectangleRoundedLinesEx({body.x, top, w, cy - top}, .04f, 4, 1.5f, tagColor);
                        DrawRectangle(static_cast<int>(body.x), static_cast<int>(top + 6), 3, static_cast<int>(cy - top - 12), tagColor);
                        ty = cy + 10;
                    } else {
                        DrawCircleLinesV({body.x + 12, ty + 10}, 8, Border);
                        text(number, body.x + 12 - textWidth(number, 11) * .5f, ty + 4, 11, Muted);
                        text(firstLine(current.text, w - 34, 13), body.x + 28, ty + 3, 13, Fade(Muted, .6f));
                        ty += 26;
                    }
                }
                if (!lesson.restore.empty()) {
                    ty += 6;
                    text("BEFORE LEAVING", body.x, ty, 11, Gold, Face::Bold); ty += 18;
                    ty = rich(lesson.restore, body.x, ty, w, font - 1, Gold) + 8;
                }
                if (room >= 4) {
                    ty += 6;
                    if (ui.button({body.x, ty, w, 28}, recipeOpen ? "Hide: Find ROOKIE again" : "Lost your ROOKIE class? Find ROOKIE again", 12, recipeOpen)) recipeOpen = !recipeOpen;
                    ty += 36;
                    if (recipeOpen) ty = rich(FindRookieRecipe(), body.x, ty, w, font - 1, Muted) + 10;
                }
                ty = paragraph("A ReClass debugger pause freezes this whole window. Keep GUIDE.html open for those steps.", body.x, ty + 6, w, 12, Muted) + 10;
                if (ui.button({body.x, ty, 130, 26}, "Copy steps", 12, false, "Copy this room's steps as plain text")) { const auto copy = instructions(lesson); SetClipboardText(copy.c_str()); notify("Steps copied."); }
                ty += 34;
                if (!progress.diagnostic.empty()) ty = paragraph(progress.diagnostic, body.x, ty, w, 12, Gold) + 10;
                tutorialScroll.end(body, ty);
                ui.clip = {0, 0, screenW, screenH};
                if (nextFooter) {
                    const float footY = d.y + d.height - 56;
                    DrawLineEx({d.x + 12, footY - 8}, {d.x + d.width - 12, footY - 8}, 1, Border);
                    const bool ready = progress.completed[room];
                    const std::string label = ready ? "Next room: " + lessons[room + 1].title + "  ->" : "Next room: complete this room to unlock";
                    if (ui.button({d.x + 12, footY, d.width - 24, 42}, label, 15, ready,
                                  ready ? "Go to room " + std::to_string(room + 1) : "The game unlocks " + lessons[room + 1].title + " once it sees this room's goal met", ready))
                        selectRoom(room + 1);
                }
            } else {
                const float half = (body.width - 18) * .5f;
                if (ui.button({body.x, body.y, half, 30}, "Reveal address", 12, progress.addresses, "Reveal current address, field type and pointer path")) { progress.addresses = !progress.addresses; dirty = true; }
                if (ui.button({body.x + half + 8, body.y, half, 30}, "Hex values", 12, progress.hex, "Compare raw field bytes with the typed value")) { progress.hex = !progress.hex; dirty = true; }
                const Rectangle fieldsBody{body.x, body.y + 42, body.width, body.height - 42};
                ui.clip = fieldsBody;
                fieldScroll.begin(fieldsBody, overlay);
                float fy = fieldsBody.y - fieldScroll.offset;
                fy = paragraph("Answer key for checking your work. Every step can be done with ReClass alone.", fieldsBody.x, fy, fieldsBody.width - 12, 12, Muted) + 12;
                auto visibleFields = game.fields();
                const auto priority = memoryPriority(room);
                const auto rank = [&](const FieldSnapshot& field) { return std::find(priority.begin(), priority.end(), field.label) - priority.begin(); };
                std::stable_sort(visibleFields.begin(), visibleFields.end(), [&](const FieldSnapshot& a, const FieldSnapshot& b) { return rank(a) < rank(b); });
                for (const auto& field : visibleFields) {
                    const float start = fy;
                    fy = paragraph(field.label, fieldsBody.x, fy, fieldsBody.width - 62, font - 1, field.valid ? Ink : Red, Face::Bold);
                    const std::string copy = field.roundTrip.empty() ? field.value : field.roundTrip;
                    if (ui.button({fieldsBody.x + fieldsBody.width - 54, start, 44, 22}, "Copy", 11, false, "Copy exact round-trip value: " + copy)) { SetClipboardText(copy.c_str()); notify("Copied " + field.label + " = " + copy); }
                    fy = paragraph((field.type == "float32" ? copy : field.value) + "   " + field.type, fieldsBody.x, fy + 3, fieldsBody.width - 12, font, field.valid ? Gold : Red, Face::Mono);
                    if (progress.hex && !field.rawHex.empty()) fy = paragraph("Raw: " + field.rawHex, fieldsBody.x, fy + 3, fieldsBody.width - 12, 12, Blue, Face::Mono);
                    if (progress.addresses) {
                        const std::string address = hexAddress(field.address);
                        const float addressTop = fy + 3;
                        fy = paragraph(address, fieldsBody.x, addressTop, fieldsBody.width - 62, 13, Teal, Face::Mono);
                        if (ui.button({fieldsBody.x + fieldsBody.width - 54, addressTop, 44, 20}, "Copy", 11, false, "Copy current absolute field address")) { SetClipboardText(address.c_str()); notify("Copied address for " + field.label); }
                        fy = paragraph(field.path, fieldsBody.x, fy + 2, fieldsBody.width - 12, 12, Muted);
                        if (field.label == "Module root")
                            fy = paragraph("Module: " + moduleReference(field.address), fieldsBody.x, fy + 3, fieldsBody.width - 12, 12, Blue);
                    }
                    fy += 8;
                    DrawLineEx({fieldsBody.x, fy}, {fieldsBody.x + fieldsBody.width - 12, fy}, 1, Border); fy += 10;
                }
                if (patched) {
                    fy = paragraph("CODE BYTES DIFFER FROM STARTUP", fieldsBody.x, fy, fieldsBody.width - 12, 13, Gold) + 6;
                    fy = paragraph("Use Restore original or Restore all in ReClass. Restart room does not restore code.", fieldsBody.x, fy, fieldsBody.width - 12, 13, Ink) + 14;
                }
                if (progress.addresses) {
                    text("TEACHING SITES", fieldsBody.x, fy, 12, Teal, Face::Bold); fy += 22;
                    const std::vector<std::pair<std::string, std::uintptr_t>> sites{{"Module root pointer", teaching.worldRoot}, {"Ammo patch site", teaching.ammoSite}, {"Damage patch site", teaching.damageSite}, {"Vault entry", teaching.vaultEntry}, {"Vault endpoint", teaching.vaultEndpoint}, {"Stable signature", teaching.signature}, {"Badge compare", teaching.badgeSite}};
                    for (const auto& site : sites) {
                        fy = paragraph(site.first, fieldsBody.x, fy, fieldsBody.width - 12, 12, Muted);
                        const auto address = hexAddress(site.second);
                        const float addressTop = fy + 3;
                        fy = paragraph(address, fieldsBody.x, addressTop, fieldsBody.width - 62, 13, Teal, Face::Mono);
                        if (ui.button({fieldsBody.x + fieldsBody.width - 54, addressTop, 44, 20}, "Copy", 11, false, "Copy current teaching-site address")) { SetClipboardText(address.c_str()); notify("Copied " + site.first); }
                        const auto relative = moduleReference(site.second);
                        fy = paragraph("Module: " + relative, fieldsBody.x, fy + 3, fieldsBody.width - 12, 12, Blue);
                        if (ui.button({fieldsBody.x, fy + 3, fieldsBody.width - 12, 22}, "Copy module + offset", 11, false, "Copy " + relative, relative != "module offset unavailable")) { SetClipboardText(relative.c_str()); notify("Copied module offset for " + site.first); }
                        fy += 38;
                    }
                    if (!teaching.signaturePattern.empty()) fy = paragraph("Pattern: " + teaching.signaturePattern, fieldsBody.x, fy, fieldsBody.width - 12, 12, Blue) + 10;
                }
                fieldScroll.end(fieldsBody, fy);
                ui.clip = {0, 0, screenW, screenH};
            }
        }

        // Full-screen overlays.
        if (menu) {
            ui.blocked = false;
            const Rectangle surface{Layout::Margin, Layout::TopH, screenW - Layout::Margin * 2, screenH - Layout::TopH - Layout::Margin};
            DrawRectangleRec({0, Layout::TopH, screenW, screenH}, Fade(Background, .85f));
            panel(surface);
            heading("SELECT ROOM", surface.x + 20, surface.y + 18, 21, Ink);
            text("Each room teaches one ReClass technique and opens the next one when you complete it.", surface.x + 20, surface.y + 48, 13, Muted);
            const int cols = screenW >= 1100 ? 4 : 3;
            const float tileW = (surface.width - 40 - static_cast<float>(cols - 1) * 14) / static_cast<float>(cols);
            const Rectangle menuBody{surface.x + 20, surface.y + 78, surface.width - 40, surface.height - 140};
            menuScroll.begin(menuBody, false);
            float endY = menuBody.y;
            for (int i = 0; i < Game::roomCount; ++i) {
                const Rectangle tile{menuBody.x + static_cast<float>(i % cols) * (tileW + 14), menuBody.y + static_cast<float>(i / cols) * 104 - menuScroll.offset, tileW, 92};
                const bool open = unlocked(i);
                const bool hover = open && CheckCollisionPointRec(mouse(), tile) && CheckCollisionPointRec(mouse(), menuBody);
                DrawRectangleRounded(tile, .07f, 4, hover ? Color{38, 54, 73, 255} : open ? Inset : Color{10, 15, 23, 255});
                DrawRectangleRoundedLinesEx(tile, .07f, 4, i == room ? 2.0f : 1.0f, i == room ? Teal : progress.completed[i] ? Fade(Teal, .5f) : Border);
                heading((i < 10 ? "0" : "") + std::to_string(i), tile.x + 14, tile.y + 14, 20, progress.completed[i] ? Teal : open ? Gold : Border);
                text(lessons[i].title, tile.x + 56, tile.y + 13, 16, open ? Ink : Muted, Face::Bold);
                text(lessons[i].chapter, tile.x + 56, tile.y + 35, 12, Muted);
                if (!open) text("LOCKED - complete room " + std::to_string(i - 1), tile.x + 14, tile.y + 62, 11, Muted, Face::Bold);
                if (progress.completed[i]) { checkmark(tile.x + 14, tile.y + 62, 12, Teal); text("COMPLETE", tile.x + 32, tile.y + 62, 11, Teal, Face::Bold); }
                const std::string key = i == 0 ? "Shift+F12" : i == 13 ? "Shift+F1" : "F" + std::to_string(i);
                text(key, tile.x + tile.width - textWidth(key, 11) - 12, tile.y + 63, 11, Muted);
                if (hover && IsMouseButtonReleased(MOUSE_BUTTON_LEFT)) selectRoom(i);
                endY = std::max(endY, tile.y + tile.height + 12);
            }
            menuScroll.end(menuBody, endY);
            if (ui.button({surface.x + 20, surface.y + surface.height - 50, 210, 34}, "Back to the room", 14)) menu = false;
        } else if (help) {
            ui.blocked = false;
            const Rectangle surface{Layout::Margin, Layout::TopH, screenW - Layout::Margin * 2, screenH - Layout::TopH - Layout::Margin};
            DrawRectangleRec({0, Layout::TopH, screenW, screenH}, Fade(Background, .85f));
            panel(surface);
            heading("CONTROLS", surface.x + 20, surface.y + 20, 22, Teal);
            const Rectangle helpBody{surface.x + 20, surface.y + 61, surface.width - 40, surface.height - 124};
            helpScroll.begin(helpBody, false);
            float hy = helpBody.y - helpScroll.offset;
            hy = paragraph("WASD / arrows   move\nMouse + click / Space   aim and shoot\nE   use the console you're standing at\nTab   show or hide the steps\nEscape   room select (completed rooms and the next one)\nCtrl+R   restart the room (gameplay data only)\nCtrl+Q   quit    A- / A+   text size", helpBody.x, hy, helpBody.width, font, Ink) + 18;
            hy = paragraph("Attach ReClass to ReClassBreakout, PID " + std::to_string(processId()) + ".", helpBody.x, hy, helpBody.width, font, Ink) + 8;
            hy = paragraph(attachmentDiagnostic, helpBody.x, hy, helpBody.width, font - 1, Gold) + 12;
            hy = paragraph("Values changed from outside the game flash violet: MEMORY WRITE DETECTED. A ReClass debugger pause freezes this window until you Resume in ReClass. Restart room never restores patched code; use Restore original or Restore all in ReClass.", helpBody.x, hy, helpBody.width, font - 1, Muted) + 12;
            helpScroll.end(helpBody, hy);
            const float footer = surface.y + surface.height - 51;
            if (ui.button({surface.x + 20, footer, 160, 33}, "Back to room", 14)) help = false;
            if (ui.button({surface.x + 192, footer, 190, 33}, progress.crt ? "CRT effect: on" : "CRT effect: off", 13, progress.crt, "Scanlines, vignette and glitch on memory edits")) { progress.crt = !progress.crt; dirty = true; }
        }
        if (ui.tooltip != previousTooltip || IsMouseButtonPressed(MOUSE_BUTTON_LEFT)) { previousTooltip = ui.tooltip; tooltipSince = GetTime(); }
        if (!ui.tooltip.empty() && GetTime() - tooltipSince > .55) {
            const float tooltipW = std::min(screenW - 40, std::max(80.0f, textWidth(ui.tooltip, 13) + 24));
            const auto lines = wrap(ui.tooltip, tooltipW - 24, 13);
            const float tooltipH = static_cast<float>(lines.size()) * 18 + 16;
            const float tx = std::clamp(mouse().x + 14.0f, 12.0f, screenW - tooltipW - 12);
            const float tyy = std::clamp(mouse().y + 22.0f, 12.0f, screenH - tooltipH - 12);
            DrawRectangleRounded({tx, tyy, tooltipW, tooltipH}, .06f, 4, Color{37, 53, 73, 255});
            paragraph(ui.tooltip, tx + 12, tyy + 8, tooltipW - 24, 13, Ink);
        }
        if (GetTime() < toastUntil && !toast.empty()) {
            const float width = std::min(layout.viewport.width - 40, textWidth(toast, 13) + 28);
            const auto lines = wrap(toast, width - 24, 13);
            const float height = static_cast<float>(lines.size()) * 18 + 16;
            const Rectangle rect{layout.viewport.x + (layout.viewport.width - width) * .5f, layout.hud.y - height - 50, width, height};
            DrawRectangleRounded(rect, .1f, 4, Color{35, 76, 69, 240});
            paragraph(toast, rect.x + 12, rect.y + 8, width - 24, 13, Ink);
        }
        previousHot = ui.hot;
        EndMode2D();
        EndDrawing();
        if (captureFrame > 0 && --captureFrame == 0) { capture(captureTarget); quit = true; }
    }
    progress.room = game.room(); progress.save();
    scene.unload();
    unloadFonts();
    CloseWindow();
    return 0;
}
} // namespace breakout

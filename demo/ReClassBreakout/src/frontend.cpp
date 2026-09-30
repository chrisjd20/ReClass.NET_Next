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
    case Action::ToggleTurret: return "Ctrl+U";
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
              << " ticks=" << state.ticks << " shots=" << state.shots << " hits=" << state.hits
              << " enemy_shots=" << state.enemyShots << " targets=" << state.targetsRemaining
              << " swaps=" << state.weaponSwaps << " primary=" << state.outcome.primaryObserved
              << " restored=" << state.outcome.restorationObserved << " manual=" << state.outcome.manualAcknowledged
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
} // namespace


namespace {
// Screen regions, in logical UI units, recomputed each frame.
struct Layout {
    float screenW = 0, screenH = 0;
    Rectangle top{}, viewport{}, banner{}, world{}, hud{}, drawer{};
    static constexpr float TopH = 50, BannerH = 42, HudH = 116, Margin = 10, Rail = 40;
    static Layout compute(float screenW, float screenH, bool drawerOpen) {
        Layout l;
        l.screenW = screenW; l.screenH = screenH;
        const float drawerW = drawerOpen ? std::clamp(screenW * .3f, 320.0f, 480.0f) : Rail;
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
    case 1: case 8: case 12: return {"Player ammo", "Player health"};
    case 2: return {"Player charge", "Door decision"};
    case 3: return {"Player speed", "Player X", "Player Y", "Remaining time", "Corridor distance"};
    case 4: case 11: return {"Player keycard", "Player flags", "Player clearance", "Door decision"};
    case 5: return {"Player callsign", "Player clearance", "Door decision"};
    case 6: return {"Player equipped", "Player weapon name", "Player weapon damage", "Player projectile speed", "Player cooldown", "Player Inventory", "Player slots[0]", "Player slots[1]"};
    case 7: return {"Player health", "Player ammo"};
    case 9: return {"Player health", "Player clearance", "Player faction", "Enemy health", "Enemy faction"};
    case 10: return {"Player health", "Player ammo", "Player faction", "Enemy health", "Enemy ammo", "Enemy faction"};
    default: return {};
    }
}
void bar(Rectangle rect, float fraction, Color color, Color back = Border) {
    DrawRectangleRounded(rect, .5f, 4, back);
    if (std::isfinite(fraction) && fraction > 0)
        DrawRectangleRounded({rect.x, rect.y, rect.width * std::clamp(fraction, 0.0f, 1.0f), rect.height}, .5f, 4, color);
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
    SetWindowMinSize(960, 600);
    SetExitKey(KEY_NULL);
    SetTargetFPS(60);
    loadFonts();
    Scene scene;
    scene.load();
    identity(game);
    bool menu = false, help = false, quit = false, dirty = true, referenceExpanded = false, previousHot = false;
    int tab = 0;
    Scroll fieldScroll, tutorialScroll, menuScroll, helpScroll;
    std::uint64_t sequence = 0;
    double lastSave = GetTime(), toastUntil = 0;
    std::string toast, previousTooltip;
    double tooltipSince = 0;
    RenderSnapshot last = game.renderSnapshot();
    scene.reset(last);
    bool sceneReset = false, aimed = false, ctrlBefore = false;
    Vector2 aimWorld{0, 0};
    const auto notify = [&](const std::string& value) { toast = value; toastUntil = GetTime() + 3.5; };
    const auto selectRoom = [&](int room) {
        game.setRoom(room); progress.room = room; dirty = true;
        menu = false; referenceExpanded = false; sceneReset = true;
        fieldScroll = {}; tutorialScroll = {};
        logAction(game, sequence, "room");
        if (!lessons[room - 1].restoration.empty())
            notify("Before changing patched code, use Restore original / Restore all in ReClass.");
    };
    const auto perform = [&](Action action, bool withAim = false) {
        if (!withAim) game.clearAim();
        aimed = withAim;
        if (action == Action::AdvanceTick) { const auto move = movement(); game.advanceTick(move.x, move.y); }
        else game.perform(action);
        if (action == Action::EvaluateDoor || action == Action::ActivateReactor) scene.attempt(game.room(), game.outcome().primaryObserved);
        logAction(game, sequence, actionName(action));
    };
    const auto reset = [&]() {
        game.resetRoom(); sceneReset = true;
        notify("Gameplay data reset. Externally patched code stays patched: restore it in ReClass.");
        logAction(game, sequence, "reset");
    };
    // Developer screenshots: RECLASS_BREAKOUT_CAPTURE=file.ppm [RECLASS_BREAKOUT_CAPTURE_FRAME=N].
    const char* captureTarget = std::getenv("RECLASS_BREAKOUT_CAPTURE");
    int captureFrame = captureTarget && *captureTarget ? 90 : 0;
    if (const char* frames = std::getenv("RECLASS_BREAKOUT_CAPTURE_FRAME"); captureFrame && frames) captureFrame = std::max(1, std::atoi(frames));
    const auto togglePause = [&]() { game.setPaused(!game.paused()); logAction(game, sequence, "pause"); };
    while (!quit && !WindowShouldClose()) {
        // Scale the entire UI, including hit targets, instead of stretching
        // regions while leaving text tiny on a maximized/high-resolution window.
        ui::scale = std::clamp(std::min(GetScreenWidth() / 1280.0f, GetScreenHeight() / 800.0f), 1.0f, 2.5f);
        if (!IsWindowFocused()) game.focusLost();
        // Anything that changed since the end of the previous frame was written
        // by another process: celebrate it in the game view.
        const RenderSnapshot before = game.renderSnapshot();
        scene.observeExternal(last, before, game.room());
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
        if (ctrl && pressed(KEY_Q)) quit = true;
        for (int i = 0; i < Game::roomCount; ++i) if (pressed(KEY_F1 + i)) selectRoom(i + 1);
        if (pressed(KEY_ESCAPE)) { menu = !menu; help = false; game.setPaused(true); }
        if (pressed(KEY_SLASH) && !ctrl) help = !help;
        if (pressed(KEY_TAB) && !ctrl) { progress.drawer = !progress.drawer; dirty = true; }
        if (ctrl && pressed(KEY_R)) reset();
        if (ctrl && pressed(KEY_P)) togglePause();
        if (ctrl && !help) {
            if (pressed(KEY_F)) perform(Action::FireOnce);
            if (pressed(KEY_H)) perform(Action::TakeOneHit);
            if (pressed(KEY_J)) perform(Action::HitEnemy);
            if (pressed(KEY_E)) perform(Action::EnemyFire);
            if (pressed(KEY_B)) perform(Action::ChargeOnce);
            if (pressed(KEY_D)) perform(Action::DrainOnce);
            if (pressed(KEY_W)) perform(Action::SwapWeapon);
            if (pressed(KEY_O)) perform(game.room() == 2 ? Action::ActivateReactor : Action::EvaluateDoor);
            if (pressed(KEY_T)) perform(Action::AdvanceTick);
            if (pressed(KEY_L)) perform(Action::Reload);
            if (pressed(KEY_Y)) perform(Action::StartTrial);
            if (pressed(KEY_K)) perform(Action::ToggleKeycard);
            if (pressed(KEY_G)) perform(Action::TogglePower);
            if (pressed(KEY_A)) perform(Action::ToggleAlarm);
            if (pressed(KEY_U)) perform(Action::ToggleTurret);
            if (pressed(KEY_M)) {
                if (game.room() == 5) perform(Action::AcknowledgeProject);
                else if (game.room() == 11) perform(Action::AcknowledgeTrace);
                else if (game.room() == 12) perform(Action::AcknowledgeRestart);
            }
        }
        const float screenW = GetScreenWidth() / ui::scale, screenH = GetScreenHeight() / ui::scale;
        const auto layout = Layout::compute(screenW, screenH, progress.drawer);
        const auto view = SceneView::fit(layout.viewport, layout.world);
        const bool overlay = menu || help;
        const auto pointer = mouse();
        const bool inWorld = !overlay && CheckCollisionPointRec(pointer, layout.world) && !previousHot;
        if (inWorld) aimWorld = view.toWorld(pointer);
        if (inWorld && IsMouseButtonPressed(MOUSE_BUTTON_LEFT)) {
            game.setAim(aimWorld.x, aimWorld.y);
            perform(Action::FireOnce, true);
        }
        if (!overlay && !ctrl && pressed(KEY_SPACE)) {
            if (inWorld) game.setAim(aimWorld.x, aimWorld.y); else game.clearAim();
            perform(Action::FireOnce, inWorld);
        }
        const auto move = !overlay && !ctrl ? movement() : Vector2{0, 0};
        game.update(GetFrameTime(), move.x, move.y);
        const RenderSnapshot scene_ = game.renderSnapshot();
        if (sceneReset) scene.reset(scene_);
        else scene.observeInternal(before, scene_, game.room(), aimWorld, aimed);
        last = scene_;
        scene.update(GetFrameTime(), scene_);
        const auto outcome = game.outcome();
        if (outcome.complete && !progress.completed[game.room() - 1]) {
            progress.completed[game.room() - 1] = true; dirty = true;
            notify("Room complete! Outcome recorded. Continue freely or pick another room (Escape).");
        }
        if (dirty && GetTime() - lastSave > .6) { progress.save(); dirty = false; lastSave = GetTime(); }
        const auto& lesson = lessons[game.room() - 1];
        auto& step = progress.steps[game.room() - 1];
        step = std::clamp(step, 0, std::max(0, static_cast<int>(lesson.steps.size()) - 1));
        const float font = static_cast<float>(progress.textSize);
        const int room = game.room();
        Ui ui;
        BeginDrawing();
        ClearBackground(Background);
        scene.render(view, ui::scale, scene_, room, aimWorld, inWorld);
        BeginMode2D(Camera2D{{0, 0}, {0, 0}, 0, ui::scale});

        // Top bar.
        DrawRectangleRec(layout.top, Color{12, 18, 28, 255});
        DrawLineEx({0, layout.top.height - 1}, {screenW, layout.top.height - 1}, 1, Border);
        heading("RECLASS", 14, 9, 10, Teal);
        heading("BREAKOUT", 14, 21, 20, Ink);
        text("MEMORY TRAINING FACILITY  |  PID " + std::to_string(processId()) + "  |  x64", 150, 18, 13, Muted);
        const float right = screenW - Layout::Margin;
        if (ui.button({right - 300, 9, 86, 32}, "Rooms", 14, menu, "Escape: room select")) { menu = !menu; help = false; game.setPaused(true); }
        if (ui.button({right - 208, 9, 62, 32}, "Help", 14, help, "?: attachment help and keyboard shortcuts")) { help = !help; menu = false; }
        if (ui.button({right - 140, 9, 36, 32}, "A-", 14, false, "Decrease text size")) { progress.textSize = std::max(13, progress.textSize - 1); dirty = true; }
        if (ui.button({right - 98, 9, 36, 32}, "A+", 14, false, "Increase text size")) { progress.textSize = std::min(22, progress.textSize + 1); dirty = true; }
        if (ui.button({right - 56, 9, 56, 32}, "Quit", 13, false, "Ctrl+Q: save progress and close the process")) quit = true;

        // Game view.
        scene.present(view, progress.crt);
        DrawRectangleRoundedLinesEx(layout.viewport, .01f, 4, 1.5f, Border);
        ui.blocked = overlay;
        {
            const auto& b = layout.banner;
            DrawRectangleGradientV(static_cast<int>(b.x), static_cast<int>(b.y), static_cast<int>(b.width), static_cast<int>(b.height), Color{6, 10, 18, 235}, Color{6, 10, 18, 120});
            const std::string number = (room < 10 ? "ROOM 0" : "ROOM ") + std::to_string(room);
            heading(number, b.x + 14, b.y + 9, 10, Gold);
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
            pill(game.paused() ? "PAUSED" : "RUNNING", game.paused() ? Gold : Teal);
            if (progress.completed[room - 1]) pill(outcome.complete ? "COMPLETE" : "RECORDED", Teal);
            if (scene.edits()) pill("MEMORY EDITS " + std::to_string(scene.edits()), Violet);
            const auto teachingNow = game.teaching();
            if (teachingNow.ammoPatched || teachingNow.damagePatched || teachingNow.vaultPatched) pill("CODE PATCHED", Gold);
        }
        if (GetTime() - scene.lastEdit().at < 3.0) {
            const float age = static_cast<float>(GetTime() - scene.lastEdit().at);
            const float alpha = std::min(1.0f, (3.0f - age) / .6f);
            const std::string title = "MEMORY WRITE DETECTED";
            const float width = std::max(textWidth(title, 16, Face::Bold), textWidth(scene.lastEdit().text, 13, Face::Mono)) + 40;
            const Rectangle box{layout.world.x + (layout.world.width - width) * .5f, layout.world.y + 10, width, 52};
            DrawRectangleRounded(box, .2f, 6, Fade(Color{30, 18, 52, 255}, .92f * alpha));
            DrawRectangleRoundedLinesEx(box, .2f, 6, 1.5f, Fade(Violet, alpha));
            text(title, box.x + (box.width - textWidth(title, 16, Face::Bold)) * .5f, box.y + 7, 16, Fade(Violet, alpha), Face::Bold);
            text(scene.lastEdit().text, box.x + (box.width - textWidth(scene.lastEdit().text, 13, Face::Mono)) * .5f, box.y + 29, 13, Fade(Ink, alpha), Face::Mono);
        }
        if (game.paused() && !overlay) {
            const std::string hint = "SIMULATION PAUSED  -  memory stays live.  Ctrl+P or Resume to play.";
            const float width = textWidth(hint, 13, Face::Bold) + 28;
            const Rectangle box{layout.world.x + (layout.world.width - width) * .5f, layout.world.y + layout.world.height - 34, width, 26};
            DrawRectangleRounded(box, .5f, 6, Fade(Color{40, 32, 10, 255}, .85f));
            text(hint, box.x + 14, box.y + 6, 13, Gold, Face::Bold);
        }
        {
            // HUD: vitals, room gauge, status line and the action hotbar.
            const auto& h = layout.hud;
            DrawRectangleGradientV(static_cast<int>(h.x), static_cast<int>(h.y), static_cast<int>(h.width), static_cast<int>(h.height), Color{6, 10, 18, 150}, Color{6, 10, 18, 245});
            DrawLineEx({h.x, h.y}, {h.x + h.width, h.y}, 1, Fade(Teal, .35f));
            float x = h.x + 14;
            const float y = h.y + 10;
            text("HP", x, y, 12, Red, Face::Bold);
            bar({x + 24, y + 2, 130, 10}, scene_.health / 100.0f, scene_.health > 100 ? Gold : Red);
            text(std::to_string(scene_.health), x + 160, y - 1, 14, scene_.health > 100 ? Gold : Ink, Face::Bold);
            x += 205;
            text("AMMO", x, y, 12, Gold, Face::Bold);
            const int pips = std::clamp(scene_.ammo, 0, 12);
            for (int i = 0; i < 12; ++i) DrawRectangleRounded({x + 42 + static_cast<float>(i) * 8, y, 5, 13}, .4f, 2, i < pips ? Gold : Border);
            text(std::to_string(scene_.ammo) + (scene_.ammo > 12 ? "!" : ""), x + 42 + 12 * 8 + 6, y - 1, 14, scene_.ammo > 12 ? Violet : Ink, Face::Bold);
            x += 190;
            if (room == 2 || room == 10) {
                text("CHARGE", x, y, 12, Blue, Face::Bold);
                bar({x + 56, y + 2, 110, 10}, scene_.charge / 100, scene_.charge >= 90 ? Gold : Blue);
                x += 180;
            } else if (room == 3) {
                std::ostringstream trial;
                trial << std::fixed << std::setprecision(2) << "TIME " << scene_.remainingTime << "s   SPEED " << std::setprecision(1) << scene_.playerSpeed;
                text(trial.str(), x, y - 1, 13, Blue, Face::Bold);
                x += textWidth(trial.str(), 13, Face::Bold) + 20;
            } else if (room == 6 || room == 10) {
                const std::string weapon = scene_.weaponName + "  DMG " + std::to_string(scene_.weaponDamage);
                text(weapon, x, y - 1, 13, Blue, Face::Bold);
                x += textWidth(weapon, 13, Face::Bold) + 20;
            }
            const float statusX = std::max(x, h.x + h.width * .55f);
            const auto status = wrap(game.status(), h.x + h.width - statusX - 12, 12);
            for (std::size_t i = 0; i < std::min<std::size_t>(2, status.size()); ++i)
                text(status[i], statusX, y - 3 + static_cast<float>(i) * 16, 12, Muted);
            // Hotbar.
            std::vector<ActionDefinition> buttons;
            for (const auto& action : game.actions())
                if (action.available && action.action != Action::AdvanceTick) buttons.push_back(action);
            const float by = h.y + 50, bh = 50, gap = 8;
            const float fixed = 3;
            const float slots = static_cast<float>(buttons.size()) + fixed;
            const float bw = std::min(150.0f, (h.width - 28 - gap * (slots - 1) - 16) / slots);
            float bx = h.x + 14;
            const auto hotkey = [&](const std::string& labelText, const std::string& key, bool selected, const std::string& tip, Color accent) {
                const Rectangle rect{bx, by, bw, bh};
                const bool pressed = ui.button({rect.x, rect.y + 8, rect.width, rect.height - 8}, labelText, 13, selected, tip);
                const float chipW = textWidth(key, 11, Face::Bold) + 12;
                const Rectangle chip{rect.x + (rect.width - chipW) * .5f, rect.y, chipW, 16};
                DrawRectangleRounded(chip, .5f, 4, Color{12, 18, 28, 255});
                DrawRectangleRoundedLinesEx(chip, .5f, 4, 1, accent);
                text(key, chip.x + 6, chip.y + 2, 11, accent, Face::Bold);
                bx += bw + gap;
                return pressed;
            };
            if (hotkey(game.paused() ? "Resume" : "Pause", "Ctrl+P", !game.paused(), "Simulation only; the ReClass debugger pause freezes the whole window", Teal)) togglePause();
            if (hotkey("Tick +1", "Ctrl+T", false, "Advance exactly one fixed tick (hold movement to step)", Teal)) perform(Action::AdvanceTick);
            bx += 8;
            for (const auto& action : buttons) {
                std::string key = actionShortcut(action.action);
                if (key.size() > 8) key = key.substr(0, key.find(' '));
                if (hotkey(action.label, key, false, action.label + "  (" + actionShortcut(action.action) + ")", Gold)) perform(action.action);
            }
            bx = h.x + h.width - 14 - bw;
            if (hotkey("Reset room", "Ctrl+R", false, "Resets gameplay data only; patched code must be restored in ReClass", Red)) reset();
        }

        // Drawer.
        const auto& d = layout.drawer;
        panel(d);
        if (!progress.drawer) {
            if (ui.button({d.x + 5, d.y + 8, d.width - 10, 30}, "<", 16, false, "Tab: show the mission drawer")) { progress.drawer = true; dirty = true; }
            DrawTextPro(GetFontDefault(), "MISSION  -  TAB", {d.x + d.width * .5f + 5, d.y + 56}, {0, 0}, 90, 10, 1, Muted);
        } else {
            std::vector<std::string> tabs{"MISSION"};
            if (progress.readout) tabs.push_back("MEMORY");
            if (tab >= static_cast<int>(tabs.size())) tab = 0;
            const int picked = ui.tabs({d.x + 8, d.y + 6, d.width - 56, 34}, tabs, tab, 13);
            if (picked >= 0) tab = picked;
            if (ui.button({d.x + d.width - 42, d.y + 8, 32, 28}, ">", 15, false, "Tab: collapse the drawer for a larger game view")) { progress.drawer = false; dirty = true; }
            const Rectangle body{d.x + 14, d.y + 50, d.width - 22, d.height - (tab == 0 ? 146.0f : 60.0f)};
            if (tab == 0) {
                ui.clip = body;
                tutorialScroll.begin(body, overlay);
                float ty = body.y - tutorialScroll.offset;
                const float w = body.width - 12;
                // Objective card.
                const float cardTop = ty;
                ty = paragraph("OBJECTIVE", body.x + 12, ty + 10, w - 24, 11, Teal, Face::Bold);
                ty = paragraph(lesson.objective, body.x + 12, ty + 2, w - 24, font, Ink) + 4;
                ty = paragraph(outcome.detail, body.x + 12, ty, w - 24, 13, outcome.complete ? Teal : Gold) + 8;
                DrawRectangleRoundedLinesEx({body.x, cardTop, w, ty - cardTop}, .06f, 4, 1, outcome.complete ? Teal : Border);
                ty += 14;
                const auto conceptBreak = lesson.concept.find("\n\n");
                ty = paragraph(lesson.concept.substr(0, conceptBreak), body.x, ty, w, font - 1, Muted) + 16;
                if (!lesson.steps.empty()) {
                    const auto& current = lesson.steps[step];
                    const float stepTop = ty;
                    DrawRectangle(static_cast<int>(body.x), static_cast<int>(stepTop), 3, 20, Teal);
                    ty = paragraph("STEP " + std::to_string(step + 1) + " / " + std::to_string(lesson.steps.size()), body.x + 12, ty, w - 12, 12, Teal, Face::Bold) + 4;
                    ty = paragraph(current.title, body.x + 12, ty, w - 12, font + 2, Ink, Face::Bold) + 8;
                    ty = paragraph(current.text, body.x + 12, ty, w - 12, font, Ink) + 12;
                    text("EXPECTED", body.x + 12, ty, 11, Gold, Face::Bold); ty += 18;
                    ty = paragraph(current.expected, body.x + 12, ty, w - 12, font, Muted) + 14;
                    DrawRectangle(static_cast<int>(body.x), static_cast<int>(stepTop), 1, static_cast<int>(ty - stepTop - 8), Fade(Teal, .5f));
                }
                const float half = (w - 8) * .5f;
                if (ui.button({body.x, ty, half, 30}, progress.hints ? "Hide hints" : "Show hints", 13, progress.hints)) { progress.hints = !progress.hints; dirty = true; }
                if (ui.button({body.x + half + 8, ty, half, 30}, progress.solutions ? "Hide solution" : "Show solution", 13, progress.solutions)) { progress.solutions = !progress.solutions; dirty = true; }
                ty += 44;
                if (progress.hints) {
                    text("HINTS", body.x, ty, 11, Blue, Face::Bold); ty += 20;
                    for (const auto& hint : lesson.hints) ty = paragraph(hint, body.x, ty, w, font, Blue) + 10;
                    ty += 4;
                }
                if (progress.solutions) {
                    text("WORKED SOLUTION", body.x, ty, 11, Teal, Face::Bold); ty += 20;
                    for (const auto& solution : lesson.solutions) ty = paragraph(solution, body.x, ty, w, font, Ink) + 10;
                    ty += 4;
                }
                if (!lesson.restoration.empty()) {
                    text("RESTORATION", body.x, ty, 11, Gold, Face::Bold); ty += 20;
                    ty = paragraph(lesson.restoration, body.x, ty, w, font, Gold) + 12;
                }
                if (conceptBreak != std::string::npos) {
                    if (ui.button({body.x, ty, w, 30}, referenceExpanded ? "Hide controls and common mistakes" : "Controls and common mistakes", 13, referenceExpanded))
                        referenceExpanded = !referenceExpanded;
                    ty += 40;
                    if (referenceExpanded) ty = paragraph(lesson.concept.substr(conceptBreak + 2), body.x, ty, w, font, Muted) + 12;
                }
                if (ui.button({body.x, ty, w, 30}, progress.readout ? "Hide debug readout (Memory tab)" : "Show debug readout (Memory tab)", 12, progress.readout,
                              "Live values, addresses and teaching sites, for checking your work")) {
                    progress.readout = !progress.readout; dirty = true; if (progress.readout) tab = 1;
                }
                ty += 40;
                ty = paragraph("A ReClass debugger pause freezes this whole window. Keep the offline HTML or Markdown guide open for those steps.", body.x, ty, w, 12, Muted) + 10;
                if (!progress.diagnostic.empty()) ty = paragraph(progress.diagnostic, body.x, ty, w, 12, Gold) + 10;
                tutorialScroll.end(body, ty);
                ui.clip = {0, 0, screenW, screenH};
                const float nav = d.y + d.height - 88;
                DrawLineEx({d.x + 12, nav - 8}, {d.x + d.width - 12, nav - 8}, 1, Border);
                const float half2 = (d.width - 34) * .5f;
                if (ui.button({d.x + 12, nav, half2, 34}, "Back", 14, false, "Tutorial navigation never requires an outcome", step > 0)) { --step; tutorialScroll = {}; dirty = true; }
                if (ui.button({d.x + 22 + half2, nav, half2, 34}, "Next step", 14, false, "Tutorial navigation never requires an outcome", step + 1 < static_cast<int>(lesson.steps.size()))) { ++step; tutorialScroll = {}; dirty = true; }
                if (ui.button({d.x + 12, nav + 42, d.width - 24, 30}, "Copy instructions", 13, false, "Copy the complete lesson, hints, solution, and restoration text")) { const auto copy = instructions(lesson); SetClipboardText(copy.c_str()); notify("Complete room instructions copied."); }
            } else {
                const float half = (body.width - 18) * .5f;
                if (ui.button({body.x, body.y, half, 30}, "Reveal address", 12, progress.addresses, "Reveal current address, field type and pointer path")) { progress.addresses = !progress.addresses; dirty = true; }
                if (ui.button({body.x + half + 8, body.y, half, 30}, "Hex values", 12, progress.hex, "Compare raw field bytes with the typed value")) { progress.hex = !progress.hex; dirty = true; }
                const Rectangle fieldsBody{body.x, body.y + 42, body.width, body.height - 42};
                ui.clip = fieldsBody;
                fieldScroll.begin(fieldsBody, overlay);
                float fy = fieldsBody.y - fieldScroll.offset;
                fy = paragraph("Debug readout for checking your work. The lessons expect you to find these values with ReClass.", fieldsBody.x, fy, fieldsBody.width - 12, 12, Muted) + 12;
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
                const auto teaching = game.teaching();
                if (teaching.ammoPatched || teaching.damagePatched || teaching.vaultPatched) {
                    fy = paragraph("CODE BYTES DIFFER FROM STARTUP", fieldsBody.x, fy, fieldsBody.width - 12, 13, Gold) + 6;
                    fy = paragraph("Use Restore original or Restore all in ReClass. Reset room does not restore code.", fieldsBody.x, fy, fieldsBody.width - 12, 13, Ink) + 14;
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
                    fy = paragraph("Persistent definitions use module offsets or signatures; absolute addresses change with ASLR.", fieldsBody.x, fy, fieldsBody.width - 12, 12, Muted) + 10;
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
            text("Every room is available. Progress records observations and tutorial position, never memory edits or code patches.", surface.x + 20, surface.y + 48, 13, Muted);
            const int cols = screenW >= 1100 ? 4 : 3;
            const float tileW = (surface.width - 40 - static_cast<float>(cols - 1) * 14) / static_cast<float>(cols);
            const Rectangle body{surface.x + 20, surface.y + 78, surface.width - 40, surface.height - 150};
            menuScroll.begin(body, false);
            float endY = body.y;
            for (int i = 0; i < 12; ++i) {
                const Rectangle tile{body.x + static_cast<float>(i % cols) * (tileW + 14), body.y + static_cast<float>(i / cols) * 112 - menuScroll.offset, tileW, 100};
                const bool hover = CheckCollisionPointRec(mouse(), tile) && CheckCollisionPointRec(mouse(), body);
                DrawRectangleRounded(tile, .07f, 4, hover ? Color{38, 54, 73, 255} : Inset);
                DrawRectangleRoundedLinesEx(tile, .07f, 4, i + 1 == room ? 2.0f : 1.0f, i + 1 == room ? Teal : progress.completed[i] ? Fade(Teal, .5f) : Border);
                heading((i + 1 < 10 ? "0" : "") + std::to_string(i + 1), tile.x + 14, tile.y + 14, 20, progress.completed[i] ? Teal : Gold);
                paragraph(lessons[i].title, tile.x + 56, tile.y + 14, tile.width - 70, 16, Ink, Face::Bold);
                text(progress.completed[i] ? "COMPLETE" : "NOT YET CLEARED", tile.x + 14, tile.y + 72, 11, progress.completed[i] ? Teal : Muted, Face::Bold);
                text("F" + std::to_string(i + 1), tile.x + tile.width - 34, tile.y + 72, 11, Muted);
                if (hover && IsMouseButtonReleased(MOUSE_BUTTON_LEFT)) selectRoom(i + 1);
                endY = std::max(endY, tile.y + tile.height + 12);
            }
            menuScroll.end(body, endY);
            const float footer = surface.y + surface.height - 54;
            if (ui.button({surface.x + 20, footer, 210, 34}, "Return to current room", 14)) menu = false;
            paragraph("Before leaving a patching lesson, use Restore original / Restore all. Restarting the executable creates a fresh process.", surface.x + 245, footer, surface.width - 265, 13, Gold);
        } else if (help) {
            ui.blocked = false;
            const Rectangle surface{Layout::Margin, Layout::TopH, screenW - Layout::Margin * 2, screenH - Layout::TopH - Layout::Margin};
            DrawRectangleRec({0, Layout::TopH, screenW, screenH}, Fade(Background, .85f));
            panel(surface);
            heading("ATTACH, INSPECT, EXPERIMENT", surface.x + 20, surface.y + 20, 22, Teal);
            const Rectangle helpBody{surface.x + 20, surface.y + 61, surface.width - 40, surface.height - 124};
            helpScroll.begin(helpBody, false);
            float hy = helpBody.y - helpScroll.offset;
            hy = paragraph("Attach ReClass to ReClassBreakout (PID " + std::to_string(processId()) + "). Use the same platform's x64 executable. Enable the debug readout to see the live root pointer and teaching sites.", surface.x + 20, hy, surface.width - 40, font, Ink) + 12;
            hy = paragraph(attachmentDiagnostic, surface.x + 20, hy, surface.width - 40, font, Gold) + 15;
            hy = paragraph("Simulation pause keeps rendering and values live. ReClass debugger pause suspends the process and its window. After focus loss the simulation stays paused until you resume it. Values changed from outside the game flash violet as MEMORY WRITE DETECTED.", surface.x + 20, hy, surface.width - 40, font, Muted) + 20;
            const float col = (surface.width - 60) * .5f;
            text("MOVEMENT AND NAVIGATION", surface.x + 20, hy, 13, Teal);
            text("DETERMINISTIC ACTIONS", surface.x + 40 + col, hy, 13, Teal); hy += 27;
            const float leftEnd = paragraph("WASD / arrows: move\nMouse: aim; click / Space: fire\nTab: show or hide the mission drawer\nF1..F12: select any room\nCtrl+P: pause / resume simulation\nCtrl+T: advance exactly one tick (hold movement)\nCtrl+R: reset gameplay data\nCtrl+M: acknowledge the room's manual exercise\nEscape: room menu    ?: this help    Ctrl+Q: quit\nA- / A+: text size; preferences save locally", surface.x + 20, hy, col, font, Ink);
            const float rightY = paragraph("Ctrl+F: Fire once    Ctrl+L: Reload\nCtrl+H: Take one hit    Ctrl+J: Hit enemy\nCtrl+E: Enemy fire    Ctrl+W: Swap weapon\nCtrl+B: Recharge once    Ctrl+D: Drain once\nCtrl+Y: Start trial    Ctrl+U: Toggle turret\nCtrl+K: Keycard    Ctrl+G: Power    Ctrl+A: Alarm\nCtrl+O: Evaluate door / Activate reactor", surface.x + 40 + col, hy, col, font, Ink);
            const float rightEnd = paragraph("Data reset never restores patched code. Use Restore original / Restore all in ReClass. Saved definitions must match the platform and executable image; loading a definition does not apply it.", surface.x + 40 + col, rightY + 20, col, font, Gold);
            helpScroll.end(helpBody, std::max(leftEnd, rightEnd) + 14);
            const float footer = surface.y + surface.height - 51;
            if (ui.button({surface.x + 20, footer, 160, 33}, "Back to room", 14)) help = false;
            if (ui.button({surface.x + 192, footer, 190, 33}, progress.crt ? "CRT effect: on" : "CRT effect: off", 13, progress.crt, "Scanlines, vignette and glitch on memory edits")) { progress.crt = !progress.crt; dirty = true; }
            if (ui.button({surface.x + 394, footer, 220, 33}, progress.readout ? "Debug readout: on" : "Debug readout: off", 13, progress.readout, "Adds the Memory tab to the drawer")) { progress.readout = !progress.readout; dirty = true; }
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
            const Rectangle rect{layout.viewport.x + (layout.viewport.width - width) * .5f, layout.hud.y - height - 44, width, height};
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

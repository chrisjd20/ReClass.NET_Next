#include "scene.h"
#include "ui.h"
#include "rlgl.h"
#include "game.h"
#include <algorithm>
#include <cmath>
#include <iomanip>
#include <sstream>

namespace breakout {
namespace {
using namespace ui;
constexpr float WorldW = 800, WorldH = 400, Tile = 40;
const Color Dark{10, 14, 22, 255}, Void{5, 8, 14, 255};

const char* const CrtShader = R"(#version 330
in vec2 fragTexCoord;
in vec4 fragColor;
uniform sampler2D texture0;
uniform vec4 colDiffuse;
uniform vec2 resolution;
uniform float time;
uniform float glitch;
out vec4 finalColor;
float hash(float n) { return fract(sin(n) * 43758.5453); }
void main() {
    vec2 uv = fragTexCoord;
    float band = floor(uv.y * 48.0);
    uv.x += glitch * (hash(band + floor(time * 30.0)) - 0.5) * 0.035 * step(0.6, hash(band * 1.7 + floor(time * 12.0)));
    float split = 0.0004 + glitch * 0.006;
    vec3 color;
    color.r = texture(texture0, uv + vec2(split, 0.0)).r;
    color.g = texture(texture0, uv).g;
    color.b = texture(texture0, uv - vec2(split, 0.0)).b;
    vec2 centered = fragTexCoord - 0.5;
    float vignette = clamp(1.0 - dot(centered, centered) * 0.85, 0.0, 1.0);
    float scan = 0.94 + 0.06 * sin(fragTexCoord.y * resolution.y * 1.5708);
    finalColor = vec4(color * scan * vignette, 1.0) * colDiffuse * fragColor;
}
)";

Color mix(Color a, Color b, float t) {
    t = std::clamp(t, 0.0f, 1.0f);
    const auto lerp = [t](unsigned char x, unsigned char y) { return static_cast<unsigned char>(x + (y - x) * t); };
    return {lerp(a.r, b.r), lerp(a.g, b.g), lerp(a.b, b.b), lerp(a.a, b.a)};
}
std::string number(float value) {
    std::ostringstream out;
    out << std::setprecision(6) << value;
    return out.str();
}
const SceneObject* find(const RenderSnapshot& snapshot, SceneObject::Kind kind, const std::string& label = {}) {
    for (const auto& object : snapshot.objects)
        if (object.kind == kind && (label.empty() || object.label == label)) return &object;
    return nullptr;
}
void sprite(Texture2D texture, Vector2 center, float size, float rotation = 0, bool flip = false, Color tint = WHITE) {
    const Rectangle source{0, 0, flip ? -static_cast<float>(texture.width) : static_cast<float>(texture.width), static_cast<float>(texture.height)};
    DrawTexturePro(texture, source, {center.x, center.y, size, size}, {size * .5f, size * .5f}, rotation, tint);
}
void label(const std::string& value, Vector2 at, float size, Color color) {
    text(value, at.x - textWidth(value, size, Face::Bold) * .5f, at.y, size, color, Face::Bold);
}
void glow(Vector2 at, float radius, Color color) {
    BeginBlendMode(BLEND_ADDITIVE);
    for (int i = 4; i >= 1; --i) DrawCircleV(at, radius * static_cast<float>(i) / 4, Fade(color, .09f));
    EndBlendMode();
}
void light(Vector2 at, bool good, const std::string& caption, float time) {
    const Color color = good ? Teal : Red;
    DrawCircleV(at, 7, Dark);
    DrawCircleV(at, 5, good ? color : Fade(color, .55f + .45f * std::sin(time * 6)));
    glow(at, 16, color);
    label(caption, {at.x, at.y + 10}, 11, Muted);
}
// Draw a sealed wall column with a doorway gap between gapTop and gapBottom.
void partition(Texture2D wall, float x, float gapTop, float gapBottom) {
    for (float y = 0; y < WorldH; y += Tile) {
        const float top = std::max(y, y < gapTop ? y : gapBottom), bottom = std::min(y + Tile, y < gapTop ? gapTop : WorldH);
        if (bottom <= top) continue;
        DrawTexturePro(wall, {0, 0, 32, 32 * (bottom - top) / Tile}, {x - 22, top, 44, bottom - top}, {0, 0}, 0, WHITE);
    }
    DrawRectangle(static_cast<int>(x) - 24, 0, 2, static_cast<int>(gapTop), Color{90, 112, 140, 255});
    DrawRectangle(static_cast<int>(x) - 24, static_cast<int>(gapBottom), 2, static_cast<int>(WorldH - gapBottom), Color{90, 112, 140, 255});
    DrawRectangle(static_cast<int>(x) + 22, 0, 2, static_cast<int>(WorldH), Color{8, 12, 20, 255});
}
}

SceneView SceneView::fit(Rectangle viewport, Rectangle area) {
    SceneView view;
    view.viewport = viewport;
    // Keep one wall tile of margin visible around the 800x400 play area.
    view.worldScale = std::max(.1f, std::min(area.width / (WorldW + Tile * 2), area.height / (WorldH + Tile * 2)));
    view.origin = {area.x + (area.width - WorldW * view.worldScale) * .5f, area.y + (area.height - WorldH * view.worldScale) * .5f};
    return view;
}

void Scene::load() {
    sprites_.load();
    crt_ = LoadShaderFromMemory(nullptr, CrtShader);
    if (crt_.id && crt_.id != rlGetShaderIdDefault()) {
        crtTime_ = GetShaderLocation(crt_, "time");
        crtGlitch_ = GetShaderLocation(crt_, "glitch");
        crtResolution_ = GetShaderLocation(crt_, "resolution");
    }
}
void Scene::unload() {
    sprites_.unload();
    if (target_.id) UnloadRenderTexture(target_);
    if (crt_.id && crt_.id != rlGetShaderIdDefault()) UnloadShader(crt_);
}
void Scene::reset(const RenderSnapshot& snapshot) {
    particles_.clear(); tracers_.clear(); floaters_.clear();
    shake_ = glitch_ = flash_ = completeGlow_ = 0;
    doorOpen_ = snapshot.doorOpen ? 1.0f : 0.0f;
    lastPlayer_ = {snapshot.playerX, snapshot.playerY};
}

void Scene::burst(Vector2 at, Color color, int count, float speed, float size) {
    for (int i = 0; i < count; ++i) {
        const float angle = static_cast<float>(GetRandomValue(0, 628)) / 100.0f;
        const float velocity = speed * static_cast<float>(GetRandomValue(30, 100)) / 100.0f;
        const float life = .35f + static_cast<float>(GetRandomValue(0, 45)) / 100.0f;
        particles_.push_back({at, {std::cos(angle) * velocity, std::sin(angle) * velocity}, life, life, size, color});
    }
    if (particles_.size() > 1500) particles_.erase(particles_.begin(), particles_.begin() + static_cast<long>(particles_.size() - 1500));
}
void Scene::floater(Vector2 at, const std::string& value, Color color, bool glitch) {
    // Stack simultaneous messages instead of drawing them on top of each other.
    float offset = 0;
    for (const auto& existing : floaters_)
        if (existing.maximum - existing.life < .3f && std::fabs(existing.position.x - at.x) < 80) offset += 16;
    floaters_.push_back({{at.x, at.y - offset}, value, glitch ? 2.6f : 1.4f, glitch ? 2.6f : 1.4f, color, glitch});
}
void Scene::tracer(Vector2 from, Vector2 to, Color color, float width, float life) {
    tracers_.push_back({from, to, life, life, color, width});
}

std::vector<std::string> Scene::observeExternal(const RenderSnapshot& before, const RenderSnapshot& now, int room) {
    const Vector2 player{now.playerX, now.playerY - 34};
    std::vector<std::string> keys, changes;
    const auto changed = [&](const char* key, const std::string& name, const std::string& from, const std::string& to, Vector2 at) {
        keys.push_back(key);
        changes.push_back(name + " " + from + " -> " + to);
        floater(at, name + " " + from + " -> " + to, Violet, true);
        burst({at.x, at.y + 30}, Violet, 26, 140, 2.5f);
    };
    if (now.ammo != before.ammo) changed("ammo", "AMMO", std::to_string(before.ammo), std::to_string(now.ammo), player);
    if (now.health != before.health) changed("health", "HEALTH", std::to_string(before.health), std::to_string(now.health), player);
    // The reactor gauge has no number on purpose; the flash shows only that it changed.
    if (now.charge != before.charge) changed("charge", "CHARGE", room == 2 ? "?" : number(before.charge), room == 2 ? "!" : number(now.charge), room == 2 ? Vector2{640, 110} : player);
    if (now.playerSpeed != before.playerSpeed) changed("speed", "SPEED", number(before.playerSpeed), number(now.playerSpeed), player);
    if (now.keycard != before.keycard) changed("keycard", "KEYCARD", std::to_string(before.keycard), std::to_string(now.keycard), player);
    if (now.flags != before.flags) changed("flags", "FLAGS", std::to_string(before.flags), std::to_string(now.flags), player);
    if (now.clearance != before.clearance) changed("clearance", "CLEARANCE", std::to_string(before.clearance), std::to_string(now.clearance), player);
    if (now.callsign != before.callsign) changed("callsign", "CALLSIGN", before.callsign, now.callsign, player);
    if (now.weaponName != before.weaponName) changed("damage", "WEAPON", before.weaponName, now.weaponName, player);
    else if (now.weaponDamage != before.weaponDamage) changed("damage", "DAMAGE", std::to_string(before.weaponDamage), std::to_string(now.weaponDamage), player);
    if (now.enemyHealth != before.enemyHealth) if (const auto* enemy = find(now, SceneObject::Kind::Enemy))
        changed("enemy_health", "ENEMY HP", std::to_string(before.enemyHealth), std::to_string(now.enemyHealth), {enemy->x, enemy->y - 34});
    if (now.remainingTime != before.remainingTime) changed("time", "TIME", number(before.remainingTime), number(now.remainingTime), {365, 110});
    if (room == 6 && now.relayPower != before.relayPower) changed("relay", "RELAY POWER", number(before.relayPower), number(now.relayPower), {590, 120});
    if (std::hypot(now.playerX - before.playerX, now.playerY - before.playerY) > .01f) {
        burst({before.playerX, before.playerY}, Violet, 30, 90);
        changed("position", "POSITION", "(" + number(before.playerX) + ", " + number(before.playerY) + ")", "(" + number(now.playerX) + ", " + number(now.playerY) + ")", player);
    }
    if (now.doorOpen != before.doorOpen && room != 6) changed("door", "DOOR", std::to_string(before.doorOpen), std::to_string(now.doorOpen), {650, 90});
    if (changes.empty()) return keys;
    ++edits_;
    glitch_ = 1; flash_ = .35f; flashColor_ = Violet; shake_ = std::max(shake_, 4.0f);
    lastEdit_.text = changes.front() + (changes.size() > 1 ? "  (+" + std::to_string(changes.size() - 1) + " more)" : "");
    lastEdit_.at = GetTime();
    return keys;
}

void Scene::observeInternal(const RenderSnapshot& before, const RenderSnapshot& after, int room, Vector2 aim, bool aimed) {
    const Vector2 player{after.playerX, after.playerY};
    const Vector2 head{player.x, player.y - 30};
    if (after.shots > before.shots) {
        // Match the target whose health changed; the simulation already resolved the hit.
        Vector2 end = aimed ? aim : Vector2{player.x + (facingLeft_ ? -420.0f : 420.0f), player.y};
        const SceneObject* struck = nullptr;
        bool destroyed = false;
        for (std::size_t i = 0; i < after.objects.size() && i < before.objects.size(); ++i) {
            const auto& old = before.objects[i];
            const auto& now = after.objects[i];
            if (now.kind == SceneObject::Kind::Target && old.label == now.label && now.health < old.health) {
                struck = &now; destroyed = old.active && !now.active; break;
            }
        }
        if (!struck && !aimed)
            for (const auto& object : after.objects)
                if (object.kind == SceneObject::Kind::Target && object.active) { struck = &object; break; }
        if (struck) end = {struck->x, struck->y};
        else if (aimed) {
            const float dx = aim.x - player.x, dy = aim.y - player.y, length = std::max(1.0f, std::hypot(dx, dy));
            end = {player.x + dx / length * 900, player.y + dy / length * 900};
        }
        const float speed = std::clamp(after.projectileSpeed, 60.0f, 2000.0f);
        tracer(player, end, room == 5 && after.weaponDamage >= 20 ? Gold : Teal, 3, std::clamp(260.0f / speed * .22f, .08f, .5f));
        burst(player, Gold, 6, 90, 2);
        if (struck) {
            burst(end, destroyed ? Gold : Teal, destroyed ? 40 : 12, destroyed ? 220.0f : 120.0f, destroyed ? 3.5f : 2.5f);
            if (destroyed) { shake_ = std::max(shake_, 5.0f); flash_ = .12f; flashColor_ = Gold; }
            const int damage = static_cast<int>(std::max<long long>(0, after.weaponDamage));
            if (struck->health < 1000 && !destroyed) floater({end.x, end.y - 30}, "-" + std::to_string(damage), Gold);
            if (room == 5 && destroyed) floater({end.x, end.y - 30}, "ARMOR BREACHED", Gold);
        } else if (room == 5 && after.weaponDamage < 20) {
            const SceneObject* nearest = nullptr;
            for (const auto& object : after.objects)
                if (object.kind == SceneObject::Kind::Target && object.active) { nearest = &object; break; }
            if (nearest) floater({nearest->x, nearest->y - 40}, "DEFLECTED (damage " + std::to_string(after.weaponDamage) + " < 20)", Muted);
        }
        if (after.ammo > before.ammo) floater(head, "AMMO +" + std::to_string(after.ammo - before.ammo) + "?!", Gold);
        else if (after.ammo == before.ammo) floater(head, "AMMO HELD", Teal);
        if (after.health > before.health) {
            floater({head.x, head.y - 16}, "+" + std::to_string(after.health - before.health) + " HP SIPHONED", Teal);
            if (const auto* wall = find(after, SceneObject::Kind::Target)) tracer({wall->x, wall->y}, player, Teal, 6, .45f);
            burst(player, Teal, 24, 110);
        }
    }
    if (after.enemyShots > before.enemyShots) if (const auto* enemy = find(after, SceneObject::Kind::Enemy)) {
        const Vector2 from{enemy->x, enemy->y};
        tracer(from, {from.x - 500, from.y}, Red, 3);
        burst(from, Red, 8, 90, 2);
        if (after.enemyAmmo == before.enemyAmmo) floater({from.x, from.y - 36}, "ENEMY AMMO HELD", Muted);
        if (after.health > before.health) floater(head, "+" + std::to_string(after.health - before.health) + " HP (ENEMY SHOT!)", Red);
    }
    if (after.hits > before.hits) {
        Vector2 source{player.x + 200, player.y - 120};
        if (const auto* turret = find(after, SceneObject::Kind::Turret)) source = {turret->x, turret->y};
        if (room == 10) source = {400, 330};
        tracer(source, player, Red, 4, .22f);
        if (after.health < before.health) {
            floater(head, "-" + std::to_string(before.health - after.health) + " HP", Red);
            burst(player, Red, 18, 150);
            shake_ = std::max(shake_, 7.0f); flash_ = .18f; flashColor_ = Red;
        } else {
            floater(head, "NO DAMAGE", Teal);
            burst(player, Blue, 22, 120);
        }
    }
    if (after.enemyHits > before.enemyHits) if (const auto* enemy = find(after, SceneObject::Kind::Enemy)) {
        tracer({400, 330}, {enemy->x, enemy->y}, Red, 4, .22f);
        floater({enemy->x, enemy->y - 36}, after.enemyHealth < before.enemyHealth ? "-" + std::to_string(before.enemyHealth - after.enemyHealth) + " HP" : "NO DAMAGE", Red);
        burst({enemy->x, enemy->y}, Red, 16, 140);
    }
    if (after.swaps > before.swaps) {
        floater(head, "EQUIPPED " + after.weaponName, Blue);
        floater({head.x, head.y - 16}, "NEW WEAPON ADDRESS", Violet);
        burst(player, Blue, 20, 100);
    }
    if (after.trialRunning && !before.trialRunning) floater({365, 120}, "RUN!", Gold);
    if (!before.primary && after.primary) {
        completeGlow_ = 1.5f;
        burst(player, Teal, 60, 260, 3);
        flash_ = .25f; flashColor_ = Teal;
    }
}

void Scene::attempt(int room, bool accepted) {
    const Vector2 at = room == 2 ? Vector2{640, 110} : Vector2{650, 100};
    if (accepted) { floater(at, room == 2 ? "REACTOR ONLINE" : "ACCESS GRANTED", Teal); burst({at.x, at.y + 60}, Teal, 50, 220, 3); }
    else { floater(at, room == 2 ? "INSUFFICIENT CHARGE" : "ACCESS DENIED", Red); shake_ = std::max(shake_, 3.0f); }
}

void Scene::update(float dt, const RenderSnapshot& snapshot) {
    dt = std::clamp(dt, 0.0f, .1f);
    time_ += dt;
    for (auto& particle : particles_) {
        particle.life -= dt;
        particle.position.x += particle.velocity.x * dt; particle.position.y += particle.velocity.y * dt;
        particle.velocity.x *= 1 - 2.5f * dt; particle.velocity.y *= 1 - 2.5f * dt;
    }
    particles_.erase(std::remove_if(particles_.begin(), particles_.end(), [](const auto& p) { return p.life <= 0; }), particles_.end());
    for (auto& item : tracers_) item.life -= dt;
    tracers_.erase(std::remove_if(tracers_.begin(), tracers_.end(), [](const auto& t) { return t.life <= 0; }), tracers_.end());
    for (auto& item : floaters_) { item.life -= dt; item.position.y -= 18 * dt; }
    floaters_.erase(std::remove_if(floaters_.begin(), floaters_.end(), [](const auto& f) { return f.life <= 0; }), floaters_.end());
    shake_ = std::max(0.0f, shake_ - 30 * dt);
    glitch_ = std::max(0.0f, glitch_ - 1.6f * dt);
    flash_ = std::max(0.0f, flash_ - dt);
    completeGlow_ = std::max(0.0f, completeGlow_ - dt);
    doorOpen_ = std::clamp(doorOpen_ + (snapshot.doorOpen ? 2.5f : -2.5f) * dt, 0.0f, 1.0f);
    const Vector2 now{snapshot.playerX, snapshot.playerY};
    const float dx = now.x - lastPlayer_.x;
    moving_ = std::hypot(dx, now.y - lastPlayer_.y) > .05f && std::hypot(dx, now.y - lastPlayer_.y) < 60;
    if (std::fabs(dx) > .05f) facingLeft_ = dx < 0;
    if (moving_) walk_ += dt;
    lastPlayer_ = now;
}

void Scene::render(const SceneView& view, float uiScale, const RenderSnapshot& snapshot, int room, Vector2 aim, bool showAim) {
    const int width = std::max(1, static_cast<int>(std::ceil(view.viewport.width * uiScale)));
    const int height = std::max(1, static_cast<int>(std::ceil(view.viewport.height * uiScale)));
    if (!target_.id || target_.texture.width != width || target_.texture.height != height) {
        if (target_.id) UnloadRenderTexture(target_);
        target_ = LoadRenderTexture(width, height);
        SetTextureFilter(target_.texture, TEXTURE_FILTER_BILINEAR);
    }
    const Vector2 jitter{shake_ ? static_cast<float>(GetRandomValue(-100, 100)) / 100.0f * shake_ : 0, shake_ ? static_cast<float>(GetRandomValue(-100, 100)) / 100.0f * shake_ : 0};
    Camera2D camera{};
    camera.offset = {(view.origin.x - view.viewport.x) * uiScale + jitter.x, (view.origin.y - view.viewport.y) * uiScale + jitter.y};
    camera.zoom = view.worldScale * uiScale;
    BeginTextureMode(target_);
    ClearBackground(Void);
    BeginMode2D(camera);
    drawEnvironment(snapshot, room);
    drawObjects(snapshot, room);
    drawPlayer(snapshot, room, aim, showAim);
    drawEffects();
    EndMode2D();
    if (flash_ > 0) DrawRectangle(0, 0, width, height, Fade(flashColor_, std::min(.35f, flash_)));
    EndTextureMode();
}

void Scene::present(const SceneView& view, bool crt) {
    if (!target_.id) return;
    const bool shaded = crt && crt_.id && crt_.id != rlGetShaderIdDefault();
    if (shaded) {
        const float resolution[2]{static_cast<float>(target_.texture.width), static_cast<float>(target_.texture.height)};
        SetShaderValue(crt_, crtResolution_, resolution, SHADER_UNIFORM_VEC2);
        SetShaderValue(crt_, crtTime_, &time_, SHADER_UNIFORM_FLOAT);
        SetShaderValue(crt_, crtGlitch_, &glitch_, SHADER_UNIFORM_FLOAT);
        BeginShaderMode(crt_);
    }
    DrawTexturePro(target_.texture, {0, 0, static_cast<float>(target_.texture.width), -static_cast<float>(target_.texture.height)},
                   view.viewport, {0, 0}, 0, WHITE);
    if (shaded) EndShaderMode();
}

void Scene::drawEnvironment(const RenderSnapshot& snapshot, int room) {
    // Outer void detail, then floor, then the perimeter wall.
    for (float x = -400; x < WorldW + 400; x += 80)
        DrawLineEx({x, -300}, {x, WorldH + 300}, 1, Color{14, 20, 31, 255});
    for (float y = -Tile; y < WorldH + Tile; y += Tile)
        for (float x = -Tile; x < WorldW + Tile; x += Tile) {
            const bool edge = x < 0 || y < 0 || x >= WorldW || y >= WorldH;
            const int variant = (static_cast<int>(x / Tile) * 7 + static_cast<int>(y / Tile) * 3) & 3 ? 0 : 1;
            DrawTexturePro(edge ? sprites_.wall : sprites_.floor[variant], {0, 0, 32, 32}, {x, y, Tile, Tile}, {0, 0}, 0, WHITE);
        }
    DrawRectangleLinesEx({-2, -2, WorldW + 4, WorldH + 4}, 2, Color{58, 76, 100, 255});
    BeginBlendMode(BLEND_ADDITIVE);
    for (float x = 100; x < WorldW; x += 200) DrawCircleGradient(static_cast<int>(x), 200, 170, Color{40, 70, 110, 38}, Color{0, 0, 0, 0});
    EndBlendMode();

    // Pads and consoles. Pads act while you stand on them; consoles take E.
    for (const auto& zone : snapshot.zones) {
        const Rectangle r{zone.x, zone.y, zone.w, zone.h};
        if (zone.id == "turret") {
            DrawRectangleRec(r, Fade(Red, zone.occupied ? .10f : .05f));
            for (float x = r.x; x < r.x + r.width; x += 40) DrawLineEx({x, r.y}, {x + 20, r.y}, 3, Fade(Red, .5f));
            DrawRectangleLinesEx(r, 2, Fade(Red, .5f + .2f * std::sin(time_ * 4)));
            label("TURRET ZONE", {r.x + r.width * .5f, r.y + r.height - 22}, 12, Red);
            continue;
        }
        if (!zone.interact) {
            const Color color = zone.id == "charge" ? Blue : zone.id == "drain" || zone.id == "press" ? Red : Teal;
            DrawRectangleRounded(r, .15f, 4, Fade(color, zone.occupied ? .30f : .14f));
            DrawRectangleRoundedLinesEx(r, .15f, 4, 2, Fade(color, .6f + .4f * std::sin(time_ * 3)));
            if (zone.occupied) glow({r.x + r.width * .5f, r.y + r.height * .5f}, r.width * .7f, color);
            if (zone.id == "charge" || zone.id == "drain")
                for (int i = 0; i < 3; ++i) {
                    const float phase = std::fmod(time_ * (zone.occupied ? 1.6f : .5f) + static_cast<float>(i) / 3, 1.0f);
                    const float y = zone.id == "charge" ? r.y + r.height * (1 - phase) : r.y + r.height * phase;
                    DrawLineEx({r.x + 10, y}, {r.x + r.width - 10, y}, 2, Fade(color, 1 - phase));
                }
            label(zone.label, {r.x + r.width * .5f, r.y + r.height + 4}, 11, color);
            continue;
        }
        const Vector2 center{r.x + r.width * .5f, r.y + r.height * .5f};
        glow(center, std::max(r.width, r.height) * (zone.occupied ? .9f : .6f), Teal);
        DrawRectangleRounded(r, .2f, 4, Color{18, 34, 40, 255});
        DrawRectangleRoundedLinesEx(r, .2f, 4, 2, zone.occupied ? Ink : Teal);
        DrawRectangleRounded({r.x + 8, r.y + 8, r.width - 16, std::min(18.0f, r.height - 16)}, .3f, 4, Fade(Teal, .35f + .25f * std::sin(time_ * 5)));
    }

    switch (room) {
    case 0: {
        DrawRectangleRounded({250, 100, 300, 180}, .06f, 4, Dark);
        DrawRectangleRoundedLinesEx({250, 100, 300, 180}, .06f, 4, 2, Teal);
        label("TRAINING FACILITY", {400, 120}, 16, Teal);
        label("Attach ReClass to this process,", {400, 160}, 13, Ink);
        label("then pick a room (Escape).", {400, 182}, 13, Ink);
        label("WASD move  -  mouse aim  -  click shoot", {400, 222}, 11, Muted);
        label("E use consoles  -  Tab instructions", {400, 242}, 11, Muted);
        break;
    }
    case 1: {
        for (float x = 200; x < 760; x += Tile) DrawTexturePro(sprites_.hazard, {0, 0, 32, 32}, {x, 0, Tile, 10}, {0, 0}, 0, Fade(WHITE, .7f));
        label("DRONE BAY", {490, 14}, 12, Gold);
        break;
    }
    case 2: {
        for (float x = 690; x < WorldW; x += 14) DrawRectangle(static_cast<int>(x), 160, 8, 40, Color{40, 56, 78, 255});
        DrawRectangle(680, 172, 120, 16, Color{30, 44, 62, 255});
        DrawLineEx({290, 105}, {600, 170}, 3, Fade(Blue, .25f));
        break;
    }
    case 3: {
        // A narrow bridge over a pit; the floor collapses behind the timer.
        DrawRectangle(0, 0, static_cast<int>(WorldW), static_cast<int>(WorldH), Void);
        for (int i = 0; i < 90; ++i) {
            const float x = std::fmod(static_cast<float>(i * 97), WorldW), y = std::fmod(static_cast<float>(i * 53) + time_ * (8 + i % 5), WorldH);
            if (y < 140 || y > 220) DrawCircleV({x, y}, 1.2f, Color{40, 56, 80, 255});
        }
        // Everything behind the collapse front has fallen; it stays fallen until the next start.
        const float collapse = std::max(45.0f, snapshot.collapseX);
        for (float x = 45; x < 685; x += Tile) {
            const float fall = x + Tile <= collapse ? std::min(1.0f, (collapse - x - Tile) / 60) : 0;
            const float inset = fall * 14;
            DrawTexturePro(sprites_.floor[0], {0, 0, 32, 32}, {x + inset, 145 + inset + fall * 30, Tile - inset * 2, 70 - inset * 2}, {0, 0}, 0, Fade(WHITE, 1 - fall));
        }
        DrawRectangleLinesEx({collapse, 145, 685 - collapse, 70}, 2, Color{70, 96, 126, 255});
        // Start line.
        DrawTexturePro(sprites_.hazard, {0, 0, 32, 8}, {Game::SpawnX - 24, 141, 6, 78}, {0, 0}, 0, WHITE);
        label("START", {Game::SpawnX - 21, 124}, 11, Teal);
        if (snapshot.trialRunning && collapse > 45) {
            DrawRectangle(static_cast<int>(collapse) - 4, 145, 4, 70, Fade(Red, .7f + .3f * std::sin(time_ * 20)));
            glow({collapse, 180}, 40, Red);
            if (GetRandomValue(0, 3) == 0) burst({collapse, 180 + static_cast<float>(GetRandomValue(-30, 30))}, Color{110, 130, 160, 255}, 2, 40, 2);
        }
        std::ostringstream timer; timer << std::fixed << std::setprecision(2) << snapshot.remainingTime << "s";
        label(timer.str(), {365, 70}, 34, snapshot.trialRunning ? (snapshot.remainingTime < 3 ? Red : Gold) : Muted);
        label(snapshot.trialRunning ? "RUN!" : snapshot.primary ? "CROSSED" : snapshot.countdown > 0 ? "GET READY" : snapshot.fall > 0 ? "THE BRIDGE GAVE WAY" : "NOTHING MOVES UNTIL YOU START A RUN", {365, 110}, 12, snapshot.fall > 0 ? Red : Muted);
        break;
    }
    case 4: case 6: case 9: case 12: {
        // Door halves slide apart into the wall as the decision opens.
        const float open = doorOpen_ * 58;
        const Color doorColor = room == 12 ? Color{78, 64, 124, 255} : Color{70, 88, 112, 255};
        for (const float sign : {-1.0f, 1.0f}) {
            const Rectangle half{628, sign < 0 ? 120 - open : 180 + open, 44, 60};
            DrawRectangleRec(half, doorColor);
            DrawRectangleLinesEx(half, 2, Color{110, 132, 160, 255});
            DrawTexturePro(sprites_.hazard, {0, 0, 32, 8}, {half.x + 2, sign < 0 ? half.y + half.height - 8 : half.y, half.width - 4, 8}, {0, 0}, 0, WHITE);
        }
        partition(sprites_.wall, 650, 120, 240);
        glow({650, 180}, 60, doorOpen_ > .5f ? Teal : room == 12 ? Violet : Red);
        if (doorOpen_ > .5f) for (float x = 680; x < WorldW; x += 30) DrawCircleV({x, 180}, 4, Fade(Teal, .5f + .5f * std::sin(time_ * 5 - x * .05f)));
        DrawRectangleRounded({522, 40, 136, 70}, .12f, 4, Dark);
        DrawRectangleRoundedLinesEx({522, 40, 136, 70}, .12f, 4, 1, Border);
        if (room == 4) {
            light({548, 64}, snapshot.keycard == 1, "KEY", time_);
            light({590, 64}, (snapshot.flags & 1u) != 0, "PWR", time_);
            light({632, 64}, (snapshot.flags & 2u) == 0, "ALARM", time_);
            label("BLAST DOOR", {590, 95}, 11, Gold);
        } else if (room == 6) {
            const float power = std::clamp(static_cast<float>(snapshot.relayPower) / 100.0f, 0.0f, 1.0f);
            label("RELAY", {590, 48}, 11, Blue);
            DrawRectangle(540, 66, 100, 8, Color{24, 34, 48, 255});
            DrawRectangle(540, 66, static_cast<int>(100 * power), 8, power >= 1 ? Teal : Blue);
            label("LINKS " + std::to_string(std::min(3, snapshot.links)) + " / 3", {590, 84}, 11, snapshot.links >= 3 ? Teal : Gold);
        } else if (room == 9) {
            light({560, 64}, snapshot.callsign == "ENGINEER", "ID", time_);
            light({620, 64}, snapshot.clearance == 2, "CLR", time_);
            label("BIOMETRIC GATE", {590, 95}, 11, Gold);
            const float sweep = 150 + std::fmod(time_ * (snapshot.scanning ? 140.0f : 40.0f), 60);
            DrawLineEx({472, sweep}, {528, sweep}, 2, Fade(Teal, .8f));
            if (snapshot.scanning) label(snapshot.primary ? "ID ACCEPTED" : "SCANNING...", {500, 128}, 11, snapshot.primary ? Teal : Gold);
            DrawRectangleRounded({420, 250, 170, 60}, .1f, 4, Dark);
            DrawRectangleRoundedLinesEx({420, 250, 170, 60}, .1f, 4, 1, Teal);
            text("ID: " + snapshot.callsign, 430, 258, 12, Teal, Face::Mono);
            text("CLEARANCE: " + std::to_string(snapshot.clearance) + (snapshot.clearance == 2 ? " ENGINEER" : snapshot.clearance == 0 ? " VISITOR" : ""), 430, 280, 12, snapshot.clearance == 2 ? Teal : Gold, Face::Mono);
        } else {
            light({548, 64}, snapshot.keycard == 1, "KEY", time_);
            light({590, 64}, snapshot.clearance == 2, "CLR", time_);
            light({632, 64}, (snapshot.flags & 2u) == 0, "ALARM", time_);
            label("DECISION VAULT", {590, 95}, 11, Violet);
            DrawCircleLinesV({650, 180}, 36, Fade(Violet, .8f));
            const float dial = time_ * (doorOpen_ > .5f ? 0.0f : 1.4f);
            DrawLineEx({650, 180}, {650 + std::cos(dial) * 30, 180 + std::sin(dial) * 30}, 3, Violet);
        }
        break;
    }
    case 5:
        label("ARMORY", {160, 280}, 11, Blue);
        break;
    case 7:
        for (float x = 480; x < 780; x += 44) DrawRectangleRounded({x, 150, 36, 18}, .5f, 4, Color{80, 72, 52, 255});
        break;
    case 8: case 11: case 13: {
        DrawRectangleRounded({700, 110, 80, 140}, .08f, 4, Dark);
        label("AMMO", {740, 122}, 10, Muted);
        label(std::to_string(snapshot.ammo), {740, 140}, 30, snapshot.ammo > 12 ? Gold : Ink);
        if (room == 11) {
            label("HP", {740, 190}, 10, Muted);
            label(std::to_string(snapshot.health), {740, 206}, 22, Teal);
        }
        if (room == 13) label("RETURN VISIT", {740, 232}, 10, Blue);
        break;
    }
    case 10: {
        // Shared damage press over its plate: one routine, two cables.
        const Vector2 press{400, 330};
        DrawRectangleRounded({press.x - 60, press.y - 24, 120, 48}, .2f, 4, Fade(Color{44, 32, 44, 255}, .85f));
        DrawRectangleRoundedLinesEx({press.x - 60, press.y - 24, 120, 48}, .2f, 4, 1.5f, Red);
        label("PRESS", {press.x, press.y - 8}, 12, Red);
        DrawLineBezier({press.x - 60, press.y}, {snapshot.playerX, snapshot.playerY + 20}, 2, Fade(Red, .4f));
        if (const auto* enemy = find(snapshot, SceneObject::Kind::Enemy)) DrawLineBezier({press.x + 60, press.y}, {enemy->x, enemy->y + 20}, 2, Fade(Red, .4f));
        label(snapshot.primary ? "OVERRIDE UNLOCKED" : "OVERRIDE INPUT " + std::to_string(snapshot.clearance), {400, 112}, 12, snapshot.primary ? Teal : Gold);
        break;
    }
    default: break;
    }
}

void Scene::drawObjects(const RenderSnapshot& snapshot, int room) {
    for (std::size_t i = 0; i < snapshot.objects.size(); ++i) {
        const auto& object = snapshot.objects[i];
        const Vector2 at{object.x, object.y};
        switch (object.kind) {
        case SceneObject::Kind::Target:
            if (object.label == "Training wall") {
                DrawRectangleRounded({at.x - 26, at.y - 50, 52, 100}, .2f, 4, Color{24, 40, 58, 255});
                for (int ring = 3; ring >= 1; --ring) DrawCircleLinesV(at, static_cast<float>(ring) * 7, Fade(Blue, .8f));
                glow(at, 40, Blue);
                label("TRAINING WALL", {at.x, at.y + 54}, 11, Muted);
            } else if (room == 5) {
                const bool waiting = &object == &snapshot.objects.back() && snapshot.swaps == 0;
                const Color tint = waiting ? Fade(WHITE, .35f) : WHITE;
                if (object.active) {
                    sprite(sprites_.sentinel, at, 56, 0, at.x < snapshot.playerX, tint);
                    // Hexagonal armor shell.
                    DrawPolyLinesEx(at, 6, 40, time_ * 20, 2, Fade(snapshot.weaponDamage >= 20 ? Gold : Blue, waiting ? .3f : .8f));
                    const float health = std::clamp(static_cast<float>(object.health) / 30.0f, 0.0f, 1.0f);
                    DrawRectangle(static_cast<int>(at.x - 24), static_cast<int>(at.y - 46), 48, 5, Dark);
                    DrawRectangle(static_cast<int>(at.x - 24), static_cast<int>(at.y - 46), static_cast<int>(48 * health), 5, Red);
                } else {
                    DrawCircleV(at, 20, Color{30, 30, 36, 255});
                    DrawLineEx({at.x - 14, at.y - 14}, {at.x + 14, at.y + 14}, 3, Muted);
                }
                label(waiting ? "ARRIVES AFTER SWAP" : object.label, {at.x, at.y + 44}, 11, Muted);
            } else {
                if (object.active) {
                    const Vector2 bob{at.x, at.y + std::sin(time_ * 2.2f + static_cast<float>(i)) * 4};
                    glow(bob, 26, Gold);
                    sprite(sprites_.drone[static_cast<int>(time_ * 10 + static_cast<float>(i)) % 2], bob, 46);
                } else {
                    DrawCircleV(at, 10, Color{34, 34, 40, 255});
                    DrawLineEx({at.x - 8, at.y - 6}, {at.x + 8, at.y + 6}, 2, Color{80, 80, 90, 255});
                    DrawLineEx({at.x - 8, at.y + 6}, {at.x + 8, at.y - 6}, 2, Color{80, 80, 90, 255});
                }
            }
            break;
        case SceneObject::Kind::Enemy: {
            const bool facing = at.x > snapshot.playerX;
            glow(at, 34, Red);
            sprite(sprites_.sentinel, {at.x, at.y + std::sin(time_ * 3) * 2}, 52, 0, facing);
            const float health = std::clamp(static_cast<float>(object.health) / 100.0f, 0.0f, 1.0f);
            DrawRectangle(static_cast<int>(at.x - 24), static_cast<int>(at.y - 40), 48, 5, Dark);
            DrawRectangle(static_cast<int>(at.x - 24), static_cast<int>(at.y - 40), static_cast<int>(48 * health), 5, Red);
            label("SENTINEL  HP " + std::to_string(object.health), {at.x, at.y + 32}, 11, Red);
            break;
        }
        case SceneObject::Kind::Turret: {
            const float angle = std::atan2(snapshot.playerY - at.y, snapshot.playerX - at.x) * RAD2DEG + 90;
            if (object.active) glow(at, 40 + 6 * std::sin(time_ * 8), Red);
            sprite(sprites_.turret, at, 52, angle);
            label(object.active ? "ARMED" : "IDLE", {at.x, at.y + 30}, 11, object.active ? Red : Muted);
            break;
        }
        case SceneObject::Kind::Reactor: {
            const float charge = std::clamp(static_cast<float>(snapshot.charge) / 100.0f, 0.0f, 1.0f);
            const bool online = snapshot.primary;
            const Color core = online ? Color{255, 240, 200, 255} : mix(Blue, Gold, (charge - .4f) / .5f);
            // Fixed geometry, only colours follow the charge: drawn shapes that scale with the
            // charge would leave float vertex data that rises and falls with it (scan decoys).
            glow(at, 90, core);
            DrawCircleV(at, 34, Dark);
            DrawCircleV(at, 24, core);
            for (int ring = 0; ring < 3; ++ring) {
                const float spin = time_ * 60 * (ring % 2 ? -1 : 1) + static_cast<float>(ring) * 60;
                DrawRing(at, 38 + static_cast<float>(ring) * 6, 41 + static_cast<float>(ring) * 6, spin, spin + 110, 16, Fade(core, .6f));
            }
            // Segmented charge gauge with the normal cap and the reactor threshold marked.
            const int lit = static_cast<int>(charge * 40);
            for (int i = 0; i < 40; ++i) {
                const float from = -90 + static_cast<float>(i) * 9;
                const Color cell = i < lit ? (charge >= .9f ? Gold : Blue) : Color{24, 34, 48, 255};
                DrawRing(at, 62, 68, from + 1, from + 8, 2, cell);
            }
            for (const auto& mark : {std::pair<float, Color>{.6f, Red}, std::pair<float, Color>{.9f, Gold}}) {
                const float a = (-90 + 360 * mark.first) * DEG2RAD;
                DrawLineEx({at.x + std::cos(a) * 58, at.y + std::sin(a) * 58}, {at.x + std::cos(a) * 72, at.y + std::sin(a) * 72}, 3, mark.second);
            }
            label(online ? "ONLINE" : "REACTOR", {at.x, at.y + 78}, 12, online ? Gold : Blue);
            label("pad stops at the red mark; needs the gold mark", {at.x, at.y + 96}, 10, Muted);
            break;
        }
        case SceneObject::Kind::Exit: {
            glow(at, 50 + 6 * std::sin(time_ * 4), Teal);
            DrawCircleLinesV(at, 26, Teal);
            DrawCircleLinesV(at, 18 + 4 * std::sin(time_ * 4), Fade(Teal, .6f));
            label("EXIT", {at.x, at.y - 8}, 12, Teal);
            break;
        }
        case SceneObject::Kind::Door: break; // Drawn with the partition in drawEnvironment.
        }
    }
}

void Scene::drawPlayer(const RenderSnapshot& snapshot, int room, Vector2 aim, bool showAim) {
    if (!snapshot.playerValid) {
        label("INVALID PLAYER POINTER / POSITION", {WorldW * .5f, WorldH * .5f}, 16, Red);
        return;
    }
    if (snapshot.downed) return;
    if (snapshot.fall > 0) {
        // Tumble into the pit: spin, shrink and fade.
        const float t = snapshot.fall;
        const Vector2 at{snapshot.playerX, snapshot.playerY + 40 * t * t};
        sprite(sprites_.player[0], at, 48 * (1 - .85f * t), 220 * t, facingLeft_, Fade(WHITE, 1 - t));
        return;
    }
    const Vector2 at{snapshot.playerX, snapshot.playerY};
    DrawEllipse(static_cast<int>(at.x), static_cast<int>(at.y + 20), 16, 5, Fade(BLACK, .45f));
    glow(at, 30 + 8 * completeGlow_, Teal);
    const int frame = moving_ ? static_cast<int>(walk_ * 8) % 2 : 0;
    sprite(sprites_.player[frame], {at.x, at.y + (moving_ ? 0 : std::sin(time_ * 2.5f) * 1.2f)}, 48, 0, facingLeft_);
    if (showAim) {
        const float angle = std::atan2(aim.y - at.y, aim.x - at.x);
        DrawLineEx({at.x + std::cos(angle) * 14, at.y + std::sin(angle) * 14}, {at.x + std::cos(angle) * 28, at.y + std::sin(angle) * 28}, 4, Muted);
        DrawCircleLinesV(aim, 8, Teal);
        DrawLineEx({aim.x - 12, aim.y}, {aim.x - 5, aim.y}, 1.5f, Teal);
        DrawLineEx({aim.x + 5, aim.y}, {aim.x + 12, aim.y}, 1.5f, Teal);
        DrawLineEx({aim.x, aim.y - 12}, {aim.x, aim.y - 5}, 1.5f, Teal);
        DrawLineEx({aim.x, aim.y + 5}, {aim.x, aim.y + 12}, 1.5f, Teal);
    }
    label(snapshot.callsign.empty() ? "YOU" : snapshot.callsign, {at.x, at.y + 27}, 10, room == 9 && snapshot.callsign == "ENGINEER" ? Teal : Muted);
}

void Scene::drawEffects() {
    BeginBlendMode(BLEND_ADDITIVE);
    for (const auto& item : tracers_) {
        const float t = item.life / item.maximum;
        DrawLineEx(item.from, item.to, item.width * 3 * t, Fade(item.color, .25f * t));
        DrawLineEx(item.from, item.to, item.width * t, Fade(item.color, t));
    }
    for (const auto& particle : particles_)
        DrawCircleV(particle.position, particle.size * (particle.life / particle.maximum + .3f), Fade(particle.color, particle.life / particle.maximum));
    EndBlendMode();
    for (const auto& item : floaters_) {
        const float alpha = std::min(1.0f, item.life / .4f);
        const float size = item.glitch ? 13 : 12;
        const float width = textWidth(item.text, size, Face::Bold);
        Vector2 at{item.position.x - width * .5f, item.position.y};
        if (item.glitch) {
            const float jitter = item.maximum - item.life < .5f ? static_cast<float>(GetRandomValue(-3, 3)) : 0;
            DrawRectangleRounded({at.x - 6, at.y - 3, width + 12, size + 6}, .3f, 4, Fade(Dark, .8f * alpha));
            text(item.text, at.x + 1.5f + jitter, at.y, size, Fade(Red, .7f * alpha), Face::Bold);
            text(item.text, at.x - 1.5f - jitter, at.y, size, Fade(Teal, .7f * alpha), Face::Bold);
        }
        text(item.text, at.x + 1, at.y + 1, size, Fade(BLACK, .6f * alpha), Face::Bold);
        text(item.text, at.x, at.y, size, Fade(item.color, alpha), Face::Bold);
    }
}
} // namespace breakout

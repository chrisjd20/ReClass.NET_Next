#include "game.h"
#include <algorithm>
#include <cmath>
#include <cstring>
#include <iomanip>
#include <limits>
#include <random>
#include <sstream>

extern "C" { breakout::World* breakout_world_root = nullptr; }

namespace breakout {
namespace {
constexpr float NormalChargeLimit = 60.0f;
template<class T> T read(const T& value) { return *reinterpret_cast<const volatile T*>(&value); }
template<class T> void write(T& target, T value) { *reinterpret_cast<volatile T*>(&target) = value; }
std::string hex(std::uintptr_t value) { std::ostringstream s; s << "0x" << std::hex << std::uppercase << value; return s.str(); }
template<class T> std::string raw(const T& value) {
    const auto* bytes = reinterpret_cast<const volatile unsigned char*>(&value);
    std::ostringstream s; s << std::hex << std::uppercase << std::setfill('0');
    for (std::size_t i = 0; i < sizeof(T); ++i) { if (i) s << ' '; s << std::setw(2) << unsigned(bytes[i]); }
    return s.str();
}
std::string stringValue(const char* source, std::size_t size) {
    std::string result;
    const auto* bytes = reinterpret_cast<const volatile unsigned char*>(source);
    for (std::size_t i = 0; i < size && bytes[i]; ++i) {
        unsigned char c = bytes[i];
        if (c >= 32 && c < 127) result.push_back(char(c));
        else { result += "\\x"; std::ostringstream s; s << std::hex << std::setw(2) << std::setfill('0') << unsigned(c); result += s.str(); }
    }
    return result;
}
float renderFloat(float value, float fallback = 0) { return std::isfinite(value) ? value : fallback; }
const char* actionLabel(Action action) {
    switch (action) {
    case Action::FireOnce: return "Fire once";
    case Action::Reload: return "Reload to 12";
    case Action::ChargeOnce: return "Recharge once (+5)";
    case Action::DrainOnce: return "Drain once (-5)";
    case Action::ActivateReactor: return "Activate reactor";
    case Action::AdvanceTick: return "Advance one tick";
    case Action::ToggleKeycard: return "Toggle keycard";
    case Action::TogglePower: return "Toggle power bit";
    case Action::ToggleAlarm: return "Toggle alarm bit";
    case Action::EvaluateDoor: return "Evaluate door";
    case Action::SwapWeapon: return "Swap weapon";
    case Action::TakeOneHit: return "Take one hit";
    case Action::HitEnemy: return "Hit enemy";
    case Action::EnemyFire: return "Enemy fire once";
    case Action::StartTrial: return "Start trial";
    case Action::ToggleTurret: return "Toggle slow turret";
    case Action::AcknowledgeProject: return "Acknowledge saved project";
    case Action::AcknowledgeDiscovery: return "Acknowledge player-only event";
    case Action::AcknowledgeTrace: return "Acknowledge CSV export";
    case Action::AcknowledgeRestart: return "Acknowledge process restart";
    }
    return "Action";
}
}

Game::Game() {
    for (std::size_t i = 0; i < actors_.size(); ++i) {
        actors_[i] = std::make_unique<Actor>(); inventories_[i] = std::make_unique<Inventory>();
        weapons_.push_back(std::make_unique<Weapon>()); weapons_.push_back(std::make_unique<Weapon>());
    }
    std::memcpy(originalAmmo_.data(), breakout_ammo_patchsite, originalAmmo_.size());
    std::memcpy(originalDamage_.data(), breakout_damage_patchsite, originalDamage_.size());
    std::memcpy(originalVault_.data(), breakout_vault_entry, originalVault_.size());
    breakout_world_root = &world_;
    resetRoom();
}
Game::~Game() { if (breakout_world_root == &world_) breakout_world_root = nullptr; }
bool Game::ownsActor(const Actor* pointer) const {
    return pointer && std::any_of(actors_.begin(), actors_.end(), [pointer](const auto& object) { return pointer == object.get(); });
}
bool Game::ownsInventory(const Inventory* pointer) const {
    return pointer && std::any_of(inventories_.begin(), inventories_.end(), [pointer](const auto& object) { return pointer == object.get(); });
}
bool Game::ownsWeapon(const Weapon* pointer) const {
    return pointer && std::any_of(weapons_.begin(), weapons_.end(), [pointer](const auto& object) { return pointer == object.get(); });
}
Actor* Game::player() const {
    if (read(breakout_world_root) != &world_) return nullptr;
    Actor* pointer = read(world_.player); return ownsActor(pointer) ? pointer : nullptr;
}
Inventory* Game::inventory(const Actor* actor) const {
    if (!ownsActor(actor)) return nullptr;
    auto* pointer = read(actor->inventory); return ownsInventory(pointer) ? pointer : nullptr;
}
Weapon* Game::equipped(const Actor* actor) const {
    auto* inv = inventory(actor); if (!inv) return nullptr;
    auto* pointer = read(inv->equipped); return ownsWeapon(pointer) ? pointer : nullptr;
}
bool Game::validateActor(const Actor* actor) {
    if (!ownsActor(actor)) { status_ = "Action suspended: invalid Actor pointer. Inspect the raw pointer in Live values."; return false; }
    return true;
}
void Game::initializeWeapon(Weapon& weapon, const char* name, int damage, float speed, float cooldown) {
    weapon = {}; std::strncpy(weapon.name, name, sizeof(weapon.name)-1);
    write(weapon.damage, damage); write(weapon.projectileSpeed, speed); write(weapon.cooldown, cooldown);
}
void Game::setRoom(int room) { room_ = std::clamp(room, 1, roomCount); resetRoom(); }
void Game::resetRoom() {
    // Reset only owned gameplay allocations; no teaching-code bytes are written.
    world_ = {}; breakout_world_root = &world_; world_.player = actors_[0].get();
    world_.enemies[0] = actors_[1].get(); world_.enemies[1] = actors_[2].get();
    world_.room = room_; world_.remainingTime = 8; world_.corridorDistance = 580;
    for (std::size_t i = 0; i < actors_.size(); ++i) {
        auto& a = *actors_[i]; a = {}; a.health = 100; a.ammo = 12; a.speed = 80;
        a.charge = 40; a.x = i ? 550.0f : 70.0f; a.y = i ? 100.0f + 120.0f * float(i) : 180.0f;
        a.flags = AlarmFlag; a.faction = i ? EnemyFaction : PlayerFaction;
        std::strncpy(a.callsign, i ? "SENTINEL" : "ROOKIE", sizeof(a.callsign)-1);
        a.inventory = inventories_[i].get(); auto& inv = *inventories_[i]; inv = {};
        inv.slots[0] = weapons_[i*2].get(); inv.slots[1] = weapons_[i*2+1].get(); inv.equipped = inv.slots[0];
        initializeWeapon(*inv.slots[0], "PULSE", 5, 260, 0.35f);
        initializeWeapon(*inv.slots[1], "ARC", 7, 180, 0.6f);
    }
    if (room_ == 3) actors_[0]->speed = 60;
    if (room_ == 10) actors_[0]->health = 50;
    targets_.clear();
    if (room_ == 1) {
        for (int i = 0; i < 20; ++i) targets_.push_back({SceneObject::Kind::Target, 260.0f + float(i%5)*95, 65.0f + float(i/5)*85, 20, true, "Target " + std::to_string(i+1), 1});
    } else if (room_ == 6) {
        targets_.push_back({SceneObject::Kind::Target, 480, 140, 26, true, "Armor A", 30});
        targets_.push_back({SceneObject::Kind::Target, 650, 260, 26, true, "Armor B (after swap)", 30});
    } else if (room_ == 8 || room_ == 10 || room_ == 12) {
        targets_.push_back({SceneObject::Kind::Target, 620, 180, 24, true, "Training wall", 100000});
    }
    shots_ = hits_ = enemyHits_ = enemyShots_ = reloads_ = swaps_ = beforeSwap_ = afterSwap_ = unchangedHits_ = increasingShots_ = 0;
    paused_ = true; primary_ = restored_ = complete_ = manual_ = enemyHookVerified_ = turret_ = hasAim_ = overrideAccepted_ = false;
    static std::mt19937 codes{std::random_device{}()};
    std::uniform_int_distribution<std::uint32_t> code(1000, 9999);
    overrideCode_ = code(codes);
    do decoyCode_ = code(codes); while (decoyCode_ == overrideCode_);
    accumulator_ = 0; automaticTimer_ = 0; ticks_ = 0;
    status_ = "Room reset: gameplay data reset; any external code patches are unchanged. Simulation paused.";
}
std::vector<ActionDefinition> Game::actions() const {
    std::vector<Action> selected{Action::AdvanceTick};
    switch (room_) {
    case 1: selected.insert(selected.end(), {Action::FireOnce, Action::Reload}); break;
    case 2: selected.insert(selected.end(), {Action::ChargeOnce, Action::DrainOnce, Action::ActivateReactor}); break;
    case 3: selected.push_back(Action::StartTrial); break;
    case 4: selected.insert(selected.end(), {Action::ToggleKeycard, Action::TogglePower, Action::ToggleAlarm, Action::EvaluateDoor}); break;
    case 5: selected.insert(selected.end(), {Action::EvaluateDoor, Action::AcknowledgeProject}); break;
    case 6: selected.insert(selected.end(), {Action::FireOnce, Action::SwapWeapon}); break;
    case 7: selected.insert(selected.end(), {Action::TakeOneHit, Action::ToggleTurret}); break;
    case 8: selected.push_back(Action::FireOnce); break;
    case 9: selected.insert(selected.end(), {Action::TakeOneHit, Action::HitEnemy, Action::EvaluateDoor}); break;
    case 10: selected.insert(selected.end(), {Action::FireOnce, Action::EnemyFire}); break;
    case 11: selected.insert(selected.end(), {Action::EvaluateDoor, Action::ToggleKeycard, Action::ToggleAlarm, Action::AcknowledgeTrace}); break;
    case 12: selected.insert(selected.end(), {Action::FireOnce, Action::AcknowledgeRestart}); break;
    }
    std::vector<ActionDefinition> result;
    for (auto action : selected) {
        std::string label = actionLabel(action);
        if (room_ == 9 && action == Action::TakeOneHit) label = "Hit player";
        if (action == Action::ToggleKeycard) label = "Invalidate keycard";
        if (action == Action::TogglePower) label = "Cut power bit";
        if (action == Action::EvaluateDoor && room_ == 5) label = "Scan badge";
        if (action == Action::EvaluateDoor && room_ == 9) label = "Submit override";
        if (action == Action::EvaluateDoor && room_ == 11) label = "Try vault";
        result.push_back({action, label, true});
    }
    return result;
}
bool Game::perform(Action action) {
    const auto offered = actions();
    if (std::none_of(offered.begin(), offered.end(), [action](const auto& d) { return d.action == action && d.available; })) {
        status_ = "That action is unavailable in this room."; return false;
    }
    Actor* a = player();
    if (action == Action::AdvanceTick) { advanceTick(); return true; }
    if (!validateActor(a)) return false;
    bool result = true;
    switch (action) {
    case Action::FireOnce: result = fire(a, false); hasAim_ = false; break;
    case Action::Reload: write(a->ammo, std::int32_t(12)); ++reloads_; status_ = "Reloaded to 12. Reset room to start a fresh completion attempt."; break;
    case Action::ChargeOnce: case Action::DrainOnce: {
        float charge = read(a->charge);
        if (!std::isfinite(charge)) { status_ = "Charge action suspended: charge is non-finite (raw bytes shown)."; return false; }
        float next = charge + (action == Action::ChargeOnce ? 5.0f : -5.0f);
        if (!std::isfinite(next)) { status_ = "Charge action suspended: result would be non-finite."; return false; }
        // Normal controls cannot reach the reactor threshold. Keep externally
        // edited over-limit values usable for the lesson's 95 -> 100 check.
        if (action == Action::ChargeOnce && charge <= NormalChargeLimit) next = std::min(next, NormalChargeLimit);
        write(a->charge, next);
        status_ = action == Action::DrainOnce ? "Charge decreased by 5." :
            next == charge ? "Normal charging is capped at 60. Edit the charge float to reach 90." : "Charge increased by 5.";
        break;
    }
    case Action::ActivateReactor:
        if (!std::isfinite(read(a->charge))) { status_ = "Reactor suspended: non-finite charge."; return false; }
        primary_ = read(a->charge) >= 90; status_ = primary_ ? "Reactor activated." : "Reactor requires charge of at least 90."; break;
    case Action::ToggleKeycard: write(a->keycard, std::uint8_t(0)); status_ = "Keycard invalidated. Restore access by editing its byte in ReClass."; break;
    case Action::TogglePower: write(a->flags, read(a->flags) & ~PowerFlag); status_ = "Power bit cleared; other flag bits preserved. Restore power in ReClass."; break;
    case Action::ToggleAlarm: write(a->flags, read(a->flags) ^ AlarmFlag); status_ = "Alarm bit toggled; other flag bits preserved."; break;
    case Action::EvaluateDoor: result = evaluateDoor(); break;
    case Action::SwapWeapon: {
        auto* inv = inventory(a);
        if (!inv) { status_ = "Swap suspended: invalid Inventory pointer."; return false; }
        // Allocate before replacing, retaining the previous owned object for safe pointer inspection.
        auto replacement = std::make_unique<Weapon>();
        initializeWeapon(*replacement, swaps_ % 2 == 0 ? "ARC replacement" : "PULSE replacement", 7, 180, 0.6f);
        auto* pointer = replacement.get(); weapons_.push_back(std::move(replacement));
        write(inv->slots[(swaps_ + 1) % 2], pointer); write(inv->equipped, pointer); ++swaps_;
        status_ = "Equipped Weapon replaced. Reacquire its address through Inventory.equipped."; break;
    }
    case Action::TakeOneHit: result = hit(a); break;
    case Action::HitEnemy: {
        Actor* enemy = read(world_.enemies[0]); if (!validateActor(enemy)) return false;
        breakout_apply_damage(enemy, decoyCode_); ++enemyHits_; status_ = "Enemy hit once through the shared damage routine."; break;
    }
    case Action::EnemyFire: {
        Actor* enemy = read(world_.enemies[0]); if (!validateActor(enemy)) return false;
        result = fire(enemy, true); break;
    }
    case Action::StartTrial:
        if (!std::isfinite(read(a->speed))) { status_ = "Trial suspended: speed is non-finite."; return false; }
        write(a->x, 70.0f); write(a->y, 180.0f); write(world_.remainingTime, 8.0f);
        write(world_.corridorDistance, 580.0f); write(world_.trialRunning, std::uint8_t(1));
        primary_ = complete_ = false; status_ = "Trial started. Move right while running, or hold right and advance a tick."; break;
    case Action::ToggleTurret: turret_ = !turret_; status_ = turret_ ? "Slow turret enabled; it fires only while simulation advances." : "Slow turret disabled."; break;
    case Action::AcknowledgeProject: case Action::AcknowledgeDiscovery: case Action::AcknowledgeTrace: case Action::AcknowledgeRestart:
        manual_ = true; status_ = "Guided manual step acknowledged. Observations cannot prove which ReClass technique was used."; break;
    case Action::AdvanceTick: break;
    }
    updateCompletion(); return result;
}

bool Game::fire(Actor* actor, bool enemy) {
    if (!validateActor(actor)) return false;
    if (read(actor->ammo) <= 0) { status_ = "Magazine empty: edit ammo or reset the room."; return false; }
    auto* weapon = equipped(actor);
    if (!weapon) { status_ = "Fire suspended: invalid Inventory/equipped Weapon pointer (raw pointers shown)."; return false; }
    if (!std::isfinite(read(weapon->projectileSpeed)) || !std::isfinite(read(weapon->cooldown))) {
        status_ = "Fire suspended: Weapon projectile speed or cooldown is non-finite."; return false;
    }
    Actor* p = player();
    int oldAmmo = read(actor->ammo), oldHealth = read(actor->health), playerHealth = p ? read(p->health) : 0;
    breakout_decrement_ammo(actor);
    int newAmmo = read(actor->ammo), newHealth = read(actor->health);
    if (enemy) {
        ++enemyShots_;
        if (room_ == 10 && std::int64_t(newAmmo) == std::int64_t(oldAmmo)-1 && newHealth == oldHealth && p && read(p->health) == playerHealth) enemyHookVerified_ = true;
        status_ = "Enemy fired once through the same ammo routine; player health " + std::to_string(p ? read(p->health) : 0) + ".";
    } else {
        ++shots_;
        if (room_ == 8 || room_ == 12) {
            if (!primary_) {
                if (std::int64_t(newAmmo) == std::int64_t(oldAmmo)+1) ++increasingShots_; else increasingShots_ = 0;
                if (increasingShots_ >= 3) primary_ = true;
            } else if (std::int64_t(newAmmo) == std::int64_t(oldAmmo)-1) restored_ = true;
        }
        if (room_ == 10) {
            if (!primary_ && std::int64_t(newAmmo) == std::int64_t(oldAmmo)-1 && std::int64_t(newHealth) == std::int64_t(oldHealth)+5) primary_ = true;
            else if (primary_ && enemyHookVerified_ && std::int64_t(newAmmo) == std::int64_t(oldAmmo)-1 && newHealth == oldHealth) restored_ = true;
        }
        SceneObject* target = nullptr;
        if (!hasAim_) {
            for (auto& candidate : targets_) if (candidate.active && (room_ != 6 || (&candidate == &targets_[0] ? swaps_ == 0 : swaps_ > 0))) { target = &candidate; break; }
        } else if (p && std::isfinite(read(p->x)) && std::isfinite(read(p->y)) && std::isfinite(aimX_) && std::isfinite(aimY_)) {
            float px = read(p->x), py = read(p->y), dx = aimX_ - px, dy = aimY_ - py;
            float length = std::sqrt(dx*dx + dy*dy), best = std::numeric_limits<float>::max();
            if (length > 0.001f) for (auto& candidate : targets_) {
                if (!candidate.active || (room_ == 6 && (&candidate == &targets_[0] ? swaps_ != 0 : swaps_ == 0))) continue;
                float tx = candidate.x-px, ty = candidate.y-py;
                float along = (tx*dx + ty*dy)/length, across = std::fabs(tx*dy - ty*dx)/length;
                if (along >= 0 && across <= candidate.radius && along < best) { best = along; target = &candidate; }
            }
        }
        if (target) {
            int damage = read(weapon->damage);
            if (room_ != 6 || damage >= 20) {
                const std::int64_t remaining = std::int64_t(target->health) - std::max(0, damage);
                target->health = int(std::max<std::int64_t>(0, remaining));
                if (target->health == 0) { target->active = false; if (room_ == 6) { if (swaps_ == 0) ++beforeSwap_; else ++afterSwap_; } }
            }
        }
        status_ = "Fired exactly once: ammo " + std::to_string(oldAmmo) + " -> " + std::to_string(newAmmo) + ", health " + std::to_string(oldHealth) + " -> " + std::to_string(newHealth) + ".";
        if (room_ == 6 && target && read(weapon->damage) < 20) status_ += " Armor requires damage >= 20.";
    }
    updateCompletion(); return true;
}
bool Game::hit(Actor* actor) {
    if (!validateActor(actor)) return false;
    int before = read(actor->health); breakout_apply_damage(actor, overrideCode_); int after = read(actor->health); ++hits_;
    if (room_ == 7) {
        if (!primary_) { unchangedHits_ = after == before ? unchangedHits_+1 : 0; if (unchangedHits_ >= 3) primary_ = true; }
        else if (std::int64_t(after) == std::int64_t(before)-10) restored_ = true;
    }
    status_ = "One hit: health " + std::to_string(before) + " -> " + std::to_string(after) + ".";
    updateCompletion(); return true;
}
bool Game::evaluateDoor() {
    Actor* a = player(); if (!validateActor(a)) return false;
    bool accepted = false;
    if (room_ == 4) accepted = read(a->keycard) == 1 && (read(a->flags) & PowerFlag) != 0 && (read(a->flags) & AlarmFlag) == 0;
    else if (room_ == 5) accepted = breakout_scan_badge(a) != 0;
    else if (room_ == 9) {
        overrideAccepted_ = read(a->clearance) == overrideCode_;
        status_ = overrideAccepted_ ? "Override accepted: the damage press is unlocked." : "Override rejected: player clearance does not match the code the press carries for you.";
        updateCompletion(); return true;
    }
    else accepted = breakout_evaluate_vault(a) != 0;
    write(world_.doorOpen, std::uint8_t(accepted)); primary_ = accepted;
    status_ = accepted ? "Door accepted the current authoritative inputs." :
        room_ == 5 ? "Badge rejected. The scanner compares your callsign and clearance with values stored in its own code." :
        "Door denied entry. Inspect keycard, clearance, callsign and alarm inputs.";
    updateCompletion(); return true;
}
void Game::update(double elapsedSeconds, float movementX, float movementY) {
    if (paused_) { accumulator_ = 0; return; }
    // A debugger stop must never replay its elapsed wall time as gameplay.
    if (!std::isfinite(elapsedSeconds) || elapsedSeconds < 0 || elapsedSeconds > 0.25) { accumulator_ = 0; return; }
    accumulator_ += elapsedSeconds;
    int budget = 6;
    while (accumulator_ >= double(fixedStep) && budget-- > 0) { tick(movementX, movementY); accumulator_ -= double(fixedStep); }
    if (accumulator_ >= double(fixedStep)) accumulator_ = 0;
}
void Game::advanceTick(float movementX, float movementY) { tick(movementX, movementY); }
void Game::tick(float movementX, float movementY) {
    Actor* a = player(); if (!validateActor(a)) return;
    const float speed = read(a->speed), x = read(a->x), y = read(a->y);
    if (!std::isfinite(speed) || !std::isfinite(x) || !std::isfinite(y) || !std::isfinite(movementX) || !std::isfinite(movementY)) {
        status_ = "Tick suspended: non-finite speed, position or movement (raw field bytes shown)."; return;
    }
    const float norm = std::sqrt(movementX*movementX + movementY*movementY);
    if (norm > 1) { movementX /= norm; movementY /= norm; }
    const double nextX = double(x) + double(speed)*movementX*fixedStep;
    const double nextY = double(y) + double(speed)*movementY*fixedStep;
    if (!std::isfinite(nextX) || !std::isfinite(nextY)) { status_ = "Tick suspended: movement result is non-finite."; return; }
    if (room_ == 3 && read(world_.trialRunning)) {
        float time = read(world_.remainingTime);
        if (!std::isfinite(time)) { status_ = "Tick suspended: trial time is non-finite."; return; }
        if (time <= 0) { write(world_.trialRunning, std::uint8_t(0)); status_ = "Trial expired. Start trial to try again."; return; }
        // The trial bridge spans y 145..215.
        write(a->x, float(std::clamp(nextX, 30.0, 770.0))); write(a->y, float(std::clamp(nextY, 160.0, 200.0)));
        write(world_.remainingTime, std::max(0.0f, time-fixedStep));
        write(world_.corridorDistance, std::max(0.0f, 650.0f-read(a->x)));
        if (read(a->x) >= 650 && time >= fixedStep) { primary_ = true; write(world_.trialRunning, std::uint8_t(0)); status_ = "Reached the exit before time expired."; }
    } else {
        // A closed door is a wall; the door opens only from the authoritative decision byte.
        const bool sealed = (room_ == 4 || room_ == 5 || room_ == 11) && !read(world_.doorOpen);
        const double minX = sealed && x > 650 ? 690.0 : 30.0, maxX = sealed && x <= 650 ? 610.0 : 770.0;
        write(a->x, float(std::clamp(nextX, minX, maxX))); write(a->y, float(std::clamp(nextY, 30.0, 370.0)));
    }
    // The gate scanner reads the badge every tick while the player stands on its pad.
    if (room_ == 5 && onScanner(a)) breakout_scan_badge(a);
    ++ticks_; automaticTimer_ += fixedStep;
    if (automaticTimer_ >= 2) {
        automaticTimer_ -= 2;
        if (room_ == 7 && turret_) hit(a);
        if (room_ == 2) { float charge = read(a->charge); if (std::isfinite(charge) && charge < NormalChargeLimit) write(a->charge, std::min(NormalChargeLimit, charge+1.0f)); }
    }
    updateCompletion();
}
bool Game::onScanner(const Actor* actor) const {
    const float x = read(actor->x), y = read(actor->y);
    return x >= 470 && x <= 530 && y >= 150 && y <= 210;
}
void Game::updateCompletion() {
    switch (room_) {
    case 1: primary_ = reloads_ == 0 && std::none_of(targets_.begin(), targets_.end(), [](const auto& t) { return t.active; }); break;
    case 6: primary_ = beforeSwap_ > 0 && swaps_ > 0 && afterSwap_ > 0; break;
    case 9: primary_ = hits_ > 0 && enemyHits_ > 0 && overrideAccepted_; break;
    default: break;
    }
    if (room_ == 7 || room_ == 8) complete_ = primary_ && restored_;
    else if (room_ == 10) complete_ = primary_ && enemyHookVerified_ && restored_;
    else if (room_ == 12) complete_ = primary_ && restored_ && manual_;
    else if (room_ == 5 || room_ == 11) complete_ = primary_ && manual_;
    else complete_ = primary_;
}

RenderSnapshot Game::renderSnapshot() const {
    RenderSnapshot result;
    Actor* a = player(); result.playerValid = a != nullptr;
    if (a) {
        result.playerX = renderFloat(read(a->x), 70); result.playerY = renderFloat(read(a->y), 180);
        result.playerSpeed = renderFloat(read(a->speed)); result.charge = renderFloat(read(a->charge));
        result.health = read(a->health); result.ammo = read(a->ammo);
        result.playerValid = std::isfinite(read(a->x)) && std::isfinite(read(a->y));
        result.keycard = read(a->keycard); result.flags = read(a->flags); result.clearance = read(a->clearance);
        result.faction = read(a->faction); result.callsign = stringValue(a->callsign, sizeof(a->callsign));
        if (Weapon* weapon = equipped(a)) {
            result.weaponName = stringValue(weapon->name, sizeof(weapon->name));
            result.weaponDamage = read(weapon->damage); result.projectileSpeed = renderFloat(read(weapon->projectileSpeed), 260);
        }
    }
    if (Actor* enemy = read(world_.enemies[0]); ownsActor(enemy)) { result.enemyHealth = read(enemy->health); result.enemyAmmo = read(enemy->ammo); }
    result.shots = shots_; result.hits = hits_; result.enemyHits = enemyHits_; result.enemyShots = enemyShots_;
    result.swaps = swaps_; result.turret = turret_; result.primary = primary_; result.complete = complete_;
    result.scanning = room_ == 5 && a && onScanner(a);
    result.remainingTime = renderFloat(read(world_.remainingTime));
    result.corridorDistance = renderFloat(read(world_.corridorDistance));
    result.doorOpen = read(world_.doorOpen) != 0; result.trialRunning = read(world_.trialRunning) != 0;
    result.objects = targets_;
    if (room_ == 9 || room_ == 10) {
        Actor* enemy = read(world_.enemies[0]);
        if (ownsActor(enemy)) result.objects.push_back({SceneObject::Kind::Enemy, renderFloat(read(enemy->x), 550), renderFloat(read(enemy->y), 220), 24, true, "Enemy (faction " + std::to_string(read(enemy->faction)) + ")", read(enemy->health)});
    }
    if (room_ == 7) result.objects.push_back({SceneObject::Kind::Turret, 700, 90, 22, turret_, turret_ ? "Turret armed" : "Turret idle", 0});
    if (room_ == 2) result.objects.push_back({SceneObject::Kind::Reactor, 640, 180, 40, !primary_, "Reactor: requires charge 90", 0});
    if (room_ == 3) result.objects.push_back({SceneObject::Kind::Exit, 650, 180, 35, !primary_, "Trial exit", 0});
    if (room_ == 4 || room_ == 5 || room_ == 11) result.objects.push_back({SceneObject::Kind::Door, 650, 180, 40, !result.doorOpen, result.doorOpen ? "Door open" : "Door locked", 0});
    return result;
}
std::vector<FieldSnapshot> Game::fields() const {
    std::vector<FieldSnapshot> result;
    auto add = [&result](const std::string& label, const std::string& type, const auto& field, const std::string& path) {
        using T = std::decay_t<decltype(field)>;
        T value = read(field); std::ostringstream display, exact;
        bool valid = true;
        if constexpr (std::is_pointer_v<T>) { display << hex(reinterpret_cast<std::uintptr_t>(value)); exact << display.str(); }
        else if constexpr (std::is_floating_point_v<T>) {
            display << std::setprecision(7) << value;
            exact << std::setprecision(std::numeric_limits<T>::max_digits10) << value;
            valid = std::isfinite(value);
        } else { display << +value; exact << +value; }
        result.push_back({label, type, display.str(), exact.str(), raw(field), path, reinterpret_cast<std::uintptr_t>(&field), valid});
    };
    auto stringField = [&result](const std::string& label, const char* value, std::size_t size, const std::string& path) {
        std::string text = stringValue(value, size), bytes;
        const auto* source = reinterpret_cast<const volatile unsigned char*>(value);
        std::ostringstream s; s << std::hex << std::uppercase << std::setfill('0');
        for (std::size_t i=0; i<size; ++i) { if (i) s << ' '; s << std::setw(2) << unsigned(source[i]); }
        result.push_back({label, "char[" + std::to_string(size) + "]", text, text, s.str(), path, reinterpret_cast<std::uintptr_t>(value), true});
    };
    add("Module root", "World*", breakout_world_root, "module!breakout_world_root");
    result.back().valid = read(breakout_world_root) == &world_;
    add("World.player", "Actor*", world_.player, "breakout_world_root -> World.player (+0)");
    result.back().valid = ownsActor(read(world_.player));
    add("World.enemies[0]", "Actor*", world_.enemies[0], "breakout_world_root -> World.enemies[0] (+8)");
    result.back().valid = ownsActor(read(world_.enemies[0]));
    add("Lesson number (UI metadata)", "int32", world_.room, "breakout_world_root -> World.room (+24)");
    if (room_ == 3) {
        add("Remaining time", "float32", world_.remainingTime, "breakout_world_root -> World.remainingTime (+28)");
        add("Corridor distance", "float32", world_.corridorDistance, "breakout_world_root -> World.corridorDistance (+32)");
    }
    add("Door decision", "uint8", world_.doorOpen, "breakout_world_root -> World.doorOpen (+36)");
    auto actorFields = [&](Actor* actor, const std::string& prefix, const std::string& path) {
        if (!ownsActor(actor)) return;
        add(prefix + "health", "int32", actor->health, path + " -> Actor.health (+0)");
        add(prefix + "ammo", "int32", actor->ammo, path + " -> Actor.ammo (+4)");
        add(prefix + "speed", "float32", actor->speed, path + " -> Actor.speed (+8)");
        add(prefix + "charge", "float32", actor->charge, path + " -> Actor.charge (+12)");
        add(prefix + "X", "float32", actor->x, path + " -> Actor.x (+16)");
        add(prefix + "Y", "float32", actor->y, path + " -> Actor.y (+20)");
        add(prefix + "flags", "uint32", actor->flags, path + " -> Actor.flags (+24), power=bit0 alarm=bit1");
        add(prefix + "faction", "uint32", actor->faction, path + " -> Actor.faction (+28), player=1 enemy=2");
        add(prefix + "clearance", "uint32", actor->clearance, path + " -> Actor.clearance (+32), engineer=2");
        add(prefix + "keycard", "uint8", actor->keycard, path + " -> Actor.keycard (+36)");
        stringField(prefix + "callsign", actor->callsign, sizeof(actor->callsign), path + " -> Actor.callsign (+40)");
        add(prefix + "Inventory", "Inventory*", actor->inventory, path + " -> Actor.inventory (+64)");
        result.back().valid = ownsInventory(read(actor->inventory));
        auto* inv = inventory(actor); if (!inv) return;
        add(prefix + "slots[0]", "Weapon*", inv->slots[0], path + " -> Inventory.slots[0] (+0)"); result.back().valid = ownsWeapon(read(inv->slots[0]));
        add(prefix + "slots[1]", "Weapon*", inv->slots[1], path + " -> Inventory.slots[1] (+8)"); result.back().valid = ownsWeapon(read(inv->slots[1]));
        add(prefix + "equipped", "Weapon*", inv->equipped, path + " -> Inventory.equipped (+16)"); result.back().valid = ownsWeapon(read(inv->equipped));
        Weapon* weapon = equipped(actor); if (!weapon) return;
        stringField(prefix + "weapon name", weapon->name, sizeof(weapon->name), path + " -> Inventory.equipped -> Weapon.name (+0)");
        add(prefix + "weapon damage", "int32", weapon->damage, path + " -> Inventory.equipped -> Weapon.damage (+24)");
        add(prefix + "projectile speed", "float32", weapon->projectileSpeed, path + " -> Inventory.equipped -> Weapon.projectileSpeed (+28)");
        add(prefix + "cooldown", "float32", weapon->cooldown, path + " -> Inventory.equipped -> Weapon.cooldown (+32)");
    };
    actorFields(player(), "Player ", "breakout_world_root -> World.player");
    if (room_ == 9 || room_ == 10) actorFields(read(world_.enemies[0]), "Enemy ", "breakout_world_root -> World.enemies[0]");
    return result;
}
OutcomeSnapshot Game::outcome() const {
    OutcomeSnapshot result;
    result.complete = complete_; result.primaryObserved = primary_; result.restorationObserved = restored_;
    result.manualRequired = room_ == 5 || room_ == 11 || room_ == 12;
    result.manualAcknowledged = manual_;
    if (complete_) result.detail = "Room complete. The observed outcome does not prove which editing technique was used.";
    else if (primary_ && (room_ == 7 || room_ == 8 || room_ == 10 || room_ == 12) && !restored_)
        result.detail = room_ == 10 && !enemyHookVerified_ ? "Player healing observed. Fire the enemy weapon, restore the code, then fire the player weapon again." : "Patched behavior observed. Use Restore original or Restore all in ReClass, then execute the next action to verify restoration.";
    else if (primary_ && result.manualRequired && !manual_) result.detail = "Gameplay outcome observed. Complete and acknowledge the guided manual step.";
    else result.detail = "Follow the lesson and execute the controlled action to observe the outcome.";
    return result;
}
TeachingSnapshot Game::teaching() const {
    TeachingSnapshot result;
    result.worldRoot = reinterpret_cast<std::uintptr_t>(&breakout_world_root);
    result.ammoSite = reinterpret_cast<std::uintptr_t>(breakout_ammo_patchsite);
    result.damageSite = reinterpret_cast<std::uintptr_t>(breakout_damage_patchsite);
    result.vaultEntry = reinterpret_cast<std::uintptr_t>(breakout_vault_entry);
    result.vaultEndpoint = reinterpret_cast<std::uintptr_t>(breakout_vault_endpoint);
    result.signature = reinterpret_cast<std::uintptr_t>(breakout_ammo_signature);
    result.badgeSite = reinterpret_cast<std::uintptr_t>(breakout_badge_compare);
    result.signaturePattern = "52 43 42 52 4B 41 4D 4D 4F 35 35 21 A7 3C 6E 91";
    result.ammoPatched = std::memcmp(originalAmmo_.data(), breakout_ammo_patchsite, originalAmmo_.size()) != 0;
    result.damagePatched = std::memcmp(originalDamage_.data(), breakout_damage_patchsite, originalDamage_.size()) != 0;
    result.vaultPatched = std::memcmp(originalVault_.data(), breakout_vault_entry, originalVault_.size()) != 0;
    return result;
}
AcceptanceSnapshot Game::acceptance() const {
    AcceptanceSnapshot result;
    result.room = room_; result.shots = shots_; result.hits = hits_; result.enemyShots = enemyShots_;
    result.targetsRemaining = int(std::count_if(targets_.begin(), targets_.end(), [](const auto& t) { return t.active; }));
    result.weaponSwaps = swaps_; result.destroyedBeforeSwap = beforeSwap_; result.destroyedAfterSwap = afterSwap_;
    result.ticks = ticks_; result.paused = paused_; result.outcome = outcome();
    return result;
}

bool selfCheck(std::string& report) {
    Game game;
    std::vector<std::string> failures;
    auto check = [&failures](bool condition, const std::string& message) { if (!condition) failures.push_back(message); };
    const Actor* stablePlayer = game.world()->player;
    const auto teaching = game.teaching();
    check(teaching.ammoSite - teaching.signature == 16, "signature displacement");
    check(breakout_ammo_patchsite[0] == 0xff && breakout_ammo_patchsite[1] == 0x08, "DEC teaching instruction");
    check(breakout_damage_patchsite[0] == 0x83 && breakout_damage_patchsite[1] == 0x28 && breakout_damage_patchsite[2] == 0x0a, "SUB teaching instruction");
    for (int i = 2; i < 26; ++i) check(breakout_ammo_patchsite[i] == 0x90, "ammo patch padding");
    for (int room = 1; room <= Game::roomCount; ++room) {
        game.setRoom(room); Actor* a = game.world()->player;
        check(a == stablePlayer && game.paused(), "room " + std::to_string(room) + " stable root and paused initialization");
        check(a->ammo == 12 && a->health == (room == 10 ? 50 : 100), "room " + std::to_string(room) + " starting values");
        const auto tick = game.acceptance().ticks; float oldX = a->x;
        game.update(0.2, 1, 0); check(a->x == oldX && game.acceptance().ticks == tick, "paused update");
        game.advanceTick(); check(game.acceptance().ticks == tick+1, "exactly one explicit tick");
        switch (room) {
        case 1:
            for (int i=0; i<30; ++i) game.perform(Action::FireOnce);
            check(!game.outcome().complete, "room1 ordinary magazine cannot clear twenty targets");
            game.perform(Action::Reload);
            for (int i=0; i<20; ++i) game.perform(Action::FireOnce);
            check(!game.outcome().complete, "room1 reloading invalidates completion");
            game.resetRoom();
            a->ammo = 30; for (int i=0; i<20; ++i) game.perform(Action::FireOnce);
            check(game.outcome().complete && a->ammo == 10 && game.acceptance().shots == 20, "room1 all twenty targets, one shot/action"); break;
        case 2:
            game.perform(Action::ChargeOnce); check(a->charge == 45, "charge once");
            game.perform(Action::DrainOnce); check(a->charge == 40, "drain once");
            for (int i=0; i<100; ++i) game.perform(Action::ChargeOnce);
            game.perform(Action::ActivateReactor);
            check(a->charge == 60 && !game.outcome().complete, "room2 repeated recharge cannot activate reactor");
            game.perform(Action::DrainOnce); game.perform(Action::ChargeOnce);
            check(a->charge == 60, "room2 drain/recharge cannot cross normal ceiling");
            game.resetRoom(); game.setPaused(false);
            for (int i=0; i<14000; ++i) game.advanceTick();
            game.perform(Action::ActivateReactor);
            check(a->charge == 60 && !game.outcome().complete, "room2 passive charging cannot activate reactor");
            for (int i=0; i<100; ++i) { game.perform(Action::ChargeOnce); game.advanceTick(); }
            game.perform(Action::ActivateReactor);
            check(a->charge == 60 && !game.outcome().complete, "room2 combined controls cannot activate reactor");
            a->charge = 95; game.perform(Action::ActivateReactor); check(game.outcome().complete, "room2 edited charge activates reactor");
            game.perform(Action::ChargeOnce); check(a->charge == 100, "room2 externally edited charge retains +5 verification");
            game.resetRoom(); game.perform(Action::ActivateReactor);
            check(a->charge == 40 && !game.outcome().complete, "room2 reset removes edited completion"); break;
        case 3:
            game.perform(Action::StartTrial); for (int i=0; i<481; ++i) game.advanceTick(1, 0);
            check(!game.outcome().complete, "default speed cannot win");
            game.perform(Action::StartTrial); a->speed = 6000;
            for (int i=0; i<6; ++i) game.advanceTick(1, 0);
            check(game.outcome().complete, "edited speed wins trial"); break;
        case 4:
            for (int i=0; i<8; ++i) {
                game.perform(Action::ToggleKeycard); game.perform(Action::TogglePower);
                game.perform(Action::ToggleAlarm); game.perform(Action::EvaluateDoor);
                check(!game.outcome().complete && a->keycard == 0 && (a->flags & PowerFlag) == 0, "room4 controls cannot grant access");
            }
            a->keycard = 1; a->flags = PowerFlag | 0x80;
            game.perform(Action::EvaluateDoor);
            check(game.outcome().complete && a->flags == (PowerFlag | 0x80), "room4 edited byte/flags preserve unrelated bits");
            game.perform(Action::ToggleKeycard); game.perform(Action::EvaluateDoor);
            check(!game.outcome().complete && a->keycard == 0, "room4 invalidation revokes edited keycard");
            a->keycard = 1; game.perform(Action::TogglePower); game.perform(Action::EvaluateDoor);
            check(!game.outcome().complete && a->flags == 0x80, "room4 power cut revokes power and preserves unrelated bits"); break;
        case 5:
            game.perform(Action::AcknowledgeProject); game.perform(Action::EvaluateDoor);
            check(!game.outcome().complete, "room5 acknowledgement cannot replace identity edit");
            game.resetRoom();
            std::strcpy(a->callsign, "ENGINEER"); a->clearance = EngineerClearance;
            game.perform(Action::EvaluateDoor); check(!game.outcome().complete && game.outcome().primaryObserved, "room5 manual requirement");
            game.perform(Action::AcknowledgeProject); check(game.outcome().complete, "room5 structure outcome");
            game.resetRoom(); std::strcpy(a->callsign, "ENGINEERS"); a->clearance = EngineerClearance;
            game.perform(Action::EvaluateDoor); check(!game.outcome().primaryObserved, "room5 scanner rejects a longer callsign");
            game.resetRoom(); a->x = 500; a->y = 180; game.advanceTick();
            check(game.renderSnapshot().scanning && !game.outcome().primaryObserved, "room5 standing on the pad scans without granting access"); break;
        case 6: {
            for (int i=0; i<6; ++i) game.perform(Action::FireOnce);
            game.perform(Action::SwapWeapon);
            for (int i=0; i<6; ++i) game.perform(Action::FireOnce);
            check(!game.outcome().complete && game.acceptance().targetsRemaining == 2, "room6 ordinary weapons cannot penetrate armor");
            game.resetRoom();
            auto* old = a->inventory->equipped; old->damage = 30; game.perform(Action::FireOnce);
            game.perform(Action::SwapWeapon); auto* replacement = a->inventory->equipped;
            check(old != replacement && replacement->damage == 7, "replacement changes address and starts unmodified");
            replacement->damage = 30; game.perform(Action::FireOnce); check(game.outcome().complete, "room6 upgrade each equipped weapon"); break;
        }
        case 7:
            for (int i=0; i<20; ++i) game.perform(Action::TakeOneHit);
            game.perform(Action::ToggleTurret);
            for (int i=0; i<400; ++i) game.advanceTick();
            check(!game.outcome().complete, "room7 repeated hits and turret cannot simulate invulnerability");
            game.resetRoom();
            game.perform(Action::TakeOneHit); check(a->health == 90 && game.acceptance().hits == 1 && !game.outcome().complete, "room7 ordinary damage and restoration requirement"); break;
        case 8:
            for (int i=0; i<20; ++i) game.perform(Action::FireOnce);
            check(!game.outcome().complete, "room8 ordinary shots cannot simulate ammo increments");
            game.resetRoom();
            game.perform(Action::FireOnce); check(a->ammo == 11 && !game.outcome().complete, "room8 ordinary ammo and restoration requirement"); break;
        case 9:
            game.perform(Action::TakeOneHit); game.perform(Action::HitEnemy);
            check(!game.outcome().complete && !game.outcome().manualRequired, "room9 hits alone do not unlock the press");
            a->clearance = game.decoyCode_; game.perform(Action::EvaluateDoor);
            check(!game.outcome().complete, "room9 the enemy's decoy code is rejected");
            check(game.overrideCode_ >= 1000 && game.overrideCode_ <= 9999 && game.overrideCode_ != game.decoyCode_, "room9 distinct four-digit codes");
            a->clearance = game.overrideCode_; game.perform(Action::EvaluateDoor);
            check(a->health == 90 && game.world()->enemies[0]->health == 90 && game.outcome().complete, "room9 player's override code completes"); break;
        case 10:
            for (int i=0; i<20; ++i) { game.perform(Action::FireOnce); game.perform(Action::EnemyFire); }
            check(!game.outcome().complete, "room10 ordinary firing cannot heal player");
            game.resetRoom();
            game.perform(Action::FireOnce); game.perform(Action::EnemyFire);
            check(a->ammo == 11 && a->health == 50 && game.world()->enemies[0]->ammo == 11 && !game.outcome().complete, "room10 ordinary ammo does not heal"); break;
        case 11:
            game.perform(Action::AcknowledgeTrace);
            for (int i=0; i<8; ++i) {
                game.perform(Action::ToggleKeycard); game.perform(Action::ToggleAlarm); game.perform(Action::EvaluateDoor);
                check(!game.outcome().complete, "room11 controls/acknowledgement cannot open vault");
            }
            a->keycard = 1; a->clearance = EngineerClearance; a->flags = PowerFlag;
            game.perform(Action::EvaluateDoor); game.perform(Action::AcknowledgeTrace); check(game.outcome().complete, "room11 NASM vault and trace acknowledgment"); break;
        case 12:
            game.perform(Action::AcknowledgeRestart);
            for (int i=0; i<20; ++i) game.perform(Action::FireOnce);
            check(!game.outcome().complete, "room12 restart acknowledgement cannot replace patch");
            game.resetRoom(); game.perform(Action::AcknowledgeRestart); game.perform(Action::FireOnce);
            check(!game.outcome().complete && a->ammo == 11, "room12 requires actual increasing then restored behavior"); break;
        }
    }
    game.setRoom(3); Actor* a = game.world()->player; game.setPaused(false);
    auto ticks = game.acceptance().ticks; game.update(10, 1, 0); check(game.acceptance().ticks == ticks, "debugger elapsed time discarded");
    game.update(0.24, 1, 0); check(game.acceptance().ticks <= ticks+6, "bounded catch-up");
    game.focusLost(); check(game.paused(), "focus loss pauses simulation");
    a->speed = std::numeric_limits<float>::quiet_NaN(); ticks = game.acceptance().ticks;
    game.advanceTick(1, 0); check(game.acceptance().ticks == ticks, "non-finite movement suspended");
    game.setRoom(6); a = game.world()->player;
    a->inventory = reinterpret_cast<Inventory*>(std::uintptr_t(1));
    check(!game.perform(Action::FireOnce) && a->ammo == 12, "invalid inventory pointer safely suspends fire");
    check(!game.fields().empty(), "invalid pointer snapshot safely displays raw values");
    game.resetRoom(); a = game.world()->player; a->inventory->equipped = reinterpret_cast<Weapon*>(std::uintptr_t(1));
    check(!game.perform(Action::FireOnce) && a->ammo == 12, "invalid equipped pointer safely suspends fire");
    game.resetRoom(); game.world()->player = reinterpret_cast<Actor*>(std::uintptr_t(1));
    check(!game.perform(Action::FireOnce) && !game.renderSnapshot().playerValid, "invalid actor pointer safely suspends action and rendering");
    game.resetRoom(); breakout_world_root = reinterpret_cast<World*>(std::uintptr_t(1));
    check(!game.perform(Action::FireOnce), "invalid world root safely suspends action");
    game.resetRoom();
    check(!game.teaching().ammoPatched && !game.teaching().damagePatched, "self-check never patches teaching code");
    std::ostringstream output;
    if (failures.empty()) output << "ReClass Breakout self-check passed: all 12 initializers, controlled actions, data outcomes, pointer safety, fixed ticks and teaching layout. Code-patch/restoration workflows require the guided ReClass acceptance pass.";
    else { output << "ReClass Breakout self-check failed:"; for (const auto& failure : failures) output << "\n- " << failure; }
    report = output.str(); return failures.empty();
}
} // namespace breakout

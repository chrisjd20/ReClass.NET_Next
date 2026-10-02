#include "game.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <iomanip>
#include <limits>
#include <random>
#include <sstream>

// Keep the root in file-backed .data: on Linux the tail of .bss is an anonymous
// mapping that ReClass does not count as part of the module.
#if defined(__GNUC__) && !defined(_WIN32)
extern "C" { __attribute__((section(".data"))) breakout::World* breakout_world_root = nullptr; }
#else
extern "C" { breakout::World* breakout_world_root = nullptr; }
#endif

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
// Copies a text field with one bulk read. A debugger watching these bytes then stops the game once or twice per
// call rather than once per letter, which keeps "Find out what accesses" on the callsign from crippling the game.
std::array<unsigned char, 64> bulkCopy(const char* source, std::size_t size) {
    std::array<unsigned char, 64> copy{};
    std::memcpy(copy.data(), source, std::min(size, copy.size()));
    return copy;
}
std::string stringValue(const char* source, std::size_t size) {
    std::string result;
    const auto bytes = bulkCopy(source, size);
    for (std::size_t i = 0; i < std::min(size, bytes.size()) && bytes[i]; ++i) {
        unsigned char c = bytes[i];
        if (c >= 32 && c < 127) result.push_back(char(c));
        else { result += "\\x"; std::ostringstream s; s << std::hex << std::setw(2) << std::setfill('0') << unsigned(c); result += s.str(); }
    }
    return result;
}
float renderFloat(float value, float fallback = 0) { return std::isfinite(value) ? value : fallback; }
constexpr std::uint32_t MaintenanceFlag = 0x80;
const char* actionLabel(Action action) {
    switch (action) {
    case Action::FireOnce: return "Fire";
    case Action::ChargeOnce: return "Charge +5";
    case Action::DrainOnce: return "Drain -5";
    case Action::ActivateReactor: return "Activate reactor";
    case Action::AdvanceTick: return "Advance one tick";
    case Action::ToggleAlarm: return "Flip alarm switch";
    case Action::EvaluateDoor: return "Open door";
    case Action::SwapWeapon: return "Swap weapon";
    case Action::TakeOneHit: return "Take one hit";
    case Action::HitEnemy: return "Hit sentinel";
    case Action::EnemyFire: return "Sentinel fires";
    case Action::StartTrial: return "Start run now";
    case Action::StartCountdown: return "Start run";
    case Action::RebootRelay: return "Reboot relay";
    }
    return "Action";
}
}

Game::Game() {
    worlds_.push_back(std::make_unique<World>());
    world_ = worlds_.back().get();
    for (std::size_t i = 0; i < actors_.size(); ++i) {
        actors_[i] = std::make_unique<Actor>(); inventories_[i] = std::make_unique<Inventory>();
        weapons_.push_back(std::make_unique<Weapon>()); weapons_.push_back(std::make_unique<Weapon>());
    }
    std::memcpy(originalAmmo_.data(), breakout_ammo_patchsite, originalAmmo_.size());
    std::memcpy(originalDamage_.data(), breakout_damage_patchsite, originalDamage_.size());
    std::memcpy(originalVault_.data(), breakout_vault_entry, originalVault_.size());
    breakout_world_root = world_;
    resetRoom();
}
Game::~Game() { if (breakout_world_root == world_) breakout_world_root = nullptr; }
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
    if (read(breakout_world_root) != world_) return nullptr;
    Actor* pointer = read(world_->player); return ownsActor(pointer) ? pointer : nullptr;
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
    if (!ownsActor(actor)) { status_ = "Suspended: the player pointer is invalid. Restore it in ReClass or restart the room."; return false; }
    return true;
}
void Game::initializeWeapon(Weapon& weapon, const char* name, int damage, float speed, float cooldown) {
    weapon = {}; std::strncpy(weapon.name, name, sizeof(weapon.name)-1);
    write(weapon.damage, damage); write(weapon.projectileSpeed, speed); write(weapon.cooldown, cooldown);
}
void Game::relocateWorld() {
    // Allocate the new block before retiring the old one so the address really changes.
    auto next = std::make_unique<World>(*world_);
    next->corridorDistance = 0; next->doorOpen = 0;
    world_ = next.get(); worlds_.push_back(std::move(next));
    if (worlds_.size() > 32) worlds_.erase(worlds_.begin());
    breakout_world_root = world_;
}
void Game::setRoom(int room) { room_ = std::clamp(room, 0, roomCount - 1); resetRoom(); }
void Game::resetRoom() {
    // Reset only owned gameplay allocations; no teaching-code bytes are written.
    *world_ = {}; breakout_world_root = world_; world_->player = actors_[0].get();
    world_->enemies[0] = actors_[1].get(); world_->enemies[1] = actors_[2].get();
    world_->room = room_; world_->remainingTime = 8; world_->corridorDistance = room_ == 6 ? 0.0f : 580.0f;
    for (std::size_t i = 0; i < actors_.size(); ++i) {
        auto& a = *actors_[i]; a = {}; a.health = 100; a.ammo = 12; a.speed = 80;
        a.charge = 40; a.x = i ? 550.0f : SpawnX; a.y = i ? 100.0f + 120.0f * float(i) : SpawnY;
        a.flags = AlarmFlag; a.faction = i ? EnemyFaction : PlayerFaction;
        std::strncpy(a.callsign, i ? "SENTINEL" : "ROOKIE", sizeof(a.callsign)-1);
        a.inventory = inventories_[i].get(); auto& inv = *inventories_[i]; inv = {};
        inv.slots[0] = weapons_[i*2].get(); inv.slots[1] = weapons_[i*2+1].get(); inv.equipped = inv.slots[0];
        initializeWeapon(*inv.slots[0], "PULSE", 5, 260, 0.35f);
        initializeWeapon(*inv.slots[1], "ARC", 7, 180, 0.6f);
    }
    if (room_ == 3) actors_[0]->speed = 60;
    if (room_ == 4) actors_[0]->flags = AlarmFlag | MaintenanceFlag;
    if (room_ == 11) actors_[0]->health = 50;
    if (room_ == 10) { actors_[1]->x = 560; actors_[1]->y = 330; }
    targets_.clear();
    if (room_ == 1) {
        for (int i = 0; i < 20; ++i) targets_.push_back({SceneObject::Kind::Target, 260.0f + float(i%5)*95, 65.0f + float(i/5)*85, 20, true, "Drone " + std::to_string(i+1), 1});
    } else if (room_ == 5) {
        targets_.push_back({SceneObject::Kind::Target, 480, 140, 26, true, "Sentinel A", 30});
        targets_.push_back({SceneObject::Kind::Target, 650, 260, 26, true, "Sentinel B", 30});
    } else if (room_ == 8 || room_ == 11 || room_ == 13) {
        targets_.push_back({SceneObject::Kind::Target, 620, 180, 24, true, "Training wall", 100000});
    }
    shots_ = hits_ = enemyHits_ = enemyShots_ = swaps_ = beforeSwap_ = afterSwap_ = unchangedHits_ = increasingShots_ = links_ = 0;
    primary_ = restored_ = complete_ = enemyHookVerified_ = hasAim_ = overrideAccepted_ = relayCounted_ = false;
    downed_ = false; collapseX_ = fallTimer_ = 0;
    static std::mt19937 codes{std::random_device{}()};
    std::uniform_int_distribution<std::uint32_t> code(1000, 9999);
    overrideCode_ = code(codes);
    do decoyCode_ = code(codes); while (decoyCode_ == overrideCode_);
    accumulator_ = 0; hazardTimer_ = enemyTimer_ = countdown_ = 0; ticks_ = 0;
    fail_.clear();
    status_ = "Room ready. External code patches are unchanged by a restart.";
}
// Room 3: back to the start line with an intact bridge and a full timer.
void Game::beginRun(bool countdown) {
    Actor* a = player(); if (!a) return;
    write(a->x, SpawnX); write(a->y, SpawnY); write(world_->remainingTime, 8.0f);
    write(world_->corridorDistance, 580.0f);
    downed_ = false; fallTimer_ = 0; collapseX_ = 0; primary_ = complete_ = false;
    countdown_ = countdown ? 3.0f : 0.0f;
    write(world_->trialRunning, std::uint8_t(countdown ? 0 : 1));
    status_ = countdown ? "Get ready..." : "GO!";
}
void Game::respawn(const std::string& reason) {
    Actor* a = player(); if (!a) return;
    write(a->health, std::int32_t(room_ == 11 ? 50 : 100));
    write(a->x, SpawnX); write(a->y, SpawnY);
    write(world_->trialRunning, std::uint8_t(0));
    countdown_ = 0; hazardTimer_ = 0;
    fail_ = reason; ++failSerial_; status_ = reason;
}

std::vector<ActionDefinition> Game::actions() const {
    // Shooting works everywhere so ROOKIE's ammo can always be found with an exact scan.
    std::vector<Action> selected{Action::AdvanceTick, Action::FireOnce};
    switch (room_) {
    case 2: selected.insert(selected.end(), {Action::ChargeOnce, Action::DrainOnce, Action::ActivateReactor}); break;
    case 3: selected.insert(selected.end(), {Action::StartTrial, Action::StartCountdown}); break;
    case 4: selected.insert(selected.end(), {Action::ToggleAlarm, Action::EvaluateDoor}); break;
    case 5: selected.push_back(Action::SwapWeapon); break;
    case 6: selected.push_back(Action::RebootRelay); break;
    case 7: selected.push_back(Action::TakeOneHit); break;
    case 9: case 12: selected.push_back(Action::EvaluateDoor); break;
    case 10: selected.insert(selected.end(), {Action::TakeOneHit, Action::HitEnemy, Action::EvaluateDoor}); break;
    case 11: selected.push_back(Action::EnemyFire); break;
    default: break;
    }
    std::vector<ActionDefinition> result;
    for (auto action : selected) result.push_back({action, actionLabel(action), true});
    return result;
}
std::vector<Zone> Game::layoutZones(int room) {
    std::vector<Zone> result;
    const auto pad = [&](const char* id, const char* label, float x, float y, float w, float h) { result.push_back({id, label, x, y, w, h, false, Action::AdvanceTick, false}); };
    const auto console = [&](const char* id, const char* label, float x, float y, float w, float h, Action action) { result.push_back({id, label, x, y, w, h, true, action, false}); };
    switch (room) {
    case 2:
        pad("charge", "CHARGE", 200, 70, 90, 70); pad("drain", "DRAIN", 200, 260, 90, 70);
        console("reactor", "Activate reactor", 520, 150, 50, 60, Action::ActivateReactor); break;
    case 4:
        console("switch", "Flip alarm switch", 300, 30, 60, 50, Action::ToggleAlarm);
        console("door", "Open blast door", 560, 250, 50, 50, Action::EvaluateDoor); break;
    case 5: console("rack", "Swap weapon", 120, 300, 80, 60, Action::SwapWeapon); break;
    case 6: console("relay", "Reboot relay (moves its control block)", 300, 30, 90, 60, Action::RebootRelay); break;
    case 7: pad("turret", "TURRET ZONE", 440, 0, 360, 400); break;
    case 9: pad("scan", "SCAN PAD", 470, 150, 60, 60); break;
    case 10:
        pad("press", "PRESS", 340, 300, 120, 60);
        console("console", "Submit override code", 300, 40, 200, 64, Action::EvaluateDoor); break;
    case 12: console("vault", "Try vault", 560, 250, 50, 50, Action::EvaluateDoor); break;
    default: break;
    }
    return result;
}
namespace {
// Consoles are usable from just outside their footprint; pads need you on them.
bool contains(const Zone& zone, float x, float y) {
    const float reach = zone.interact ? 28.0f : 0.0f;
    return x >= zone.x - reach && x <= zone.x + zone.w + reach && y >= zone.y - reach && y <= zone.y + zone.h + reach;
}
}
std::vector<Zone> Game::zones() const {
    auto result = layoutZones(room_);
    if (const Actor* a = player()) {
        const float x = read(a->x), y = read(a->y);
        for (auto& zone : result) zone.occupied = std::isfinite(x) && std::isfinite(y) && contains(zone, x, y);
    }
    return result;
}
bool Game::inZone(const Actor* actor, const char* id) const {
    const float x = read(actor->x), y = read(actor->y);
    if (!std::isfinite(x) || !std::isfinite(y)) return false;
    for (const auto& zone : layoutZones(room_)) if (zone.id == id) return contains(zone, x, y);
    return false;
}
bool Game::interaction(Zone& found) const {
    for (const auto& zone : zones()) if (zone.interact && zone.occupied) { found = zone; return true; }
    return false;
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
    case Action::ChargeOnce: case Action::DrainOnce: {
        // Locals of scanned fields are doubles: this file is built without optimization,
        // so a float/int local would linger on the stack as a scan decoy.
        const double charge = read(a->charge);
        if (!std::isfinite(charge)) { status_ = "Charge is not a valid number."; return false; }
        double next = charge + (action == Action::ChargeOnce ? 5.0 : -5.0);
        // Normal charging stops at the cap; externally edited values stay authoritative.
        if (action == Action::ChargeOnce && charge <= NormalChargeLimit) next = std::min<double>(next, NormalChargeLimit);
        write(a->charge, static_cast<float>(std::max(0.0, next)));
        break;
    }
    case Action::ActivateReactor:
        if (!std::isfinite(read(a->charge))) { status_ = "Reactor refuses a non-finite charge."; return false; }
        primary_ = read(a->charge) >= 90;
        status_ = primary_ ? "Reactor online." : "Not enough charge: the reactor needs 90%.";
        break;
    case Action::ToggleAlarm: write(a->flags, read(a->flags) ^ AlarmFlag); status_ = (read(a->flags) & AlarmFlag) ? "Alarm armed." : "Alarm off."; break;
    case Action::EvaluateDoor: result = evaluateDoor(); break;
    case Action::SwapWeapon: {
        auto* inv = inventory(a);
        if (!inv) { status_ = "Swap failed: the inventory pointer is invalid."; return false; }
        // Allocate before replacing, retaining the previous owned object for safe pointer inspection.
        auto replacement = std::make_unique<Weapon>();
        initializeWeapon(*replacement, swaps_ % 2 == 0 ? "ARC" : "PULSE", 7, 180, 0.6f);
        auto* pointer = replacement.get(); weapons_.push_back(std::move(replacement));
        write(inv->slots[(swaps_ + 1) % 2], pointer); write(inv->equipped, pointer); ++swaps_;
        status_ = "New weapon equipped: it is a new object at a new address."; break;
    }
    case Action::TakeOneHit: result = hit(a); break;
    case Action::HitEnemy: {
        Actor* enemy = read(world_->enemies[0]); if (!validateActor(enemy)) return false;
        breakout_apply_damage(enemy, decoyCode_); ++enemyHits_;
        if (read(enemy->health) <= 0) write(enemy->health, std::int32_t(100));
        status_ = "The press hits the sentinel."; break;
    }
    case Action::EnemyFire: {
        Actor* enemy = read(world_->enemies[0]); if (!validateActor(enemy)) return false;
        if (read(enemy->ammo) <= 0) write(enemy->ammo, std::int32_t(12));
        result = fire(enemy, true); break;
    }
    case Action::StartTrial:
        if (!std::isfinite(read(a->speed))) { status_ = "Speed is not a valid number."; return false; }
        beginRun(false); break;
    case Action::StartCountdown:
        if (read(world_->trialRunning) || countdown_ > 0 || fallTimer_ > 0) return false;
        beginRun(true); break;
    case Action::RebootRelay:
        relocateWorld(); relayCounted_ = false;
        status_ = "Relay rebooted: its control block moved to a new address."; break;
    case Action::AdvanceTick: break;
    }
    updateCompletion(); return result;
}

bool Game::fire(Actor* actor, bool enemy) {
    if (!validateActor(actor)) return false;
    if (read(actor->ammo) <= 0) { status_ = "Out of ammo."; return false; }
    auto* weapon = equipped(actor);
    if (!weapon) { status_ = "Can't fire: the equipped weapon pointer is invalid."; return false; }
    if (!std::isfinite(read(weapon->projectileSpeed)) || !std::isfinite(read(weapon->cooldown))) {
        status_ = "Can't fire: the weapon's speed or cooldown is not a valid number."; return false;
    }
    Actor* p = player();
    const double oldAmmo = read(actor->ammo), oldHealth = read(actor->health), playerHealth = p ? read(p->health) : 0;
    breakout_decrement_ammo(actor);
    const double newAmmo = read(actor->ammo), newHealth = read(actor->health);
    if (enemy) {
        ++enemyShots_;
        // Only a sentinel shot fired while the hook is live proves it spares the sentinel.
        if (room_ == 11 && primary_ && std::int64_t(newAmmo) == std::int64_t(oldAmmo)-1 && newHealth == oldHealth && p && read(p->health) == playerHealth) enemyHookVerified_ = true;
        status_ = "The sentinel fired.";
    } else {
        ++shots_;
        if (room_ == 8 || room_ == 13) {
            if (!primary_) {
                if (std::int64_t(newAmmo) == std::int64_t(oldAmmo)+1) ++increasingShots_; else increasingShots_ = 0;
                if (increasingShots_ >= 3) primary_ = true;
            } else if (std::int64_t(newAmmo) == std::int64_t(oldAmmo)-1) restored_ = true;
        }
        if (room_ == 11) {
            if (!primary_ && std::int64_t(newAmmo) == std::int64_t(oldAmmo)-1 && std::int64_t(newHealth) == std::int64_t(oldHealth)+5) primary_ = true;
            else if (primary_ && enemyHookVerified_ && std::int64_t(newAmmo) == std::int64_t(oldAmmo)-1 && newHealth == oldHealth) restored_ = true;
        }
        SceneObject* target = nullptr;
        const auto eligible = [&](const SceneObject& candidate) {
            // Sentinel B steps out only after a weapon swap.
            return candidate.active && (room_ != 5 || (&candidate == &targets_[0] ? swaps_ == 0 : swaps_ > 0));
        };
        if (!hasAim_) {
            for (auto& candidate : targets_) if (eligible(candidate)) { target = &candidate; break; }
        } else if (p && std::isfinite(read(p->x)) && std::isfinite(read(p->y)) && std::isfinite(aimX_) && std::isfinite(aimY_)) {
            float px = read(p->x), py = read(p->y), dx = aimX_ - px, dy = aimY_ - py;
            float length = std::sqrt(dx*dx + dy*dy), best = std::numeric_limits<float>::max();
            if (length > 0.001f) for (auto& candidate : targets_) {
                if (!eligible(candidate)) continue;
                float tx = candidate.x-px, ty = candidate.y-py;
                float along = (tx*dx + ty*dy)/length, across = std::fabs(tx*dy - ty*dx)/length;
                if (along >= 0 && across <= candidate.radius && along < best) { best = along; target = &candidate; }
            }
        }
        if (target) {
            int damage = read(weapon->damage);
            if (room_ != 5 || damage >= 20) {
                const std::int64_t remaining = std::int64_t(target->health) - std::max(0, damage);
                target->health = int(std::max<std::int64_t>(0, remaining));
                if (target->health == 0) { target->active = false; if (room_ == 5) { if (swaps_ == 0) ++beforeSwap_; else ++afterSwap_; } }
            }
        }
        status_ = "Ammo " + std::to_string(static_cast<long long>(oldAmmo)) + " -> " + std::to_string(static_cast<long long>(newAmmo)) + ".";
        if (room_ == 5 && target && read(weapon->damage) < 20) status_ = "The armor deflects anything under 20 damage.";
    }
    updateCompletion(); return true;
}
bool Game::hit(Actor* actor) {
    if (!validateActor(actor)) return false;
    const double before = read(actor->health); breakout_apply_damage(actor, overrideCode_); const double after = read(actor->health); ++hits_;
    if (room_ == 7) {
        if (!primary_) { unchangedHits_ = after == before ? unchangedHits_+1 : 0; if (unchangedHits_ >= 3) primary_ = true; }
        else if (std::int64_t(after) == std::int64_t(before)-10) restored_ = true;
    }
    status_ = "Hit: health " + std::to_string(static_cast<long long>(before)) + " -> " + std::to_string(static_cast<long long>(after)) + ".";
    updateCompletion();
    if (after <= 0 && !complete_) respawn("Destroyed. ROOKIE respawns with full health.");
    return true;
}
bool Game::evaluateDoor() {
    Actor* a = player(); if (!validateActor(a)) return false;
    bool accepted = false;
    if (room_ == 4) {
        const auto flags = read(a->flags);
        accepted = read(a->keycard) == 1 && (flags & PowerFlag) && !(flags & AlarmFlag) && (flags & MaintenanceFlag);
    } else if (room_ == 9) accepted = breakout_scan_badge(a) != 0;
    else if (room_ == 10) {
        overrideAccepted_ = read(a->clearance) == overrideCode_;
        status_ = overrideAccepted_ ? "Override accepted." : "Override rejected: your clearance doesn't match the code the press carries for you.";
        updateCompletion(); return true;
    } else accepted = breakout_evaluate_vault(a) != 0;
    write(world_->doorOpen, std::uint8_t(accepted)); primary_ = accepted;
    status_ = accepted ? "Access granted." : room_ == 9 ? "Badge rejected." : "Access denied.";
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
        status_ = "Frozen: speed or position is not a valid number."; return;
    }
    ++ticks_;
    if (room_ == 3) {
        // While ROOKIE falls, and after, nothing moves until the player starts again.
        if (fallTimer_ > 0) { fallTimer_ -= fixedStep; if (fallTimer_ <= 0) { fallTimer_ = 0; downed_ = true; } updateCompletion(); return; }
        if (downed_) { updateCompletion(); return; }
        if (countdown_ > 0) {
            // Hold still for 3-2-1, then the run starts.
            countdown_ -= fixedStep;
            if (countdown_ <= 0) { countdown_ = 0; write(world_->trialRunning, std::uint8_t(1)); status_ = "GO!"; }
            updateCompletion(); return;
        }
    }
    const float norm = std::sqrt(movementX*movementX + movementY*movementY);
    if (norm > 1) { movementX /= norm; movementY /= norm; }
    const double nextX = double(x) + double(speed)*movementX*fixedStep;
    const double nextY = double(y) + double(speed)*movementY*fixedStep;
    if (!std::isfinite(nextX) || !std::isfinite(nextY)) { status_ = "Frozen: movement result is not a valid number."; return; }
    // A closed door is a wall; it opens only from the authoritative decision byte.
    const bool sealed = (room_ == 4 || room_ == 6 || room_ == 9 || room_ == 12) && !read(world_->doorOpen);
    const double minX = sealed && x > 650 ? 690.0 : 30.0, maxX = sealed && x <= 650 ? 610.0 : 770.0;
    const double minY = room_ == 3 ? 160.0 : 30.0, maxY = room_ == 3 ? 200.0 : 370.0;
    write(a->x, float(std::clamp(nextX, minX, maxX))); write(a->y, float(std::clamp(nextY, minY, maxY)));
    switch (room_) {
    case 2:
        if (inZone(a, "charge")) { const double c = read(a->charge); if (std::isfinite(c) && c < NormalChargeLimit) write(a->charge, static_cast<float>(std::min<double>(NormalChargeLimit, c + 10.0 * fixedStep))); }
        if (inZone(a, "drain")) { const double c = read(a->charge); if (std::isfinite(c)) write(a->charge, static_cast<float>(std::max(0.0, c - 10.0 * fixedStep))); }
        break;
    case 3:
        if (read(world_->trialRunning)) {
            float time = read(world_->remainingTime);
            if (!std::isfinite(time)) { status_ = "Frozen: the timer is not a valid number."; return; }
            time = std::max(0.0f, time - fixedStep);
            write(world_->remainingTime, time);
            write(world_->corridorDistance, std::max(0.0f, 650.0f - read(a->x)));
            // The collapse starts at the bridge edge and accelerates: an idle robot falls after ~1.8 s, and at speed 60 the 8 s timer runs out first.
            const float elapsed = 8.0f - time;
            collapseX_ = std::min(685.0f, 45.0f + 7.5f * elapsed * elapsed);
            if (read(a->x) >= 650) { primary_ = true; write(world_->trialRunning, std::uint8_t(0)); status_ = "Made it across!"; }
            else if (collapseX_ >= read(a->x) || time <= 0) {
                write(world_->trialRunning, std::uint8_t(0));
                // The floor under ROOKIE goes with him.
                collapseX_ = std::min(685.0f, std::max(collapseX_, read(a->x) + 30));
                fallTimer_ = FallSeconds;
                std::ostringstream reason;
                reason << std::setprecision(3) << "The bridge fell away under ROOKIE. At speed " << read(a->speed) << " the crossing takes "
                       << std::setprecision(2) << std::fixed << 580.0f / std::max(0.001f, read(a->speed)) << " s; the bridge lasts 8.";
                fail_ = reason.str(); ++failSerial_; status_ = fail_;
            }
        }
        break;
    case 6: {
        // The relay door follows its control block's power value.
        const float power = read(world_->corridorDistance);
        const bool powered = std::isfinite(power) && power >= 100;
        write(world_->doorOpen, std::uint8_t(powered));
        if (powered && !relayCounted_) { relayCounted_ = true; ++links_; status_ = "Relay link " + std::to_string(links_) + " / 3."; }
        break;
    }
    case 7:
        if (inZone(a, "turret")) { hazardTimer_ += fixedStep; if (hazardTimer_ >= 1.0f) { hazardTimer_ = 0; hit(a); } }
        else hazardTimer_ = 0.6f;
        break;
    case 9:
        // The gate scanner reads the badge every tick while the player stands on its pad.
        if (inZone(a, "scan") && breakout_scan_badge(a) && !primary_) {
            write(world_->doorOpen, std::uint8_t(1)); primary_ = true; status_ = "Access granted.";
        }
        break;
    case 10:
        // The press swings at the sentinel, then at you, while you stand on its plate.
        if (inZone(a, "press")) {
            const float before = hazardTimer_;
            hazardTimer_ += fixedStep;
            if (before < .8f && hazardTimer_ >= .8f) perform(Action::HitEnemy);
            if (hazardTimer_ >= 1.6f) { hazardTimer_ = 0; hit(a); }
        } else hazardTimer_ = 0;
        break;
    case 11:
        enemyTimer_ += fixedStep;
        if (enemyTimer_ >= 2.5f) { enemyTimer_ = 0; perform(Action::EnemyFire); }
        break;
    default: break;
    }
    updateCompletion();
}
void Game::updateCompletion() {
    switch (room_) {
    case 1: primary_ = std::none_of(targets_.begin(), targets_.end(), [](const auto& t) { return t.active; }); break;
    case 5: primary_ = beforeSwap_ > 0 && swaps_ > 0 && afterSwap_ > 0; break;
    case 6: primary_ = links_ >= 3; break;
    case 10: primary_ = hits_ > 0 && enemyHits_ > 0 && overrideAccepted_; break;
    default: break;
    }
    if (room_ == 0) complete_ = false;
    else if (room_ == 7 || room_ == 8 || room_ == 13) complete_ = primary_ && restored_;
    else if (room_ == 11) complete_ = primary_ && enemyHookVerified_ && restored_;
    else complete_ = primary_;
}

RenderSnapshot Game::renderSnapshot() const {
    RenderSnapshot result;
    Actor* a = player(); result.playerValid = a != nullptr;
    if (a) {
        result.playerX = renderFloat(read(a->x), SpawnX); result.playerY = renderFloat(read(a->y), SpawnY);
        result.playerSpeed = renderFloat(read(a->speed)); result.charge = renderFloat(read(a->charge));
        result.health = read(a->health); result.ammo = read(a->ammo);
        result.playerValid = std::isfinite(read(a->x)) && std::isfinite(read(a->y));
        result.keycard = read(a->keycard); result.flags = read(a->flags); result.clearance = read(a->clearance);
        result.faction = read(a->faction); result.callsign = stringValue(a->callsign, sizeof(a->callsign));
        if (Weapon* weapon = equipped(a)) {
            result.weaponName = stringValue(weapon->name, sizeof(weapon->name));
            result.weaponDamage = read(weapon->damage); result.projectileSpeed = renderFloat(read(weapon->projectileSpeed), 260);
        }
        result.scanning = room_ == 9 && inZone(a, "scan");
    }
    if (Actor* enemy = read(world_->enemies[0]); ownsActor(enemy)) { result.enemyHealth = read(enemy->health); result.enemyAmmo = read(enemy->ammo); }
    result.shots = shots_; result.hits = hits_; result.enemyHits = enemyHits_; result.enemyShots = enemyShots_;
    result.swaps = swaps_; result.primary = primary_; result.complete = complete_; result.links = links_;
    result.paused = paused_; result.countdown = countdown_; result.collapseX = collapseX_;
    result.fall = fallTimer_ > 0 ? 1 - fallTimer_ / FallSeconds : downed_ ? 1.0f : 0.0f; result.downed = downed_; result.fail = fail_; result.failSerial = failSerial_;
    result.remainingTime = renderFloat(read(world_->remainingTime));
    result.corridorDistance = renderFloat(read(world_->corridorDistance));
    result.relayPower = room_ == 6 ? result.corridorDistance : 0;
    result.doorOpen = read(world_->doorOpen) != 0; result.trialRunning = read(world_->trialRunning) != 0;
    result.objects = targets_;
    result.zones = zones();
    if (room_ == 10 || room_ == 11) {
        Actor* enemy = read(world_->enemies[0]);
        if (ownsActor(enemy)) result.objects.push_back({SceneObject::Kind::Enemy, renderFloat(read(enemy->x), 550), renderFloat(read(enemy->y), 220), 24, true, "Sentinel", read(enemy->health)});
    }
    if (room_ == 7) result.objects.push_back({SceneObject::Kind::Turret, 700, 90, 22, a && inZone(a, "turret"), "Turret", 0});
    if (room_ == 2) result.objects.push_back({SceneObject::Kind::Reactor, 640, 180, 40, !primary_, "Reactor", 0});
    if (room_ == 3) result.objects.push_back({SceneObject::Kind::Exit, 650, 180, 35, !primary_, "Exit", 0});
    if (room_ == 4 || room_ == 6 || room_ == 9 || room_ == 12) result.objects.push_back({SceneObject::Kind::Door, 650, 180, 40, !result.doorOpen, result.doorOpen ? "Open" : "Locked", 0});
    return result;
}
std::vector<FieldSnapshot> Game::fields() const {
    std::vector<FieldSnapshot> result;
    result.reserve(64); // Never reallocates, so no stale copies are freed.
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
        result.push_back({label, type, display.str(), exact.str(), raw(field), path, hex(reinterpret_cast<std::uintptr_t>(&field)), valid});
    };
    auto stringField = [&result](const std::string& label, const char* value, std::size_t size, const std::string& path) {
        std::string text = stringValue(value, size);
        const auto source = bulkCopy(value, size);
        std::ostringstream s; s << std::hex << std::uppercase << std::setfill('0');
        for (std::size_t i=0; i<std::min(size, source.size()); ++i) { if (i) s << ' '; s << std::setw(2) << unsigned(source[i]); }
        result.push_back({label, "char[" + std::to_string(size) + "]", text, text, s.str(), path, hex(reinterpret_cast<std::uintptr_t>(value)), true});
    };
    add("Module root", "World*", breakout_world_root, "module!breakout_world_root");
    result.back().valid = read(breakout_world_root) == world_;
    add("World.player", "Actor*", world_->player, "breakout_world_root -> World.player (+0)");
    result.back().valid = ownsActor(read(world_->player));
    add("World.enemies[0]", "Actor*", world_->enemies[0], "breakout_world_root -> World.enemies[0] (+8)");
    result.back().valid = ownsActor(read(world_->enemies[0]));
    add("World.room", "int32", world_->room, "breakout_world_root -> World.room (+24)");
    if (room_ == 3) {
        add("Remaining time", "float32", world_->remainingTime, "breakout_world_root -> World.remainingTime (+28)");
        add("Corridor distance", "float32", world_->corridorDistance, "breakout_world_root -> World.corridorDistance (+32)");
    }
    if (room_ == 6) add("Relay power", "float32", world_->corridorDistance, "breakout_world_root -> World (+32)");
    add("Door decision", "uint8", world_->doorOpen, "breakout_world_root -> World.doorOpen (+36)");
    auto actorFields = [&](Actor* actor, const std::string& prefix, const std::string& path) {
        if (!ownsActor(actor)) return;
        add(prefix + "health", "int32", actor->health, path + " -> Actor.health (+0)");
        add(prefix + "ammo", "int32", actor->ammo, path + " -> Actor.ammo (+4)");
        add(prefix + "speed", "float32", actor->speed, path + " -> Actor.speed (+8)");
        add(prefix + "charge", "float32", actor->charge, path + " -> Actor.charge (+12)");
        add(prefix + "X", "float32", actor->x, path + " -> Actor.x (+16)");
        add(prefix + "Y", "float32", actor->y, path + " -> Actor.y (+20)");
        add(prefix + "flags", "uint32", actor->flags, path + " -> Actor.flags (+24), power=bit0 alarm=bit1 maintenance=bit7");
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
    if (room_ == 10 || room_ == 11) actorFields(read(world_->enemies[0]), "Enemy ", "breakout_world_root -> World.enemies[0]");
    return result;
}
OutcomeSnapshot Game::outcome() const {
    OutcomeSnapshot result;
    result.complete = complete_; result.primaryObserved = primary_; result.restorationObserved = restored_;
    if (complete_) result.detail = "Room complete.";
    else if (primary_ && (room_ == 7 || room_ == 8 || room_ == 11 || room_ == 13) && !restored_)
        result.detail = room_ == 11 && !enemyHookVerified_ ? "Healing works. Wait for a sentinel shot, then restore the original code." : "It works. Now restore the original code and try once more.";
    return result;
}
std::vector<std::pair<std::string, std::string>> Game::liveText() const {
    std::vector<std::pair<std::string, std::string>> result;
    result.reserve(16);
    const auto address = [&result](const char* name, const void* pointer) {
        result.emplace_back(name, pointer ? hex(reinterpret_cast<std::uintptr_t>(pointer)) : std::string());
    };
    const auto at = [](const unsigned char* label, std::ptrdiff_t offset) { return label + offset; };
    const World* current = read(breakout_world_root);
    Actor* actor = player();
    Actor* enemy = read(world_->enemies[0]);
    address("rookie", actor);
    address("inventory", inventory(actor));
    address("weapon", equipped(actor));
    address("world", current == world_ ? current : nullptr);
    address("enemy", ownsActor(enemy) ? enemy : nullptr);
    address("root", &breakout_world_root);
    address("damage.site", breakout_damage_patchsite);
    address("damage.decoy", at(breakout_damage_patchsite, 1));      // tail of the sub: "sub [rdx],cl"
    address("ammo.site", breakout_ammo_patchsite);
    address("badge.read", at(breakout_badge_compare, -4));          // mov rdx,[rax+0x28]
    address("badge.decoy", at(breakout_badge_compare, -3));         // its tail: mov edx,[rax+0x28]
    address("vault.entry", breakout_vault_entry);
    address("vault.deny", at(breakout_vault_endpoint, -2));         // xor eax,eax
    result.emplace_back("override", std::to_string(overrideCode_));
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
    result.links = links_; result.ticks = ticks_; result.paused = paused_; result.outcome = outcome();
    return result;
}

bool selfCheck(std::string& report) {
    Game game;
    std::vector<std::string> failures;
    auto check = [&failures](bool condition, const std::string& message) { if (!condition) failures.push_back(message); };
    auto ticks = [&game](int count, float mx = 0, float my = 0) { for (int i = 0; i < count; ++i) game.advanceTick(mx, my); };
    const auto place = [&game](float x, float y) { Actor* a = game.world()->player; a->x = x; a->y = y; };
    const Actor* stablePlayer = game.world()->player;
    const auto teaching = game.teaching();
    check(teaching.ammoSite - teaching.signature == 16, "signature displacement");
    check(breakout_ammo_patchsite[0] == 0xff && breakout_ammo_patchsite[1] == 0x08, "DEC teaching instruction");
    check(breakout_damage_patchsite[0] == 0x83 && breakout_damage_patchsite[1] == 0x28 && breakout_damage_patchsite[2] == 0x0a, "SUB teaching instruction");
    for (int i = 2; i < 26; ++i) check(breakout_ammo_patchsite[i] == 0x90, "ammo patch padding");
    for (int room = 0; room < Game::roomCount; ++room) {
        const std::string name = "room" + std::to_string(room) + " ";
        game.setRoom(room); Actor* a = game.world()->player;
        check(a == stablePlayer && !game.paused(), name + "stable player and running start");
        check(a->ammo == 12 && a->health == (room == 11 ? 50 : 100) && a->x == Game::SpawnX && a->y == Game::SpawnY, name + "starting values");
        const auto start = game.acceptance().ticks;
        game.setPaused(true); game.update(0.2, 1, 0); check(game.acceptance().ticks == start, name + "test freeze");
        game.setPaused(false); game.update(10, 1, 0); check(game.acceptance().ticks == start, name + "debugger time discarded");
        game.resetRoom(); a = game.world()->player;
        for (int i = 0; i < 3; ++i) game.perform(Action::FireOnce);
        check(a->ammo == 9 && !game.outcome().complete, name + "shooting works and completes nothing");
        game.resetRoom(); a = game.world()->player;
        switch (room) {
        case 0: ticks(600, 1, 0); check(!game.outcome().complete, name + "has no gameplay outcome"); break;
        case 1:
            for (int i = 0; i < 30; ++i) game.perform(Action::FireOnce);
            check(!game.outcome().complete, name + "twelve shots cannot clear twenty drones");
            game.resetRoom(); for (int i = 0; i < 20; ++i) game.perform(Action::FireOnce);
            check(!game.outcome().complete, name + "a restart gives only another twelve shots");
            game.resetRoom(); a->ammo = 30; for (int i = 0; i < 20; ++i) game.perform(Action::FireOnce);
            check(game.outcome().complete && a->ammo == 10, name + "edited ammo clears all drones");
            { Zone zone; check(!game.interaction(zone) && game.zones().empty(), name + "no reload station exists"); }
            break;
        case 2:
            place(245, 105); ticks(60); check(a->charge > 40 && a->charge < 60, name + "charge pad raises charge");
            ticks(1200); check(a->charge == 60, name + "charge pad stops at the cap");
            place(245, 295); ticks(60); check(a->charge < 60, name + "drain vent lowers charge");
            place(245, 105); ticks(1200); place(545, 180); game.perform(Action::ActivateReactor);
            check(!game.outcome().complete, name + "ordinary charging cannot reach 90");
            a->charge = 95; game.perform(Action::ActivateReactor); check(game.outcome().complete, name + "edited charge activates");
            break;
        case 3: {
            place(200, 180); ticks(10, 1, 0); check(a->x > 200 && !game.renderSnapshot().trialRunning, name + "free walking before a run");
            game.perform(Action::StartCountdown);
            check(game.renderSnapshot().countdown > 2.9f && a->x == Game::SpawnX, name + "start puts ROOKIE on the line and counts down");
            ticks(181, 1, 0); check(game.renderSnapshot().trialRunning && a->x == Game::SpawnX, name + "countdown holds still, then runs");
            int guard = 0;
            while (game.renderSnapshot().fall == 0 && guard++ < 600) game.advanceTick(1, 0);
            check(!game.outcome().complete && !game.renderSnapshot().fail.empty() && guard > 400, name + "speed 60 is caught by the collapse near the end");
            const float fellAt = a->x; ticks(200, 1, 0);
            check(game.renderSnapshot().downed && a->x == fellAt, name + "after falling ROOKIE stays down until restarted");
            game.perform(Action::StartCountdown); check(!game.renderSnapshot().downed && a->x == Game::SpawnX, name + "try again restores the run");
            game.resetRoom(); game.perform(Action::StartTrial); a->speed = 6000; ticks(6, 1, 0);
            check(game.outcome().complete, name + "edited speed wins");
            game.resetRoom(); place(300, 30); ticks(1); check(a->y == 160, name + "bridge keeps ROOKIE on the deck");
            break;
        }
        case 4:
            check(a->flags == (AlarmFlag | 0x80), name + "maintenance and alarm bits start set");
            game.perform(Action::ToggleAlarm); game.perform(Action::EvaluateDoor);
            check(!game.outcome().complete && a->flags == 0x80, name + "switch clears only the alarm bit");
            a->keycard = 1; a->flags = PowerFlag; game.perform(Action::EvaluateDoor);
            check(!game.outcome().complete, name + "wiping the maintenance bit is rejected");
            a->flags = PowerFlag | 0x80; game.perform(Action::EvaluateDoor);
            check(game.outcome().complete, name + "preserving edit opens the door");
            game.resetRoom(); ticks(120, 1, 0); place(600, 180); ticks(60, 1, 0); check(a->x <= 610, name + "closed door blocks movement");
            break;
        case 5: {
            for (int i = 0; i < 6; ++i) game.perform(Action::FireOnce);
            game.perform(Action::SwapWeapon); for (int i = 0; i < 6; ++i) game.perform(Action::FireOnce);
            check(!game.outcome().complete && game.acceptance().targetsRemaining == 2, name + "damage 5 cannot break armor");
            game.resetRoom();
            auto* old = a->inventory->equipped; old->damage = 30; game.perform(Action::FireOnce);
            game.perform(Action::SwapWeapon); auto* replacement = a->inventory->equipped;
            check(old != replacement && replacement->damage == 7, name + "swap allocates a fresh weapon");
            replacement->damage = 30; game.perform(Action::FireOnce); check(game.outcome().complete, name + "both upgrades complete");
            break;
        }
        case 6: {
            World* first = game.world();
            check(breakout_world_root == first && first->room == 6, name + "root points at a room-6 block");
            ticks(10); check(game.acceptance().links == 0 && !first->doorOpen, name + "relay starts unpowered");
            first->corridorDistance = 100; ticks(1); check(game.acceptance().links == 1 && first->doorOpen, name + "power opens the relay");
            game.perform(Action::RebootRelay); World* second = game.world();
            check(second != first && breakout_world_root == second && second->room == 6 && second->player == stablePlayer, name + "reboot moves the block");
            ticks(1); check(!second->doorOpen && game.acceptance().links == 1, name + "new block starts closed");
            first->corridorDistance = 100; ticks(1); check(game.acceptance().links == 1, name + "stale block edits do nothing");
            second->corridorDistance = 100; ticks(1); game.perform(Action::RebootRelay);
            game.world()->corridorDistance = 100; ticks(1);
            check(game.outcome().complete, name + "three links complete");
            break;
        }
        case 7:
            for (int i = 0; i < 20; ++i) game.perform(Action::TakeOneHit);
            check(!game.outcome().complete && a->health > 0, name + "hits cannot fake invulnerability and respawn");
            game.resetRoom(); place(500, 200); ticks(70); check(a->health == 90, name + "turret zone hits after a moment");
            place(200, 200); ticks(120); check(a->health == 90, name + "safe outside the zone");
            break;
        case 8:
            for (int i = 0; i < 20; ++i) game.perform(Action::FireOnce);
            check(!game.outcome().complete, name + "ordinary shots never increase ammo"); break;
        case 9:
            place(500, 180); ticks(5); check(game.renderSnapshot().scanning && !game.outcome().complete, name + "pad scans ROOKIE and rejects it");
            std::strcpy(a->callsign, "ENGINEERS"); a->clearance = EngineerClearance; ticks(5);
            check(!game.outcome().complete, name + "longer callsign rejected");
            std::memset(a->callsign, 0, sizeof(a->callsign)); std::strcpy(a->callsign, "ENGINEER"); ticks(5);
            check(game.outcome().complete && game.world()->doorOpen, name + "secret callsign opens the gate");
            break;
        case 10:
            place(400, 330); ticks(100);
            check(game.acceptance().hits == 1 && game.world()->enemies[0]->health == 90, name + "press hits sentinel then player");
            a->clearance = game.decoyCode_; game.perform(Action::EvaluateDoor);
            check(!game.outcome().complete, name + "decoy code rejected");
            check(game.overrideCode_ >= 1000 && game.overrideCode_ <= 9999 && game.overrideCode_ != game.decoyCode_, name + "distinct four-digit codes");
            a->clearance = game.overrideCode_; game.perform(Action::EvaluateDoor);
            check(game.outcome().complete, name + "player's code completes");
            break;
        case 11:
            ticks(160); check(game.acceptance().enemyShots == 1, name + "sentinel fires on its own");
            for (int i = 0; i < 20; ++i) game.perform(Action::FireOnce);
            check(!game.outcome().complete && a->health == 50, name + "ordinary firing never heals"); break;
        case 12:
            for (int i = 0; i < 8; ++i) { game.perform(Action::EvaluateDoor); check(!game.outcome().complete, name + "vault refuses defaults"); }
            a->keycard = 1; a->clearance = EngineerClearance; a->flags = 0;
            game.perform(Action::EvaluateDoor); check(game.outcome().complete, name + "fixed inputs open the vault"); break;
        case 13:
            for (int i = 0; i < 20; ++i) game.perform(Action::FireOnce);
            check(!game.outcome().complete, name + "ordinary shots never increase ammo"); break;
        }
    }
    game.setRoom(6); game.perform(Action::RebootRelay); game.setRoom(1);
    check(game.world()->room == 1 && game.world()->player == stablePlayer, "relocated world survives room changes");
    game.setRoom(3); Actor* a = game.world()->player;
    auto count = game.acceptance().ticks; game.update(0.24, 1, 0); check(game.acceptance().ticks <= count + 6, "bounded catch-up");
    a->speed = std::numeric_limits<float>::quiet_NaN(); count = game.acceptance().ticks;
    game.advanceTick(1, 0); check(game.acceptance().ticks == count, "non-finite movement suspended");
    game.setRoom(5); a = game.world()->player;
    a->inventory = reinterpret_cast<Inventory*>(std::uintptr_t(1));
    check(!game.perform(Action::FireOnce) && a->ammo == 12, "invalid inventory pointer safely suspends fire");
    check(!game.fields().empty(), "invalid pointer snapshot safely displays raw values");
    {
        const auto live = game.liveText();
        const auto rookie = std::find_if(live.begin(), live.end(), [](const auto& entry) { return entry.first == "rookie"; });
        check(rookie != live.end() && rookie->second == hex(reinterpret_cast<std::uintptr_t>(a)), "live text names ROOKIE's address");
    }
    game.resetRoom(); a = game.world()->player; a->inventory->equipped = reinterpret_cast<Weapon*>(std::uintptr_t(1));
    check(!game.perform(Action::FireOnce) && a->ammo == 12, "invalid equipped pointer safely suspends fire");
    game.resetRoom(); game.world()->player = reinterpret_cast<Actor*>(std::uintptr_t(1));
    check(!game.perform(Action::FireOnce) && !game.renderSnapshot().playerValid, "invalid actor pointer safely suspends action and rendering");
    game.resetRoom(); breakout_world_root = reinterpret_cast<World*>(std::uintptr_t(1));
    check(!game.perform(Action::FireOnce), "invalid world root safely suspends action");
    game.resetRoom();
    check(!game.teaching().ammoPatched && !game.teaching().damagePatched, "self-check never patches teaching code");
    std::ostringstream output;
    if (failures.empty()) output << "ReClass Breakout self-check passed: all 14 rooms, ordinary-play barriers, edited outcomes, zones, countdown, relay relocation, pointer safety and teaching layout. Code-patch/restoration workflows require the guided ReClass acceptance pass.";
    else { output << "ReClass Breakout self-check failed:"; for (const auto& failure : failures) output << "\n- " << failure; }
    report = output.str(); return failures.empty();
}
} // namespace breakout

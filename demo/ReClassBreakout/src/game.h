#pragma once

#include <generated/layout.h>
#include <array>
#include <cstdint>
#include <memory>
#include <string>
#include <utility>
#include <vector>

namespace breakout {

// Room actions. Players reach them through world interactions (E at a
// console, standing on a pad, shooting); hidden Ctrl shortcuts reach the same
// actions deterministically for the acceptance harness.
enum class Action {
    FireOnce, ChargeOnce, DrainOnce, ActivateReactor, AdvanceTick,
    ToggleAlarm, EvaluateDoor, SwapWeapon, TakeOneHit, HitEnemy, EnemyFire,
    StartTrial, StartCountdown, RebootRelay
};
struct ActionDefinition { Action action; std::string label; bool available; };
// Addresses are kept as text: a binary copy left in freed heap memory would
// show up in the player's pointer scans as a false result.
struct FieldSnapshot {
    std::string label, type, value, roundTrip, rawHex, path, address;
    bool valid = true;
};
// A rectangular area of the room: a pad you stand on, or a console you use with E.
struct Zone {
    std::string id, label;
    float x = 0, y = 0, w = 0, h = 0;
    bool interact = false;
    Action action = Action::AdvanceTick;
    bool occupied = false;
};
struct SceneObject {
    enum class Kind { Target, Enemy, Door, Reactor, Exit, Turret };
    Kind kind = Kind::Target;
    float x = 0, y = 0, radius = 18;
    bool active = true;
    std::string label;
    int health = 0;
};
// Presentation copies of fields the lessons scan for are stored as doubles, so
// a 4-byte scan finds only the real field (an int64 copy would still match:
// its low dword equals the value).
struct Count {
    double value = 0;
    Count() = default;
    Count(double v) : value(v) {}
    operator long long() const { return static_cast<long long>(value); }
};
struct RenderSnapshot {
    float playerX = 70, playerY = 180;
    double playerSpeed = 0, charge = 0;
    float worldWidth = 800, worldHeight = 400;
    double remainingTime = 0, relayPower = 0;
    float corridorDistance = 0, countdown = 0;
    float collapseX = 0, fall = 0; // Room 3: collapse front and fall progress (0..1)
    Count health, ammo;
    bool playerValid = true, doorOpen = false, trialRunning = false, paused = false;
    std::vector<SceneObject> objects;
    std::vector<Zone> zones;
    // Presentation-only copies; the authoritative values stay in the layout structs.
    std::uint8_t keycard = 0;
    std::uint32_t flags = 0, clearance = 0, faction = 0;
    std::string callsign, weaponName, fail;
    Count weaponDamage, enemyHealth, enemyAmmo;
    float projectileSpeed = 0;
    int shots = 0, hits = 0, enemyHits = 0, enemyShots = 0, swaps = 0, links = 0, failSerial = 0;
    bool primary = false, complete = false, scanning = false, downed = false;
};
struct OutcomeSnapshot {
    bool complete = false, primaryObserved = false, restorationObserved = false;
    std::string detail;
};
struct TeachingSnapshot {
    std::uintptr_t worldRoot = 0, ammoSite = 0, damageSite = 0, vaultEntry = 0;
    std::uintptr_t vaultEndpoint = 0, signature = 0, badgeSite = 0;
    bool ammoPatched = false, damagePatched = false, vaultPatched = false;
    std::string signaturePattern;
};
struct AcceptanceSnapshot {
    int room = 1, shots = 0, hits = 0, enemyShots = 0, targetsRemaining = 0;
    int weaponSwaps = 0, destroyedBeforeSwap = 0, destroyedAfterSwap = 0, links = 0;
    std::uint64_t ticks = 0;
    bool paused = false;
    OutcomeSnapshot outcome;
};

class Game {
public:
    Game();
    ~Game();
    Game(const Game&) = delete;
    Game& operator=(const Game&) = delete;
    int room() const { return room_; }
    void setRoom(int room);
    void resetRoom(); // Gameplay data only: never writes executable memory.
    // A hidden test freeze; players never see a paused simulation.
    bool paused() const { return paused_; }
    void setPaused(bool paused) { paused_ = paused; accumulator_ = 0; }
    void update(double elapsedSeconds, float movementX = 0, float movementY = 0);
    void advanceTick(float movementX = 0, float movementY = 0);
    bool perform(Action action);
    void setAim(float x, float y) { aimX_ = x; aimY_ = y; hasAim_ = true; }
    void clearAim() { hasAim_ = false; }
    std::vector<ActionDefinition> actions() const;
    std::vector<Zone> zones() const;
    // The console within reach of the player, if any.
    bool interaction(Zone& zone) const;
    RenderSnapshot renderSnapshot() const;
    std::vector<FieldSnapshot> fields() const;
    OutcomeSnapshot outcome() const;
    TeachingSnapshot teaching() const;
    // Current addresses ("0x…") and values named for {{live.NAME}} lesson
    // tokens. Reads pointers and code labels only, never fields a lesson
    // watches, and returns text only.
    std::vector<std::pair<std::string, std::string>> liveText() const;
    AcceptanceSnapshot acceptance() const;
    const std::string& status() const { return status_; }
    World* world() { return world_; }
    const World* world() const { return world_; }
    static constexpr float fixedStep = 1.0f / 60.0f;
    static constexpr int roomCount = 14;
    static constexpr float SpawnX = 70, SpawnY = 180;
private:
    Actor* player() const;
    Inventory* inventory(const Actor* actor) const;
    Weapon* equipped(const Actor* actor) const;
    bool ownsActor(const Actor* pointer) const;
    bool ownsInventory(const Inventory* pointer) const;
    bool ownsWeapon(const Weapon* pointer) const;
    bool validateActor(const Actor* actor);
    bool fire(Actor* actor, bool enemy);
    bool hit(Actor* actor);
    bool evaluateDoor();
    void tick(float movementX, float movementY);
    void updateCompletion();
    void initializeWeapon(Weapon& weapon, const char* name, int damage, float speed, float cooldown);
    bool inZone(const Actor* actor, const char* id) const;
    static std::vector<Zone> layoutZones(int room);
    void respawn(const std::string& reason);
    void relocateWorld();
    void beginRun(bool countdown);
    static constexpr float FallSeconds = 1.2f;
    friend bool selfCheck(std::string& report);
    // The World moves in Room 6 (relay reboots); breakout_world_root always points at the current one.
    std::vector<std::unique_ptr<World>> worlds_;
    World* world_ = nullptr;
    std::array<std::unique_ptr<Actor>, 3> actors_;
    std::array<std::unique_ptr<Inventory>, 3> inventories_;
    std::vector<std::unique_ptr<Weapon>> weapons_;
    std::vector<SceneObject> targets_;
    int room_ = 1, shots_ = 0, hits_ = 0, enemyHits_ = 0, enemyShots_ = 0;
    int swaps_ = 0, beforeSwap_ = 0, afterSwap_ = 0, unchangedHits_ = 0, increasingShots_ = 0;
    int links_ = 0, failSerial_ = 0;
    bool paused_ = false, primary_ = false, restored_ = false, complete_ = false;
    bool enemyHookVerified_ = false, overrideAccepted_ = false, relayCounted_ = false, downed_ = false;
    // Room 10: passed to the shared damage routine in r9d; only player hits carry the real code.
    std::uint32_t overrideCode_ = 0, decoyCode_ = 0;
    double accumulator_ = 0;
    float hazardTimer_ = 0, enemyTimer_ = 0, countdown_ = 0, collapseX_ = 0, fallTimer_ = 0, aimX_ = 0, aimY_ = 0;
    bool hasAim_ = false;
    std::uint64_t ticks_ = 0;
    std::string status_, fail_;
    std::array<unsigned char, 24> originalAmmo_{}, originalDamage_{}, originalVault_{};
};

// A bounded sanity batch. Does not patch executable code or open a window.
bool selfCheck(std::string& report);

} // namespace breakout

extern "C" {
extern breakout::World* breakout_world_root;
void breakout_decrement_ammo(breakout::Actor* actor);
void breakout_apply_damage(breakout::Actor* actor, std::uint32_t code);
int breakout_evaluate_vault(breakout::Actor* actor);
int breakout_scan_badge(breakout::Actor* actor);
extern unsigned char breakout_ammo_patchsite[], breakout_damage_patchsite[];
extern unsigned char breakout_vault_entry[], breakout_vault_endpoint[], breakout_ammo_signature[], breakout_badge_compare[];
}

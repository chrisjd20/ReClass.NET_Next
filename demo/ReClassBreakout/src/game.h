#pragma once

#include <generated/layout.h>
#include <array>
#include <cstdint>
#include <memory>
#include <string>
#include <vector>

namespace breakout {

enum class Action {
    FireOnce, Reload, ChargeOnce, DrainOnce, ActivateReactor, AdvanceTick,
    ToggleKeycard, TogglePower, ToggleAlarm, EvaluateDoor, SwapWeapon,
    TakeOneHit, HitEnemy, EnemyFire, StartTrial, ToggleTurret,
    AcknowledgeProject, AcknowledgeDiscovery, AcknowledgeTrace, AcknowledgeRestart
};
struct ActionDefinition { Action action; std::string label; bool available; };
struct FieldSnapshot {
    std::string label, type, value, roundTrip, rawHex, path;
    std::uintptr_t address = 0;
    bool valid = true;
};
struct SceneObject {
    enum class Kind { Target, Enemy, Door, Reactor, Exit, Turret };
    Kind kind = Kind::Target;
    float x = 0, y = 0, radius = 18;
    bool active = true;
    std::string label;
    int health = 0;
};
struct RenderSnapshot {
    float playerX = 70, playerY = 180, playerSpeed = 0, charge = 0;
    float worldWidth = 800, worldHeight = 400;
    float remainingTime = 0, corridorDistance = 0;
    int health = 0, ammo = 0;
    bool playerValid = true, doorOpen = false, trialRunning = false;
    std::vector<SceneObject> objects;
    // Presentation-only copies; the authoritative values stay in the layout structs.
    std::uint8_t keycard = 0;
    std::uint32_t flags = 0, clearance = 0, faction = 0;
    std::string callsign, weaponName;
    int weaponDamage = 0, enemyHealth = 0, enemyAmmo = 0;
    float projectileSpeed = 0;
    int shots = 0, hits = 0, enemyHits = 0, enemyShots = 0, swaps = 0;
    bool turret = false, primary = false, complete = false, scanning = false;
};
struct OutcomeSnapshot {
    bool complete = false, primaryObserved = false, restorationObserved = false;
    bool manualRequired = false, manualAcknowledged = false;
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
    int weaponSwaps = 0, destroyedBeforeSwap = 0, destroyedAfterSwap = 0;
    std::uint64_t ticks = 0;
    bool paused = true;
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
    bool paused() const { return paused_; }
    void setPaused(bool paused) { paused_ = paused; accumulator_ = 0; }
    void focusLost() { setPaused(true); }
    void update(double elapsedSeconds, float movementX = 0, float movementY = 0);
    void advanceTick(float movementX = 0, float movementY = 0);
    bool perform(Action action);
    void setAim(float x, float y) { aimX_ = x; aimY_ = y; hasAim_ = true; }
    void clearAim() { hasAim_ = false; }
    std::vector<ActionDefinition> actions() const;
    RenderSnapshot renderSnapshot() const;
    std::vector<FieldSnapshot> fields() const;
    OutcomeSnapshot outcome() const;
    TeachingSnapshot teaching() const;
    AcceptanceSnapshot acceptance() const;
    const std::string& status() const { return status_; }
    World* world() { return &world_; }
    const World* world() const { return &world_; }
    static constexpr float fixedStep = 1.0f / 60.0f;
    static constexpr int roomCount = 12;
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
    bool onScanner(const Actor* actor) const;
    friend bool selfCheck(std::string& report);
    World world_{};
    std::array<std::unique_ptr<Actor>, 3> actors_;
    std::array<std::unique_ptr<Inventory>, 3> inventories_;
    std::vector<std::unique_ptr<Weapon>> weapons_;
    std::vector<SceneObject> targets_;
    int room_ = 1, shots_ = 0, hits_ = 0, enemyHits_ = 0, enemyShots_ = 0, reloads_ = 0;
    int swaps_ = 0, beforeSwap_ = 0, afterSwap_ = 0, unchangedHits_ = 0, increasingShots_ = 0;
    bool paused_ = true, primary_ = false, restored_ = false, complete_ = false;
    bool manual_ = false, enemyHookVerified_ = false, turret_ = false, overrideAccepted_ = false;
    // Room 9: passed to the shared damage routine in r9d; only player hits carry the real code.
    std::uint32_t overrideCode_ = 0, decoyCode_ = 0;
    double accumulator_ = 0;
    float automaticTimer_ = 0, aimX_ = 0, aimY_ = 0;
    bool hasAim_ = false;
    std::uint64_t ticks_ = 0;
    std::string status_;
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

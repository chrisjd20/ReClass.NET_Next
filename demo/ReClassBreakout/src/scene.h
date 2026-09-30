#pragma once
// Presentation of the simulation: world rendering, per-room set pieces and
// visual effects. Nothing here writes gameplay memory; effects are derived
// from RenderSnapshot differences only.
#include "game.h"
#include "sprites.h"
#include "raylib.h"
#include <string>
#include <vector>

namespace breakout {
struct SceneView {
    Rectangle viewport{}; // logical UI coordinates
    Vector2 origin{};     // logical position of world (0, 0)
    float worldScale = 1; // logical units per world unit
    Vector2 toWorld(Vector2 logical) const { return {(logical.x - origin.x) / worldScale, (logical.y - origin.y) / worldScale}; }
    Vector2 toScreen(Vector2 world) const { return {origin.x + world.x * worldScale, origin.y + world.y * worldScale}; }
    static SceneView fit(Rectangle viewport, Rectangle area);
};

class Scene {
public:
    void load();
    void unload();
    // A room change or reset: forget previous values without effects.
    void reset(const RenderSnapshot& snapshot);
    // Changes between frames come from outside the process (ReClass edits).
    void observeExternal(const RenderSnapshot& before, const RenderSnapshot& now, int room);
    // Changes during this frame's own actions and ticks.
    void observeInternal(const RenderSnapshot& before, const RenderSnapshot& after, int room, Vector2 aim, bool aimed);
    void attempt(int room, bool accepted);
    void update(float dt, const RenderSnapshot& snapshot);
    // render() must run outside any Camera2D; present() draws inside the UI camera.
    void render(const SceneView& view, float uiScale, const RenderSnapshot& snapshot, int room, Vector2 aim, bool showAim);
    void present(const SceneView& view, bool crt);
    struct Edit { std::string text; double at = -100; };
    const Edit& lastEdit() const { return lastEdit_; }
    int edits() const { return edits_; }
private:
    struct Particle { Vector2 position, velocity; float life, maximum, size; Color color; };
    struct Tracer { Vector2 from, to; float life, maximum; Color color; float width; };
    struct Floater { Vector2 position; std::string text; float life, maximum; Color color; bool glitch; };
    void burst(Vector2 at, Color color, int count, float speed, float size = 3);
    void floater(Vector2 at, const std::string& text, Color color, bool glitch = false);
    void tracer(Vector2 from, Vector2 to, Color color, float width = 3, float life = .18f);
    void drawEnvironment(const RenderSnapshot& snapshot, int room);
    void drawObjects(const RenderSnapshot& snapshot, int room);
    void drawPlayer(const RenderSnapshot& snapshot, int room, Vector2 aim, bool showAim);
    void drawEffects();
    Sprites sprites_;
    RenderTexture2D target_{};
    Shader crt_{};
    int crtTime_ = -1, crtGlitch_ = -1, crtResolution_ = -1;
    std::vector<Particle> particles_;
    std::vector<Tracer> tracers_;
    std::vector<Floater> floaters_;
    float time_ = 0, shake_ = 0, glitch_ = 0, flash_ = 0, doorOpen_ = 0, walk_ = 0, completeGlow_ = 0;
    Color flashColor_{176, 132, 255, 255};
    Vector2 lastPlayer_{70, 180};
    bool facingLeft_ = false, moving_ = false;
    Edit lastEdit_;
    int edits_ = 0;
};
} // namespace breakout

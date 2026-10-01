#pragma once
#include <string>
#include <vector>

namespace breakout {
// One instruction. `where` is "game" or "reclass"; text marks UI labels with
// backticks. `check` names an event the game can observe (empty = manual).
// `loop` marks the end of a round the player may need to repeat.
struct TutorialStep {
    std::string where;
    std::string text;
    std::string check;
    // 1-based step to return to when one round isn't enough (0 = no loop).
    int loop = 0;
    // One line on why this step works, and what the player should see afterwards.
    std::string why, see;
};
struct Lesson {
    int id;
    std::string title;
    std::string chapter;
    std::string goal;
    std::vector<TutorialStep> steps;
    std::string learned;
    std::string restore;
};
const std::vector<Lesson>& Lessons();
const std::string& FindRookieRecipe();
}

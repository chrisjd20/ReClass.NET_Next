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
// A longer, plain-language explanation shown in the Explain window. Text uses
// blank lines between blocks; a block of "- " lines is a bullet list, a block
// starting with "> " is a callout, and a block starting with "```" is code.
struct ExplainSection {
    std::string title;
    std::string text;
};
struct Lesson {
    int id;
    std::string title;
    std::string chapter;
    std::string goal;
    std::vector<TutorialStep> steps;
    std::string learned;
    std::string restore;
    std::vector<ExplainSection> explain;
};
const std::vector<Lesson>& Lessons();
const std::string& FindRookieRecipe();
}

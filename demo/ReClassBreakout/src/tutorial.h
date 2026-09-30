#pragma once
#include <string>
#include <vector>

namespace breakout {
struct TutorialStep {
    std::string title;
    std::string text;
    std::string expected;
};
struct Lesson {
    int id;
    std::string title;
    std::string objective;
    std::string concept;
    std::vector<TutorialStep> steps;
    std::vector<std::string> hints;
    std::vector<std::string> solutions;
    std::string restoration;
};
const std::vector<Lesson>& Lessons();
}

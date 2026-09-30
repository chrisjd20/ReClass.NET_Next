#include "progress.h"
#include <algorithm>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <sstream>

namespace breakout {
void Progress::load() {
    const char* base = nullptr;
#ifdef _WIN32
    base = std::getenv("LOCALAPPDATA");
    if (!base || !*base) base = std::getenv("APPDATA");
    if (base && *base) path = (std::filesystem::path(base) / "ReClassBreakout" / "progress.ini").string();
#else
    base = std::getenv("XDG_STATE_HOME");
    if (base && *base && std::filesystem::path(base).is_absolute())
        path = (std::filesystem::path(base) / "reclass-breakout" / "progress.ini").string();
    else if ((base = std::getenv("HOME")) && *base)
        path = (std::filesystem::path(base) / ".local" / "state" / "reclass-breakout" / "progress.ini").string();
#endif
    if (path.empty()) { diagnostic = "User-local progress directory unavailable; this session will not be saved."; return; }
    std::ifstream file(path);
    std::string line;
    int version = 1;
    while (std::getline(file, line)) {
        const auto equal = line.find('=');
        if (equal == std::string::npos) continue;
        const auto key = line.substr(0, equal);
        int value = 0;
        std::istringstream number(line.substr(equal + 1));
        if (!(number >> value)) continue;
        if (key == "version") version = value;
        else if (key == "room") room = std::clamp(value, 1, 12);
        else if (key == "text_size") textSize = std::clamp(value, 13, 22);
        else if (key == "addresses") addresses = value != 0;
        else if (key == "hex") hex = value != 0;
        else if (key == "hints") hints = value != 0;
        else if (key == "solutions") solutions = value != 0;
        else if (key == "readout") readout = value != 0;
        else if (key == "drawer") drawer = value != 0;
        else if (key == "crt") crt = value != 0;
        else {
            for (int i = 0; i < 12; ++i) {
                if (key == "complete_" + std::to_string(i + 1)) completed[i] = value != 0;
                if (key == "step_" + std::to_string(i + 1)) steps[i] = std::clamp(value, 0, 100);
            }
        }
    }
    // Earlier rules allowed these badges to be earned with ordinary controls.
    // Retain unrelated progress and display preferences during migration.
    if (version < 2) { completed[1] = false; completed[3] = false; }
    // Version 3 changed the Room 5 gate and replaced Room 9's self-report with an override code.
    if (version < 3) { completed[4] = false; completed[8] = false; }
}

bool Progress::save() {
    if (path.empty()) return false;
    std::error_code error;
    std::filesystem::create_directories(std::filesystem::path(path).parent_path(), error);
    if (error) { diagnostic = "Could not create the user-local progress directory."; return false; }
    // No addresses, code bytes, gameplay fields, or patch definitions are persisted.
    const auto temporary = path + ".tmp";
    std::ofstream file(temporary, std::ios::trunc);
    if (!file) { diagnostic = "Could not save tutorial progress."; return false; }
    file << "version=3\nroom=" << room << "\ntext_size=" << textSize
         << "\naddresses=" << addresses << "\nhex=" << hex
         << "\nhints=" << hints << "\nsolutions=" << solutions
         << "\nreadout=" << readout << "\ndrawer=" << drawer << "\ncrt=" << crt << '\n';
    for (int i = 0; i < 12; ++i)
        file << "complete_" << i + 1 << '=' << completed[i] << "\nstep_" << i + 1 << '=' << steps[i] << '\n';
    file.close();
    if (!file) { diagnostic = "Could not finish saving tutorial progress."; return false; }
#ifdef _WIN32
    // Windows rename cannot replace an existing destination. Both files are user progress only.
    std::filesystem::remove(path, error);
    error.clear();
#endif
    std::filesystem::rename(temporary, path, error);
    if (error) { diagnostic = "Could not commit tutorial progress."; return false; }
    diagnostic.clear();
    return true;
}
}

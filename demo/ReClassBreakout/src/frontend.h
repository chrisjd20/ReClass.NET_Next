#pragma once
#include <string>
namespace breakout {
// unlockAll opens every room (developer/acceptance use only).
int runFrontend(int initialRoom, const std::string& attachmentDiagnostic, bool unlockAll = false);
}

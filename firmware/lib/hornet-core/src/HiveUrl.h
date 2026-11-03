#pragma once

// The hive's address as typed in the console or sent by the wizard: "192.168.10.4" or "hive.local:8443" is taken as
// http://..., a trailing slash dropped. Arduino-free for native tests.

#include <string>

namespace hornet {

inline std::string hiveUrl(std::string url) {
    while (!url.empty() && (url.front() == ' ' || url.front() == '\t')) url.erase(0, 1);
    while (!url.empty() && (url.back() == ' ' || url.back() == '\t' || url.back() == '/')) url.pop_back();
    if (url.empty()) return url;
    if (url.rfind("http://", 0) != 0 && url.rfind("https://", 0) != 0) url = "http://" + url;
    return url;
}

}  // namespace hornet

#pragma once
// One physical button as a person means it, kept free of Arduino so the host tests cover it.
//
// The contacts of the panel's buttons chatter, and the e-ink refresh blocks the loop for a second or two, so a press can be seen twice:
// a glitch in the middle of a hold looks like a release and a new press. This reads a button the careful way:
//  - the level counts only after it has been the same for kStableMs (chatter shorter than that is nothing);
//  - a short press is acted on once, when it is let go; a long one once, while it is still held (kLongMs);
//  - a press that begins while the button is locked (lockFor: right after the screen changed) is ignored whole, short and long,
//    and the button must be let go before it counts again.
#include <cstdint>

namespace hornet::buttons {

constexpr uint32_t kStableMs = 30;
constexpr uint32_t kLongMs = 2000;
constexpr uint32_t kExitLockMs = 400;  // after any action a second EXIT is not believed for this long
constexpr uint32_t kMenuLockMs = 200;

enum class Out : uint8_t { None, Short, Long };

class Button {
public:
    /// <down>: the pin reads pressed right now; <now>: millis(). Call it every loop (at least every ~10 ms).
    Out poll(bool down, uint32_t now) {
        if (down != raw_) {
            raw_ = down;
            rawSince_ = now;
        }
        if (raw_ != stable_ && now - rawSince_ >= kStableMs) {
            stable_ = raw_;
            if (stable_) {  // the press began (at rawSince_, when the level first changed)
                downAt_ = rawSince_;
                longDone_ = false;
                ignored_ = locked_ && static_cast<int32_t>(rawSince_ - lockUntil_) < 0;
            } else {        // let go
                const bool tap = !ignored_ && !longDone_;
                ignored_ = false;
                longDone_ = false;
                if (tap) return Out::Short;
            }
        }
        if (stable_ && !longDone_ && !ignored_ && now - downAt_ >= kLongMs) {
            longDone_ = true;
            return Out::Long;
        }
        return Out::None;
    }

    /// Presses that begin before <now> + <ms> are ignored, and so is one that is going on now (a finger still on the button after the
    /// screen refreshed). Call it when a view changed, after the refresh.
    void lockFor(uint32_t now, uint32_t ms) {
        locked_ = true;
        lockUntil_ = now + ms;
        if (stable_ || raw_) ignored_ = true;
    }

    /// The button is down (or about to be): the panel stays awake for it.
    bool busy() const { return stable_ || raw_; }

private:
    bool raw_ = false, stable_ = false;
    uint32_t rawSince_ = 0, downAt_ = 0, lockUntil_ = 0;
    bool longDone_ = false, ignored_ = false, locked_ = false;
};

}  // namespace hornet::buttons

#pragma once
// The panel's buttons as a person means them, kept free of Arduino so the host tests cover it.
//
// The contacts chatter, and the e-ink refresh blocks the loop for a second or two, so one click can be seen twice (a glitch in the
// middle of a hold looks like a release and a new press; edges that came during a refresh are stacked and replayed afterwards).
//
//  - Button: MENU and EXIT, polled. The level counts only after it held kStableMs; a short press is acted on once, when it is let go; a
//    long one once, while still held (kLongMs); a press that begins while the button is locked (lockFor, right after the screen changed) is
//    ignored whole, and the button must be let go before it counts again.
//  - PressCounter: the rocker's up, down and press, read from their edges in an interrupt. Only the press edge (falling) counts, never the
//    release; it counts only when the contact had been let go and was quiet for kReleaseQuietUs, and not within kRockerGapUs of the last
//    accepted press: nobody clicks twice in a quarter of a second, so every edge in that time is chatter.
#include <cstdint>

namespace hornet::buttons {

constexpr uint32_t kStableMs = 50;     // a level counts after it held this long: a touch shorter than this is no press either
constexpr uint32_t kLongMs = 2000;
constexpr uint32_t kExitLockMs = 600;  // after any action a second EXIT is not believed for this long
constexpr uint32_t kMenuLockMs = 400;  // MENU too (a page turn): more than the quarter second a person can click in

constexpr int64_t kRockerGapUs = 300000;      // after an accepted press every edge of that contact is ignored this long
constexpr int64_t kReleaseQuietUs = 40000;    // it must have been let go and stable this long before it can fire again

enum class Out : uint8_t { None, Short, Long };

class Button {
public:
    /// <down>: the pin reads pressed right now; <now>: millis(). Call it every loop (at least every ~10 ms).
    Out poll(bool down, uint32_t now) {
        if (down != raw_) {
            raw_ = down;
            rawSince_ = now;
            edges_++;
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
                if (tap) return accepted(now, Out::Short);
            }
        }
        if (stable_ && !longDone_ && !ignored_ && now - downAt_ >= kLongMs) {
            longDone_ = true;
            return accepted(now, Out::Long);
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

    /// For the debug log, about the action just accepted: raw level changes since the one before, and the time since it (0: the first).
    uint32_t lastEdges() const { return lastEdges_; }
    uint32_t lastGapMs() const { return lastGapMs_; }

private:
    Out accepted(uint32_t now, Out out) {
        lastEdges_ = edges_;
        edges_ = 0;
        lastGapMs_ = haveAccepted_ ? now - lastAcceptedAt_ : 0;
        haveAccepted_ = true;
        lastAcceptedAt_ = now;
        return out;
    }

    bool raw_ = false, stable_ = false;
    uint32_t rawSince_ = 0, downAt_ = 0, lockUntil_ = 0;
    bool longDone_ = false, ignored_ = false, locked_ = false;
    uint32_t edges_ = 0, lastEdges_ = 0, lastGapMs_ = 0, lastAcceptedAt_ = 0;
    bool haveAccepted_ = false;
};

/// One contact of the rocker read from its edges (call at every change, from the interrupt; nowUs: microseconds, down: the pin is low).
struct PressCounter {
    bool released = true;                      // let go since the last press (a contact held at start gives no press until then)
    int64_t lastRiseUs = -1000000000000LL;     // when it was last let go
    int64_t lastCountUs = -1000000000000LL;    // the last accepted press
    uint32_t edges = 0;                        // raw edges since the last accepted press
    uint32_t lastEdges = 0, lastGapMs = 0;     // about the press just accepted, for the debug log (0 gap: the first)
    bool havePress = false;

    /// True when this edge is a press to act on.
    bool onEdge(bool down, int64_t nowUs) {
        edges++;
        if (!down) {  // let go: never counted, but it arms the next press
            released = true;
            lastRiseUs = nowUs;
            return false;
        }
        const bool counts = released && nowUs - lastRiseUs >= kReleaseQuietUs && nowUs - lastCountUs >= kRockerGapUs;
        released = false;  // the contact is low until the next rising edge
        if (!counts) return false;
        lastEdges = edges;
        edges = 0;
        lastGapMs = havePress ? static_cast<uint32_t>((nowUs - lastCountUs) / 1000) : 0;
        havePress = true;
        lastCountUs = nowUs;
        return true;
    }
};

// ------------------------------------------------------------------ what a key does in the book reader

/// What the reader's views hear from the buttons.
enum class Key : uint8_t { None, Up, Down, Press, Menu, MenuLong, Exit, ExitLong };

enum class SettingsAction : uint8_t { None, MoveUp, MoveDown, Change, Back };

/// In the settings: the rocker's up and down move between rows, MENU changes the value of the row, EXIT goes back. The rocker's PRESS does
/// nothing here: its contact fired twice from one tap (the value changed, then at once changed again), and a value that is changed twice by
/// one finger is worse than a button that does not answer.
inline SettingsAction settingsAction(Key key) {
    switch (key) {
        case Key::Up: return SettingsAction::MoveUp;
        case Key::Down: return SettingsAction::MoveDown;
        case Key::Menu: return SettingsAction::Change;
        case Key::Exit: return SettingsAction::Back;
        default: return SettingsAction::None;  // Press, MenuLong, ExitLong (the reader handles that one before)
    }
}

/// In a book the settings open with the rocker's press, or with MENU held 2 s (a deliberate hold that cannot fire twice).
inline bool opensSettings(Key key) { return key == Key::Press || key == Key::MenuLong; }

}  // namespace hornet::buttons

#pragma once

#include "Config.h"
#include "IDisplay.h"

// The "ANTI STRESS BUTTON" minigame (see Brobot.Sender's Anti-Stress card) —
// a solo Pong-style bounce, paddle at the bottom, controlled by the PC's
// arrow keys. Exclusive full-screen mode: while isActive() is true,
// main.cpp renders this instead of Personality/Face entirely (the same
// trick already used for the WiFi setup portal and the "waiting for PC"
// screen) rather than adding another Personality tier, so nothing — not
// even NOTIFY — can interrupt a round. Brobot.Sender only ever reports
// which arrow key changed state (see PROTOCOL.md's PONG commands); every
// bit of physics, collision, scoring and rendering lives here.
class PongGame {
public:
    // Parses one PONG command's arguments (everything after "PONG "):
    // "START", "STOP", or "KEY <LEFT|RIGHT> <DOWN|UP>".
    void onCommand(const char* args, unsigned long now);

    void update(unsigned long now);        // no-op while !isActive()
    void render(IDisplay& display) const;  // no-op while !isActive()

    bool isActive() const { return _active; }

    // Edge-triggered: true exactly once, on the frame the ball is missed and
    // the round moves to its Game Over screen — main.cpp uses this to fire
    // the one PONG OVER <score> line back to the PC (see PROTOCOL.md), so
    // Brobot.Sender knows to stop listening to the keyboard. Reading it
    // clears it. A PONG STOP does NOT set this — main.cpp already knows the
    // round ended because it sent that command itself.
    bool justEnded();

    int lastScore() const { return _score; }

private:
    enum class SubState { PLAYING, GAME_OVER };

    bool _active = false;
    SubState _subState = SubState::PLAYING;
    bool _justEnded = false;

    bool _leftHeld = false;
    bool _rightHeld = false;
    float _paddleX = 0.0f; // left edge

    float _ballX = 0.0f; // top-left corner
    float _ballY = 0.0f;
    float _velX = 0.0f; // px/ms
    float _velY = 0.0f;

    int _score = 0;
    unsigned long _lastUpdateMs = 0;
    unsigned long _gameOverStartedAt = 0;

    void onKeyArgs(const char* args);
    void startRound(unsigned long now);
    void stop();
    void clampBallSpeed();
    void renderGameOver(IDisplay& display) const;
};

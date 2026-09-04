#include "PongGame.h"

#include <stdio.h>
#include <string.h>

namespace {
// Must match the renderer's per-character advance (Font5x7 on the PC side,
// the built-in font at textSize 1 on the physical display) — Face.cpp keeps
// its own private copy of this same constant, for the same reason: centering
// text by hand needs to know how wide each character actually draws.
constexpr int CHAR_ADVANCE_PX = 6;
constexpr int LINE_GAP_PX = 4;
}  // namespace

void PongGame::onCommand(const char* args, unsigned long now) {
    if (strncmp(args, "START", 5) == 0) {
        startRound(now);
    } else if (strncmp(args, "STOP", 4) == 0) {
        stop();
    } else if (strncmp(args, "KEY ", 4) == 0) {
        onKeyArgs(args + 4);
    }
    // Anything else is ignored, same leniency Protocol::dispatch already
    // applies to unrecognized commands.
}

void PongGame::onKeyArgs(const char* args) {
    bool isLeft;
    if (strncmp(args, "LEFT", 4) == 0) {
        isLeft = true;
        args += 4;
    } else if (strncmp(args, "RIGHT", 5) == 0) {
        isLeft = false;
        args += 5;
    } else {
        return;
    }

    while (*args == ' ') {
        args++;
    }

    // Anything other than an exact "DOWN" is treated as released, not just
    // "UP" specifically — a malformed/truncated line should fail toward the
    // paddle stopping, not toward it getting stuck sliding forever.
    bool down = strcmp(args, "DOWN") == 0;
    if (isLeft) {
        _leftHeld = down;
    } else {
        _rightHeld = down;
    }
}

void PongGame::startRound(unsigned long now) {
    _active = true;
    _subState = SubState::PLAYING;
    _justEnded = false;
    _score = 0;
    _leftHeld = false;
    _rightHeld = false;

    _paddleX = (float)(LOGICAL_WIDTH - PONG_PADDLE_WIDTH_PX) / 2.0f;
    _ballX = (float)(LOGICAL_WIDTH - PONG_BALL_SIZE_PX) / 2.0f;
    _ballY = (float)LOGICAL_HEIGHT / 2.0f;
    _velX = (random(0, 2) == 0 ? -1.0f : 1.0f) * PONG_BALL_BASE_SPEED_PX_PER_MS;
    _velY = PONG_BALL_BASE_SPEED_PX_PER_MS;  // downward, toward the paddle

    _lastUpdateMs = now;
}

void PongGame::stop() {
    // An explicit STOP is not "the ball got past you" — main.cpp already
    // knows the round is over because it just sent this command, so
    // justEnded() must stay false or a PONG OVER would follow a STOP that
    // didn't need one.
    _active = false;
    _justEnded = false;
}

bool PongGame::justEnded() {
    if (!_justEnded) {
        return false;
    }
    _justEnded = false;
    return true;
}

void PongGame::clampBallSpeed() {
    if (_velX > PONG_BALL_MAX_SPEED_PX_PER_MS) _velX = PONG_BALL_MAX_SPEED_PX_PER_MS;
    if (_velX < -PONG_BALL_MAX_SPEED_PX_PER_MS) _velX = -PONG_BALL_MAX_SPEED_PX_PER_MS;
    if (_velY > PONG_BALL_MAX_SPEED_PX_PER_MS) _velY = PONG_BALL_MAX_SPEED_PX_PER_MS;
    if (_velY < -PONG_BALL_MAX_SPEED_PX_PER_MS) _velY = -PONG_BALL_MAX_SPEED_PX_PER_MS;
}

void PongGame::update(unsigned long now) {
    if (!_active) {
        return;
    }

    unsigned long dt = now - _lastUpdateMs;
    _lastUpdateMs = now;

    if (_subState == SubState::GAME_OVER) {
        if (now - _gameOverStartedAt >= PONG_GAME_OVER_HOLD_MS) {
            _active = false;
        }
        return;
    }

    float paddleDelta = PONG_PADDLE_SPEED_PX_PER_MS * (float)dt;
    if (_leftHeld && !_rightHeld) {
        _paddleX -= paddleDelta;
    } else if (_rightHeld && !_leftHeld) {
        _paddleX += paddleDelta;
    }
    float maxPaddleX = (float)(LOGICAL_WIDTH - PONG_PADDLE_WIDTH_PX);
    if (_paddleX < 0.0f) _paddleX = 0.0f;
    if (_paddleX > maxPaddleX) _paddleX = maxPaddleX;

    _ballX += _velX * (float)dt;
    _ballY += _velY * (float)dt;

    float maxBallX = (float)(LOGICAL_WIDTH - PONG_BALL_SIZE_PX);
    if (_ballX <= 0.0f) {
        _ballX = 0.0f;
        _velX = -_velX;
    } else if (_ballX >= maxBallX) {
        _ballX = maxBallX;
        _velX = -_velX;
    }
    if (_ballY <= 0.0f) {
        _ballY = 0.0f;
        _velY = -_velY;
    }

    float paddleTop = (float)PONG_PADDLE_Y;
    float ballBottom = _ballY + (float)PONG_BALL_SIZE_PX;
    bool overPaddleX = _ballX + (float)PONG_BALL_SIZE_PX >= _paddleX
        && _ballX <= _paddleX + (float)PONG_PADDLE_WIDTH_PX;

    if (_velY > 0.0f && ballBottom >= paddleTop
        && ballBottom <= paddleTop + (float)PONG_PADDLE_HEIGHT_PX && overPaddleX) {
        _ballY = paddleTop - (float)PONG_BALL_SIZE_PX;
        _velY = -_velY;

        // Deflect a bit based on where on the paddle it landed — same idea
        // as Breakout's paddle steering, so the player has some control over
        // the return angle instead of a purely vertical bounce every time.
        float paddleCenter = _paddleX + (float)PONG_PADDLE_WIDTH_PX / 2.0f;
        float ballCenter = _ballX + (float)PONG_BALL_SIZE_PX / 2.0f;
        _velX += (ballCenter - paddleCenter) * PONG_PADDLE_DEFLECTION_FACTOR;

        _velX *= PONG_BALL_SPEED_MULTIPLIER_PER_HIT;
        _velY *= PONG_BALL_SPEED_MULTIPLIER_PER_HIT;
        clampBallSpeed();

        _score++;
    } else if (_ballY > (float)LOGICAL_HEIGHT) {
        _subState = SubState::GAME_OVER;
        _gameOverStartedAt = now;
        _justEnded = true;
    }
}

void PongGame::render(IDisplay& display) const {
    if (!_active) {
        return;
    }

    if (_subState == SubState::GAME_OVER) {
        renderGameOver(display);
        return;
    }

    char scoreText[16];
    snprintf(scoreText, sizeof(scoreText), "SCORE %d", _score);
    display.drawText(scoreText, 4, 4, 255, 255, 255);

    display.fillRect((int)_paddleX, PONG_PADDLE_Y, PONG_PADDLE_WIDTH_PX, PONG_PADDLE_HEIGHT_PX, 255, 255, 255);
    display.fillRect((int)_ballX, (int)_ballY, PONG_BALL_SIZE_PX, PONG_BALL_SIZE_PX, 255, 255, 255);
}

void PongGame::renderGameOver(IDisplay& display) const {
    static const char* TITLE = "GAME OVER";
    char scoreText[24];
    snprintf(scoreText, sizeof(scoreText), "SCORE %d", _score);

    int titleWidth = (int)strlen(TITLE) * CHAR_ADVANCE_PX;
    int scoreWidth = (int)strlen(scoreText) * CHAR_ADVANCE_PX;
    int centerY = display.height() / 2;

    display.drawText(TITLE, (display.width() - titleWidth) / 2, centerY - CHAR_ADVANCE_PX - LINE_GAP_PX, 255, 255, 255);
    display.drawText(scoreText, (display.width() - scoreWidth) / 2, centerY + LINE_GAP_PX, 255, 255, 255);
}

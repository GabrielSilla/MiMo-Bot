#pragma once

#include <Arduino.h>

// Logical display resolution. Must always match the Brobot Virtual Display's
// resolution (Brobot.Display.Abstractions on the PC side).
constexpr int LOGICAL_WIDTH = 160;
constexpr int LOGICAL_HEIGHT = 128;

#ifndef VSCREEN
#define VSCREEN 1
#endif

// Pin wiring for a physical ST7735S. Only used when VSCREEN == 0.
// Chosen for the ESP32-C3 SuperMini specifically: GPIO2/8/9 are strapping
// pins (sampled at boot — an external circuit holding one low can prevent
// boot or force download mode) and GPIO8 also drives the SuperMini's
// onboard WS2812 LED, so all three are avoided here in favor of GPIO0/1/3/4/6/10,
// which carry no boot-time role on this chip.
constexpr uint8_t TFT_CS_PIN = 10;
constexpr uint8_t TFT_RST_PIN = 1;
constexpr uint8_t TFT_DC_PIN = 3;
constexpr uint8_t TFT_SCK_PIN = 4;
constexpr uint8_t TFT_MOSI_PIN = 6;

// Passive piezo buzzer (Buzzer.h), driven via tone()/noTone() — the one
// remaining pin from the same GPIO0/1/3/4/6/10 safe set above that isn't
// already claimed by the display.
constexpr uint8_t BUZZER_PIN = 0;

constexpr unsigned long SERIAL_BAUD_RATE = 115200;

// TCP port the ESP32 build's WiFi protocol server listens on for FACE/MSG/
// etc. commands from Brobot.Sender — same port BrobotCore/native's dev
// server uses (and the same default Brobot.Sender's TCP field already has),
// so switching from the local dev server to a real device is just changing
// the host field.
constexpr uint16_t PROTOCOL_TCP_PORT = 5555;

// ~60 fps. NOTE: this exceeds the 115200 baud link's ~11.5 KB/s budget by
// several times over (a frame's worth of draw commands, incl. the eyes'
// rounded-corner cuts in Face.cpp, easily runs 500+ bytes) — fine for now
// since development is TCP-only via BrobotCore/native while waiting on the
// ESP32 (see CLAUDE.md), but this MUST come back down (or the baud rate
// must go up) before this is flashed to real Serial hardware again, or
// frames will lag/garble.
constexpr unsigned long FRAME_INTERVAL_MS = 16;

// Pong minigame (PongGame.h/.cpp) — the "ANTI STRESS BUTTON" in Brobot.Sender.
// Exclusive full-screen mode, bypasses Personality/Face entirely while
// active (see main.cpp's loop()); Core owns every bit of physics/score.
constexpr int PONG_PADDLE_WIDTH_PX = 30;
constexpr int PONG_PADDLE_HEIGHT_PX = 4;
constexpr int PONG_PADDLE_Y = LOGICAL_HEIGHT - 10;
// px/ms the paddle slides while a direction key is held.
constexpr float PONG_PADDLE_SPEED_PX_PER_MS = 0.15f;
constexpr int PONG_BALL_SIZE_PX = 4;
// px/ms in each axis at the start of a round.
constexpr float PONG_BALL_BASE_SPEED_PX_PER_MS = 0.06f;
// Every successful paddle hit nudges the ball a little faster, capped here —
// an anti-stress toy that quietly ramps up the tension is the whole joke,
// but it shouldn't become unplayably fast.
constexpr float PONG_BALL_SPEED_MULTIPLIER_PER_HIT = 1.05f;
constexpr float PONG_BALL_MAX_SPEED_PX_PER_MS = 0.18f;
// How much a hit near the paddle's edge (vs. its center) steers the ball's
// horizontal velocity — gives the player some control over the return angle,
// same idea as Breakout's paddle deflection.
constexpr float PONG_PADDLE_DEFLECTION_FACTOR = 0.0025f;
// How long the "GAME OVER" screen holds before auto-reverting to
// Personality's own rendering (see PongGame::update / main.cpp's loop()).
constexpr unsigned long PONG_GAME_OVER_HOLD_MS = 4000;

// RPG battle minigame ("Batalha RPG" in Brobot.Sender) — a Final Fantasy-
// style turn-based fight against 2-3 randomly rolled enemies (see
// RpgBattle.h/.cpp). Same exclusive-mode bypass as the Pong block above;
// Protocol.cpp's dispatch is what keeps this and Pong from both being
// active at once.
constexpr int RPG_MIN_ENEMIES = 2;
constexpr int RPG_MAX_ENEMIES = 2;
constexpr int RPG_ENEMY_MAX_HP = 50;
constexpr int RPG_MIMO_MAX_HP = 100;
// Cura's heal roll, and every enemy's own attack roll.
constexpr int RPG_DAMAGE_MIN = 1;
constexpr int RPG_DAMAGE_MAX = 10; // inclusive
// MiMo's own offensive actions (Atacar/Bola de Fogo) roll from this wider,
// higher range instead — with 2 enemies at 50 HP each hitting back for
// 1-10 every single round, matching that same range made a full battle drag
// on far longer than a quick "anti-stress" break should. Cura still uses
// RPG_DAMAGE_MIN/MAX above; only the damage-dealing actions got faster.
constexpr int RPG_PLAYER_ATTACK_DAMAGE_MIN = 5;
constexpr int RPG_PLAYER_ATTACK_DAMAGE_MAX = 20; // inclusive
// How long a player action's (attack/fireball/heal) animation plays before
// resolving — damage/heal is applied at the halfway point (see
// RpgBattle::updatePlayerAction), so the HP number changes exactly when the
// hit "lands" on screen instead of the instant CONFIRM was pressed.
constexpr unsigned long RPG_PLAYER_ACTION_ANIM_MS = 900;
// Same idea, per enemy, during the enemy turn (see RpgBattle::updateEnemyAction).
constexpr unsigned long RPG_ENEMY_ATTACK_ANIM_MS = 700;
// How long the Victory/Defeat/Fled screen holds before auto-reverting —
// same role as PONG_GAME_OVER_HOLD_MS, kept as its own constant so the two
// minigames' timing can be tuned independently.
constexpr unsigned long RPG_END_HOLD_MS = 4000;

#pragma once

#include "Config.h"
#include "IDisplay.h"

// "Batalha RPG" — a solo Final Fantasy-style turn-based fight against 2
// randomly rolled tech-themed enemies, triggered by Brobot.Sender's Batalha
// RPG card. Exclusive full-screen mode, same shape as PongGame: while
// isActive() is true, main.cpp renders this instead of Personality/Face
// entirely rather than adding another Personality tier — see PongGame.h for
// the reasoning, which applies unchanged here. Brobot.Sender only ever
// reports which key changed state (left/right/confirm/escape); every bit of
// battle logic, damage rolls, turn order and animation timing lives here.
class RpgBattle {
public:
    // Parses one RPG command's arguments (everything after "RPG "):
    // "START", "STOP", "LEFT", "RIGHT", or "CONFIRM".
    void onCommand(const char* args, unsigned long now);

    void update(unsigned long now);                    // no-op while !isActive()
    void render(IDisplay& display, unsigned long now) const;  // no-op while !isActive()

    bool isActive() const { return _active; }

    // Edge-triggered, same contract as PongGame::justEnded(): true exactly
    // once, the moment the battle ends by the Core's own decision (victory,
    // defeat, or choosing Fugir) — main.cpp uses this to fire the one
    // RPG OVER <result> line back to the PC. An RPG STOP (Escape) never sets
    // this, same reasoning as PongGame::stop().
    bool justEnded();

    // "VICTORY", "DEFEAT" or "FLED" — valid only right after justEnded() has
    // returned true once.
    const char* lastResultToken() const { return _resultToken; }

private:
    enum class SubState {
        PLAYER_MENU,    // Atacar / Magias / Fugir
        SPELL_MENU,     // Bola de Fogo / Cura
        TARGET_SELECT,  // which enemy (for Atacar or Bola de Fogo)
        PLAYER_ACTION,  // animating the chosen action; damage/heal applied at its impact keyframe
        ENEMY_ACTION,   // each surviving enemy attacks in turn
        VICTORY,
        DEFEAT,
        FLED,
    };

    enum class EnemyKind : uint8_t { BUG, VIRUS, DRONE, FIREWALL, ROUTER, ROGUE_AI };

    enum class PlayerActionKind { ATTACK, FIREBALL, HEAL };

    struct EnemySlot {
        bool present = false;
        EnemyKind kind = EnemyKind::BUG;
        int hp = 0;
        bool alive() const { return present && hp > 0; }
    };

    bool _active = false;
    SubState _subState = SubState::PLAYER_MENU;
    bool _justEnded = false;
    const char* _resultToken = "VICTORY";

    EnemySlot _enemies[RPG_MAX_ENEMIES];
    int _enemyCount = 0;
    int _mimoHp = 0;

    int _menuCursor = 0;   // PLAYER_MENU: 0=Atacar, 1=Magias, 2=Fugir
    int _spellCursor = 0;  // SPELL_MENU: 0=Bola de Fogo, 1=Cura
    int _targetIndex = 0;  // TARGET_SELECT: index into _enemies

    // What TARGET_SELECT is choosing a target *for* — ATTACK or FIREBALL
    // (HEAL never reaches TARGET_SELECT, it always targets MiMo).
    PlayerActionKind _targetPurpose = PlayerActionKind::ATTACK;

    // The action currently resolving in PLAYER_ACTION.
    PlayerActionKind _pendingAction = PlayerActionKind::ATTACK;
    int _pendingTarget = 0;    // enemy slot index; unused for HEAL
    int _pendingValue = 0;     // rolled 1-10
    bool _pendingApplied = false;
    unsigned long _actionStartedMs = 0;

    // ENEMY_ACTION: walks the alive enemies one at a time.
    int _enemyTurnIndex = -1;
    unsigned long _enemyTurnStartedMs = 0;
    int _enemyTurnValue = 0;
    bool _enemyTurnApplied = false;
    bool _enemyTurnDefeatedMimo = false;

    unsigned long _endStartedMs = 0; // anchors the Victory/Defeat/Fled hold

    void start(unsigned long now);
    void stop();
    void onDirection(int dir);
    void onConfirm(unsigned long now);

    int aliveCount() const;
    int firstAliveIndex() const;
    int nextAliveIndex(int from, int dir) const;

    void beginPlayerAction(PlayerActionKind kind, int targetSlot, unsigned long now);
    void applyPendingAction();
    void updatePlayerAction(unsigned long now);

    void advanceEnemyTurn(unsigned long now);
    void updateEnemyAction(unsigned long now);

    void endBattle(SubState result, const char* token, unsigned long now);

    void renderField(IDisplay& display, unsigned long now) const;
    void renderMenu(IDisplay& display) const;
    void renderEndScreen(IDisplay& display) const;

    // Pure drawing helpers. Members (rather than free functions in the .cpp)
    // purely so they can name the private EnemyKind/PlayerActionKind enums;
    // the static ones touch no instance state, same "just a shape renderer"
    // spirit as Face::render itself.
    static void drawEnemySprite(IDisplay& display, EnemyKind kind, int x, int y, int size,
                                 unsigned long now, uint8_t r, uint8_t g, uint8_t b);
    static void drawMimoWarrior(IDisplay& display, int x, int y, bool hitFlash);
    static int lungeOffsetPx(unsigned long elapsedMs);
    void renderFireball(IDisplay& display, unsigned long now) const;
    void renderHealSparkles(IDisplay& display, unsigned long now) const;
    void renderEnemyBolt(IDisplay& display, unsigned long now) const;
};

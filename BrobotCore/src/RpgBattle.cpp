#include "RpgBattle.h"

#include <stdio.h>
#include <string.h>

// IDisplay::drawRoundedRect is an outline only on both real implementations
// (Adafruit_GFX's drawRoundRect on the physical build, an outer-minus-inner
// ring on the Simulator) — not a filled shape. Every sprite here is composed
// from plain fillRect blocks instead, same technique the rest of Face.cpp
// already uses for its own blocky icons (the coffee cup, the gamepad, the
// book) rather than attempting a filled circle IDisplay has no primitive for.
namespace {
constexpr int CHAR_ADVANCE_PX = 6;  // must match Font5x7's advance, see Face.cpp's own copy
constexpr int LINE_HEIGHT_PX = 10;

constexpr int FIELD_BOTTOM_Y = 92;   // menu strip starts here
constexpr int ENEMY_SLOT_X = 4;
constexpr int ENEMY_SLOT_HEIGHT = 30;
constexpr int ENEMY_SPRITE_SIZE = 18;

constexpr int MIMO_X = 112;
constexpr int MIMO_Y = 20;
constexpr int MIMO_WIDTH = 26;
constexpr int MIMO_HEIGHT = 46;
constexpr int MIMO_LUNGE_PX = 40; // how far MiMo advances toward the enemies on Atacar
constexpr int MIMO_EYE_SIZE = 10;
constexpr int MIMO_EYE_GAP = 4;

// Same small "staircase" corner cut Face.cpp's own eyes use (2px, then
// 1px), just scaled down — softens a plain square enough that it still
// reads as an eye instead of a bare block. Always cuts in background black
// regardless of the eye's own color, same as Face.cpp's own convention.
void drawSmallEye(IDisplay& display, int x, int y, int size, uint8_t r, uint8_t g, uint8_t b) {
    display.fillRect(x, y, size, size, r, g, b);
    display.fillRect(x, y, 2, 1, 0, 0, 0);
    display.fillRect(x, y, 1, 2, 0, 0, 0);
    display.fillRect(x + size - 2, y, 2, 1, 0, 0, 0);
    display.fillRect(x + size - 1, y, 1, 2, 0, 0, 0);
    display.fillRect(x, y + size - 1, 2, 1, 0, 0, 0);
    display.fillRect(x, y + size - 2, 1, 2, 0, 0, 0);
    display.fillRect(x + size - 2, y + size - 1, 2, 1, 0, 0, 0);
    display.fillRect(x + size - 1, y + size - 2, 1, 2, 0, 0, 0);
}

constexpr int MENU_TEXT_Y = 104;

const char* kEnemyNames[] = { "Bug", "Virus", "Drone", "Firewall", "Roteador", "IA Rebelde" };

int enemySlotY(int index) { return 2 + index * ENEMY_SLOT_HEIGHT; }
}  // namespace

void RpgBattle::onCommand(const char* args, unsigned long now) {
    if (strncmp(args, "START", 5) == 0) {
        start(now);
    } else if (strncmp(args, "STOP", 4) == 0) {
        stop();
    } else if (strncmp(args, "LEFT", 4) == 0) {
        onDirection(-1);
    } else if (strncmp(args, "RIGHT", 5) == 0) {
        onDirection(1);
    } else if (strncmp(args, "CONFIRM", 7) == 0) {
        onConfirm(now);
    }
    // Anything else is ignored, same leniency Protocol::dispatch already
    // applies to unrecognized commands.
}

void RpgBattle::start(unsigned long now) {
    _active = true;
    _subState = SubState::PLAYER_MENU;
    _justEnded = false;
    _menuCursor = 0;
    _spellCursor = 0;
    _mimoHp = RPG_MIMO_MAX_HP;
    _enemyTurnIndex = -1;
    _enemyTurnDefeatedMimo = false;

    _enemyCount = (int)random(RPG_MIN_ENEMIES, RPG_MAX_ENEMIES + 1);
    for (int i = 0; i < RPG_MAX_ENEMIES; i++) {
        _enemies[i] = EnemySlot();
        if (i < _enemyCount) {
            _enemies[i].present = true;
            _enemies[i].kind = (EnemyKind)random(0, 6);
            _enemies[i].hp = RPG_ENEMY_MAX_HP;
        }
    }
    _targetIndex = firstAliveIndex();
}

void RpgBattle::stop() {
    // An explicit STOP (Escape) is not a Core-decided ending — main.cpp
    // already knows the battle is over because it just sent this command,
    // so justEnded() must stay false or an RPG OVER would follow a STOP
    // that didn't need one. Same reasoning as PongGame::stop().
    _active = false;
    _justEnded = false;
}

int RpgBattle::aliveCount() const {
    int n = 0;
    for (int i = 0; i < _enemyCount; i++) {
        if (_enemies[i].alive()) n++;
    }
    return n;
}

int RpgBattle::firstAliveIndex() const {
    for (int i = 0; i < _enemyCount; i++) {
        if (_enemies[i].alive()) return i;
    }
    return -1;
}

int RpgBattle::nextAliveIndex(int from, int dir) const {
    if (_enemyCount == 0) {
        return from;
    }
    int i = from;
    for (int step = 0; step < _enemyCount; step++) {
        i = (i + dir + _enemyCount) % _enemyCount;
        if (_enemies[i].alive()) {
            return i;
        }
    }
    return from;
}

void RpgBattle::onDirection(int dir) {
    switch (_subState) {
        case SubState::PLAYER_MENU:
            _menuCursor = (_menuCursor + dir + 3) % 3;
            break;
        case SubState::SPELL_MENU:
            _spellCursor = (_spellCursor + dir + 2) % 2;
            break;
        case SubState::TARGET_SELECT:
            _targetIndex = nextAliveIndex(_targetIndex, dir);
            break;
        default:
            break;  // no cursor to move mid-animation or on an end screen
    }
}

void RpgBattle::onConfirm(unsigned long now) {
    switch (_subState) {
        case SubState::PLAYER_MENU:
            if (_menuCursor == 0) {  // Atacar
                // A lone survivor is an obvious target — skip straight past
                // a selection screen with only one thing to select.
                if (aliveCount() <= 1) {
                    beginPlayerAction(PlayerActionKind::ATTACK, firstAliveIndex(), now);
                } else {
                    _targetPurpose = PlayerActionKind::ATTACK;
                    _targetIndex = firstAliveIndex();
                    _subState = SubState::TARGET_SELECT;
                }
            } else if (_menuCursor == 1) {  // Magias
                _spellCursor = 0;
                _subState = SubState::SPELL_MENU;
            } else {  // Fugir — always succeeds, no failure roll
                endBattle(SubState::FLED, "FLED", now);
            }
            break;

        case SubState::SPELL_MENU:
            if (_spellCursor == 0) {  // Bola de Fogo
                if (aliveCount() <= 1) {
                    beginPlayerAction(PlayerActionKind::FIREBALL, firstAliveIndex(), now);
                } else {
                    _targetPurpose = PlayerActionKind::FIREBALL;
                    _targetIndex = firstAliveIndex();
                    _subState = SubState::TARGET_SELECT;
                }
            } else {  // Cura — always targets MiMo himself, no selection needed
                beginPlayerAction(PlayerActionKind::HEAL, -1, now);
            }
            break;

        case SubState::TARGET_SELECT:
            beginPlayerAction(_targetPurpose, _targetIndex, now);
            break;

        default:
            break;  // CONFIRM does nothing mid-animation or on an end screen
    }
}

void RpgBattle::beginPlayerAction(PlayerActionKind kind, int targetSlot, unsigned long now) {
    _pendingAction = kind;
    _pendingTarget = targetSlot;
    // Atacar/Bola de Fogo roll from the wider, higher damage range (see
    // Config.h) — only Cura keeps the original 1-10.
    _pendingValue = kind == PlayerActionKind::HEAL
        ? (int)random(RPG_DAMAGE_MIN, RPG_DAMAGE_MAX + 1)
        : (int)random(RPG_PLAYER_ATTACK_DAMAGE_MIN, RPG_PLAYER_ATTACK_DAMAGE_MAX + 1);
    _pendingApplied = false;
    _actionStartedMs = now;
    _subState = SubState::PLAYER_ACTION;
}

void RpgBattle::applyPendingAction() {
    switch (_pendingAction) {
        case PlayerActionKind::ATTACK:
        case PlayerActionKind::FIREBALL:
            if (_pendingTarget >= 0) {
                _enemies[_pendingTarget].hp -= _pendingValue;
                if (_enemies[_pendingTarget].hp < 0) {
                    _enemies[_pendingTarget].hp = 0;
                }
            }
            break;
        case PlayerActionKind::HEAL:
            _mimoHp += _pendingValue;
            if (_mimoHp > RPG_MIMO_MAX_HP) {
                _mimoHp = RPG_MIMO_MAX_HP;
            }
            break;
    }
}

void RpgBattle::updatePlayerAction(unsigned long now) {
    unsigned long elapsed = now - _actionStartedMs;

    // Damage/heal lands at the halfway point of the animation, not the
    // instant CONFIRM was pressed — the HP bar only changes when the hit
    // actually "arrives" on screen.
    if (!_pendingApplied && elapsed >= RPG_PLAYER_ACTION_ANIM_MS / 2) {
        applyPendingAction();
        _pendingApplied = true;
    }

    if (elapsed >= RPG_PLAYER_ACTION_ANIM_MS) {
        if (aliveCount() == 0) {
            endBattle(SubState::VICTORY, "VICTORY", now);
        } else {
            _enemyTurnIndex = -1;
            advanceEnemyTurn(now);
        }
    }
}

void RpgBattle::advanceEnemyTurn(unsigned long now) {
    int next = -1;
    for (int i = _enemyTurnIndex + 1; i < _enemyCount; i++) {
        if (_enemies[i].alive()) {
            next = i;
            break;
        }
    }

    if (next < 0) {
        // Every surviving enemy has had its turn — back to the player.
        _subState = SubState::PLAYER_MENU;
        _menuCursor = 0;
        _enemyTurnIndex = -1;
        return;
    }

    _subState = SubState::ENEMY_ACTION;
    _enemyTurnIndex = next;
    _enemyTurnStartedMs = now;
    _enemyTurnValue = (int)random(RPG_DAMAGE_MIN, RPG_DAMAGE_MAX + 1);
    _enemyTurnApplied = false;
}

void RpgBattle::updateEnemyAction(unsigned long now) {
    unsigned long elapsed = now - _enemyTurnStartedMs;

    if (!_enemyTurnApplied && elapsed >= RPG_ENEMY_ATTACK_ANIM_MS / 2) {
        _mimoHp -= _enemyTurnValue;
        if (_mimoHp <= 0) {
            _mimoHp = 0;
            _enemyTurnDefeatedMimo = true;
        }
        _enemyTurnApplied = true;
    }

    if (elapsed >= RPG_ENEMY_ATTACK_ANIM_MS) {
        if (_enemyTurnDefeatedMimo) {
            endBattle(SubState::DEFEAT, "DEFEAT", now);
        } else {
            advanceEnemyTurn(now);
        }
    }
}

void RpgBattle::endBattle(SubState result, const char* token, unsigned long now) {
    _subState = result;
    _resultToken = token;
    _endStartedMs = now;
    _justEnded = true;
}

bool RpgBattle::justEnded() {
    if (!_justEnded) {
        return false;
    }
    _justEnded = false;
    return true;
}

void RpgBattle::update(unsigned long now) {
    if (!_active) {
        return;
    }

    switch (_subState) {
        case SubState::PLAYER_MENU:
        case SubState::SPELL_MENU:
        case SubState::TARGET_SELECT:
            break;  // waiting on input, nothing to animate
        case SubState::PLAYER_ACTION:
            updatePlayerAction(now);
            break;
        case SubState::ENEMY_ACTION:
            updateEnemyAction(now);
            break;
        case SubState::VICTORY:
        case SubState::DEFEAT:
        case SubState::FLED:
            if (now - _endStartedMs >= RPG_END_HOLD_MS) {
                _active = false;
            }
            break;
    }
}

void RpgBattle::render(IDisplay& display, unsigned long now) const {
    if (!_active) {
        return;
    }

    if (_subState == SubState::VICTORY || _subState == SubState::DEFEAT || _subState == SubState::FLED) {
        renderEndScreen(display);
        return;
    }

    renderField(display, now);
    renderMenu(display);
}

void RpgBattle::renderField(IDisplay& display, unsigned long now) const {
    for (int i = 0; i < _enemyCount; i++) {
        if (!_enemies[i].alive()) {
            continue;  // defeated enemies simply vanish from the field
        }

        int slotY = enemySlotY(i);
        bool isTargeted = _subState == SubState::TARGET_SELECT && i == _targetIndex;
        bool isBeingHit = _subState == SubState::PLAYER_ACTION && _pendingApplied
            && (_pendingAction == PlayerActionKind::ATTACK || _pendingAction == PlayerActionKind::FIREBALL)
            && _pendingTarget == i
            && (now - _actionStartedMs) < RPG_PLAYER_ACTION_ANIM_MS / 2 + 200;

        uint8_t r = 255, g = 255, b = 255;
        if (isBeingHit && ((now / 60) % 2) == 0) {
            r = 255;
            g = 60;
            b = 60;  // brief red flash on impact
        }

        drawEnemySprite(display, _enemies[i].kind, ENEMY_SLOT_X, slotY, ENEMY_SPRITE_SIZE, now, r, g, b);

        if (isTargeted) {
            display.drawText(">", ENEMY_SLOT_X + ENEMY_SPRITE_SIZE + 2, slotY + 4, 255, 255, 255);
        }

        display.drawText(kEnemyNames[(int)_enemies[i].kind], ENEMY_SLOT_X, slotY + ENEMY_SPRITE_SIZE + 1, 255, 255, 255);

        int barWidth = 40;
        int barY = slotY + ENEMY_SPRITE_SIZE + 9;
        display.drawRect(ENEMY_SLOT_X, barY, barWidth, 4, 255, 255, 255);
        int fillWidth = (barWidth - 2) * _enemies[i].hp / RPG_ENEMY_MAX_HP;
        if (fillWidth > 0) {
            display.fillRect(ENEMY_SLOT_X + 1, barY + 1, fillWidth, 2, 255, 255, 255);
        }
    }

    int mimoX = MIMO_X;
    bool mimoFlash = false;
    if (_subState == SubState::PLAYER_ACTION && _pendingAction == PlayerActionKind::ATTACK) {
        mimoX -= lungeOffsetPx(now - _actionStartedMs);
    }
    if (_subState == SubState::ENEMY_ACTION && _enemyTurnApplied) {
        unsigned long half = RPG_ENEMY_ATTACK_ANIM_MS / 2;
        unsigned long sinceHit = (now - _enemyTurnStartedMs) - half;
        mimoFlash = sinceHit < 200 && ((sinceHit / 60) % 2) == 0;
    }
    drawMimoWarrior(display, mimoX, MIMO_Y, mimoFlash);

    char hpLine[20];
    snprintf(hpLine, sizeof(hpLine), "MIMO %d/%d", _mimoHp, RPG_MIMO_MAX_HP);
    display.drawText(hpLine, 96, 2, 255, 255, 255);

    if (_subState == SubState::PLAYER_ACTION && _pendingAction == PlayerActionKind::FIREBALL) {
        renderFireball(display, now);
    }
    if (_subState == SubState::PLAYER_ACTION && _pendingAction == PlayerActionKind::HEAL) {
        renderHealSparkles(display, now);
    }
    if (_subState == SubState::ENEMY_ACTION) {
        renderEnemyBolt(display, now);
    }
}

void RpgBattle::renderMenu(IDisplay& display) const {
    display.fillRect(0, FIELD_BOTTOM_Y, LOGICAL_WIDTH, 1, 255, 255, 255);

    switch (_subState) {
        case SubState::PLAYER_MENU: {
            static const char* options[3] = { "ATACAR", "MAGIAS", "FUGIR" };
            static const int xs[3] = { 10, 62, 114 };
            for (int i = 0; i < 3; i++) {
                if (i == _menuCursor) {
                    display.drawText(">", xs[i] - 8, MENU_TEXT_Y, 255, 255, 255);
                }
                display.drawText(options[i], xs[i], MENU_TEXT_Y, 255, 255, 255);
            }
            break;
        }
        case SubState::SPELL_MENU: {
            static const char* options[2] = { "BOLA DE FOGO", "CURA" };
            static const int xs[2] = { 10, 100 };
            for (int i = 0; i < 2; i++) {
                if (i == _spellCursor) {
                    display.drawText(">", xs[i] - 8, MENU_TEXT_Y, 255, 255, 255);
                }
                display.drawText(options[i], xs[i], MENU_TEXT_Y, 255, 255, 255);
            }
            break;
        }
        case SubState::TARGET_SELECT:
            display.drawText("ESCOLHA O ALVO", 10, MENU_TEXT_Y, 255, 255, 255);
            break;
        case SubState::PLAYER_ACTION: {
            const char* label = _pendingAction == PlayerActionKind::ATTACK ? "MIMO ATACA!"
                : _pendingAction == PlayerActionKind::FIREBALL ? "BOLA DE FOGO!"
                : "CURANDO...";
            display.drawText(label, 10, MENU_TEXT_Y, 255, 255, 255);
            break;
        }
        case SubState::ENEMY_ACTION: {
            if (_enemyTurnIndex >= 0) {
                char label[24];
                snprintf(label, sizeof(label), "%s ATACA!", kEnemyNames[(int)_enemies[_enemyTurnIndex].kind]);
                display.drawText(label, 10, MENU_TEXT_Y, 255, 255, 255);
            }
            break;
        }
        default:
            break;
    }
}

void RpgBattle::renderEndScreen(IDisplay& display) const {
    const char* title;
    switch (_subState) {
        case SubState::VICTORY:
            title = "VITORIA!";
            break;
        case SubState::DEFEAT:
            title = "DERROTA...";
            break;
        default:
            title = "VOCE FUGIU!";
            break;
    }

    int titleWidth = (int)strlen(title) * CHAR_ADVANCE_PX;
    int centerY = display.height() / 2;
    display.drawText(title, (display.width() - titleWidth) / 2, centerY - LINE_HEIGHT_PX, 255, 255, 255);
}

int RpgBattle::lungeOffsetPx(unsigned long elapsedMs) {
    unsigned long half = RPG_PLAYER_ACTION_ANIM_MS / 2;
    if (elapsedMs >= RPG_PLAYER_ACTION_ANIM_MS) {
        return 0;
    }
    if (elapsedMs <= half) {
        return (int)(MIMO_LUNGE_PX * elapsedMs / half);
    }
    unsigned long back = elapsedMs - half;
    unsigned long backDur = RPG_PLAYER_ACTION_ANIM_MS - half;
    if (backDur == 0) {
        return 0;
    }
    return (int)(MIMO_LUNGE_PX * (backDur - back) / backDur);
}

void RpgBattle::drawMimoWarrior(IDisplay& display, int x, int y, bool hitFlash) {
    uint8_t r = 255, g = 255, b = 255;
    if (hitFlash) {
        r = 255;
        g = 70;
        b = 70;
    }

    // Sword, held to the left toward the enemies: a narrow blade tapering
    // to a point, a crossguard near its base (not its tip), and a short
    // handle below that — a first version had a short thick blade under a
    // wide guard near the top, which read as a hammer rather than a sword.
    int swordX = x - 12;
    display.fillRect(swordX + 1, y, 1, 4, r, g, b);        // pointed tip
    display.fillRect(swordX, y + 4, 3, 16, r, g, b);       // blade
    display.fillRect(swordX - 3, y + 20, 9, 2, r, g, b);   // crossguard
    display.fillRect(swordX, y + 22, 3, 8, r, g, b);       // handle

    // MiMo has no body here, same as everywhere else on screen — just the
    // same two small rounded eyes the normal face uses, scaled down. A
    // first version drew a solid body block with the eyes cut into it in
    // background color, which read as a different character next to the
    // real face instead of a smaller MiMo.
    int eyesY = y + (MIMO_HEIGHT - MIMO_EYE_SIZE) / 2;
    int eyesX = x + (MIMO_WIDTH - (MIMO_EYE_SIZE * 2 + MIMO_EYE_GAP)) / 2;
    drawSmallEye(display, eyesX, eyesY, MIMO_EYE_SIZE, r, g, b);
    drawSmallEye(display, eyesX + MIMO_EYE_SIZE + MIMO_EYE_GAP, eyesY, MIMO_EYE_SIZE, r, g, b);
}

void RpgBattle::drawEnemySprite(IDisplay& display, EnemyKind kind, int x, int y, int size,
                                 unsigned long now, uint8_t r, uint8_t g, uint8_t b) {
    switch (kind) {
        case EnemyKind::BUG: {
            int bw = size - 8, bh = size / 2;
            int bx = x + 4, by = y + size / 4;
            display.fillRect(bx, by, bw, bh, r, g, b);
            for (int i = 0; i < 3; i++) {
                int ly = by + 1 + i * (bh / 3);
                display.fillRect(bx - 3, ly, 3, 1, r, g, b);
                display.fillRect(bx + bw, ly, 3, 1, r, g, b);
            }
            display.fillRect(bx + 1, by - 3, 1, 3, r, g, b);
            display.fillRect(bx + bw - 2, by - 3, 1, 3, r, g, b);
            break;
        }
        case EnemyKind::VIRUS: {
            int core = size / 2;
            int cx = x + size / 2 - core / 2, cy = y + size / 2 - core / 2;
            display.fillRect(cx, cy, core, core, r, g, b);
            static const int dx[8] = { 0, 1, 1, 1, 0, -1, -1, -1 };
            static const int dy[8] = { -1, -1, 0, 1, 1, 1, 0, -1 };
            int reach = core / 2 + 3;
            for (int i = 0; i < 8; i++) {
                int px = x + size / 2 + dx[i] * reach - 1;
                int py = y + size / 2 + dy[i] * reach - 1;
                display.fillRect(px, py, 2, 2, r, g, b);
            }
            break;
        }
        case EnemyKind::DRONE: {
            int bw = size / 2, bh = size / 3;
            int bx = x + (size - bw) / 2, by = y + (size - bh) / 2;
            display.fillRect(bx, by, bw, bh, r, g, b);
            display.fillRect(x, y, 5, 1, r, g, b);
            display.fillRect(x + size - 5, y, 5, 1, r, g, b);
            display.fillRect(x, y + size - 1, 5, 1, r, g, b);
            display.fillRect(x + size - 5, y + size - 1, 5, 1, r, g, b);
            if (((now / 300) % 2) == 0) {
                display.fillRect(bx + bw / 2 - 1, by + bh / 2 - 1, 2, 2, 255, 60, 60);
            }
            break;
        }
        case EnemyKind::FIREWALL: {
            for (int row = 0; row < 3; row++) {
                int rowY = y + size - 3 - row * 5;
                int offset = (row % 2 == 0) ? 0 : 3;
                for (int col = 0; col < 3; col++) {
                    display.fillRect(x + offset + col * 6, rowY, 5, 3, r, g, b);
                }
            }
            bool flicker = ((now / 200) % 2) == 0;
            int flameY = y + size - 12 - (flicker ? 2 : 0);
            display.fillRect(x + 3, flameY, 3, 4, 255, 140, 40);
            display.fillRect(x + size - 8, flameY - (flicker ? 0 : 2), 3, 4, 255, 140, 40);
            break;
        }
        case EnemyKind::ROUTER: {
            int bw = size - 4, bh = size / 3;
            int bx = x + 2, by = y + size - bh - 2;
            display.fillRect(bx, by, bw, bh, r, g, b);
            display.fillRect(x + 3, y, 1, by - y, r, g, b);
            display.fillRect(x + size - 4, y + 3, 1, by - y - 3, r, g, b);
            int lit = (int)((now / 250) % 3);
            for (int i = 0; i < 3; i++) {
                if (i == lit) {
                    display.fillRect(bx + 2 + i * 4, by + bh / 2 - 1, 2, 2, 60, 255, 60);
                } else {
                    display.fillRect(bx + 2 + i * 4, by + bh / 2 - 1, 2, 2, r, g, b);
                }
            }
            break;
        }
        case EnemyKind::ROGUE_AI: {
            int fw = size - 2, fh = size - 6;
            display.fillRect(x + 1, y, fw, fh, r, g, b);
            display.fillRect(x + 3, y + 2, fw - 4, fh - 4, 0, 0, 0);
            bool pulse = ((now / 150) % 3) != 0;
            uint8_t er = pulse ? 255 : 150;
            display.fillRect(x + size / 2 - 1, y + fh / 2 - 1, 2, 2, er, 0, 0);
            display.fillRect(x + size / 2 - 2, y + fh, 4, 3, r, g, b);
            break;
        }
    }
}

void RpgBattle::renderFireball(IDisplay& display, unsigned long now) const {
    unsigned long elapsed = now - _actionStartedMs;
    unsigned long half = RPG_PLAYER_ACTION_ANIM_MS / 2;

    int targetSlotY = enemySlotY(_pendingTarget);
    int targetX = ENEMY_SLOT_X + ENEMY_SPRITE_SIZE / 2;
    int targetY = targetSlotY + ENEMY_SPRITE_SIZE / 2;
    int startX = MIMO_X;
    int startY = MIMO_Y + MIMO_HEIGHT / 2;

    if (elapsed < half) {
        int px = startX + (targetX - startX) * (int)elapsed / (int)half;
        int py = startY + (targetY - startY) * (int)elapsed / (int)half;
        display.fillRect(px - 2, py - 2, 4, 4, 255, 140, 30);
    } else if (elapsed < half + 250) {
        display.fillRect(targetX - 3, targetY - 3, 6, 6, 255, 200, 80);
    }
}

void RpgBattle::renderHealSparkles(IDisplay& display, unsigned long now) const {
    unsigned long elapsed = now - _actionStartedMs;
    for (int i = 0; i < 3; i++) {
        unsigned long phase = (elapsed + i * 250) % 700;
        int riseY = (int)(phase * 20 / 700);
        int px = MIMO_X + 4 + i * 8;
        int py = MIMO_Y + MIMO_HEIGHT - riseY;
        display.fillRect(px, py, 2, 2, 120, 255, 140);
    }
}

void RpgBattle::renderEnemyBolt(IDisplay& display, unsigned long now) const {
    if (_enemyTurnIndex < 0) {
        return;
    }
    unsigned long elapsed = now - _enemyTurnStartedMs;
    unsigned long half = RPG_ENEMY_ATTACK_ANIM_MS / 2;
    if (elapsed >= half) {
        return;
    }

    int slotY = enemySlotY(_enemyTurnIndex);
    int startX = ENEMY_SLOT_X + ENEMY_SPRITE_SIZE / 2;
    int startY = slotY + ENEMY_SPRITE_SIZE / 2;
    int targetX = MIMO_X + MIMO_WIDTH / 2;
    int targetY = MIMO_Y + MIMO_HEIGHT / 2;

    int px = startX + (targetX - startX) * (int)elapsed / (int)half;
    int py = startY + (targetY - startY) * (int)elapsed / (int)half;
    display.fillRect(px - 1, py - 1, 3, 3, 200, 60, 255);
}

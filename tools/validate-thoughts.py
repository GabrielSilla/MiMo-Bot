"""Validates Pensamentos do Peemo's phrase data against specs/voice-guide.md.

Usage:
    python tools/validate-thoughts.py              # check both data files
    python tools/validate-thoughts.py --sample N   # also write a review sample
                                                   # (N random lines per category/group)
                                                   # to tools/thoughts-sample.md

Checks: column count, unique ids, known faces/levels/groups, rendered
length with the longest placeholder values, glyphs the display can draw,
uppercase accented letters (the font drops the accent), banned words,
SATELLITE only on satellite phrases (and space words under a non-space
face, as a warning), exact and near duplicates, and — for context groups —
that every mood a group can occur in has enough phrases.
Exit code 1 if there are errors (warnings don't fail).
"""
import difflib
import io
import os
import random
import re
import sys
import unicodedata
from collections import Counter, defaultdict

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DATA = os.path.join(ROOT, 'src', 'Brobot.Sender', 'Thoughts', 'Data')
FILES = {'base': os.path.join(DATA, 'peemo-base.tsv'), 'context': os.path.join(DATA, 'work-context.tsv')}

MAX_LEN = 75
FACES = {'NEUTRAL', 'HAPPY', 'SAD', 'ANGRY', 'SWEATING', 'SPACE', 'SATELLITE'}
LEVELS = {'-', 'leve', 'medio', 'acido'}
ALL_MOODS = {'leve', 'medio', 'acido'}

# Longest value each placeholder can realistically take (see
# WorkContextMessages.Situations/Duration).
PLACEHOLDERS = {'{tempo}': '10h30', '{site}': 'Instagram', '{jogo}': 'League of Legends', '{n}': '99', '{temp}': '38'}

# group -> moods it can occur in (by the hours it can hold). Groups not
# listed here can happen at any hour.
GROUP_MOODS = {
    'hora:madrugada': {'acido'},
    'hora:manha-cedo': {'acido', 'leve'},
    'hora:manha': {'leve'},
    'hora:almoco': {'leve'},
    'hora:tarde': {'leve', 'medio'},
    'hora:noite': {'medio', 'acido'},
    'combo:madrugada-commit': {'acido'},
    'combo:madrugada-jogo': {'acido'},
    'combo:sexta-noite': {'medio', 'acido'},
    'combo:segunda-cedo': {'acido', 'leve'},
    'jogo-expediente': {'leve', 'medio'},
    'clima:sol': {'acido', 'leve', 'medio'},          # 06-18 only
    'clima:noite-limpa': {'medio', 'acido'},          # 18-06, clear sky
    'combo:madrugada-youtube': {'acido'},
    'combo:madrugada-rede-social': {'acido'},
    'combo:madrugada-musica': {'acido'},
    'combo:madrugada-reuniao': {'acido'},
    'combo:madrugada-frio': {'acido'},
    'combo:madrugada-calor': {'acido'},
    'combo:manha-cedo-frio': {'acido', 'leve'},
    'combo:manha-cedo-neblina': {'acido', 'leve'},
    'combo:almoco-jogo': {'leve'},
    'combo:almoco-youtube': {'leve'},
    'combo:tempestade-noite': {'medio', 'acido'},
    'combo:sexta-noite-chuva': {'medio', 'acido'},
    'combo:domingo-noite-jogo': {'medio', 'acido'},
    'combo:reuniao-longa-sem-commit': {'medio', 'acido'},
}
KNOWN_GROUPS = set(GROUP_MOODS) | {
    'dia:seg', 'dia:ter', 'dia:qua', 'dia:qui', 'dia:sex', 'dia:sab', 'dia:dom',
    'clima:sol', 'clima:noite-limpa', 'clima:nublado', 'clima:chuva', 'clima:tempestade', 'clima:neblina', 'clima:frio', 'clima:calor',
    'data:natal', 'data:ano-novo', 'data:halloween', 'data:sexta-13', 'data:dia-programador',
    'data:inicio-mes', 'data:fim-mes',
    'rede-social', 'rede-social-muito', 'youtube', 'youtube-muito', 'musica-longa',
    'reuniao-longa', 'sem-reuniao', 'reuniao-acabou',
    'build-falhando', 'build-limpo', 'commits-muitos', 'commit-primeiro',
    'jogo-acabou', 'jogo-hora', 'jogo-muito', 'jogos-varios',
    'combo:sexta-calor', 'combo:segunda-chuva', 'combo:build-quebrado-jogo',
    'combo:sexta-build-quebrado', 'combo:segunda-sem-reuniao', 'combo:sexta-sem-reuniao',
    'combo:segunda-jogo', 'combo:fim-de-semana-commit', 'combo:fim-de-semana-reuniao',
    'combo:fim-de-semana-jogo', 'combo:chuva-reuniao-longa', 'combo:calor-reuniao-longa',
    'combo:calor-jogo-muito', 'combo:chuva-jogo', 'combo:chuva-youtube',
    'combo:reuniao-acabou-build-quebrado', 'combo:dia-dev-perfeito', 'combo:musica-build-limpo',
    'combo:rede-social-reuniao-longa', 'combo:sexta-13-build-quebrado', 'combo:natal-trabalho',
    'combo:ano-novo-jogo', 'combo:inicio-mes-segunda', 'combo:fim-mes-sexta',
}
# Placeholders each group actually fills in.
GROUP_VALUES = defaultdict(set, {
    'rede-social': {'{site}', '{tempo}'}, 'rede-social-muito': {'{site}', '{tempo}'},
    'youtube': {'{tempo}'}, 'youtube-muito': {'{tempo}'}, 'musica-longa': {'{tempo}'},
    'reuniao-longa': {'{tempo}'}, 'build-falhando': {'{n}'}, 'build-limpo': {'{n}'},
    'commits-muitos': {'{n}'}, 'jogo-acabou': {'{jogo}'}, 'jogo-expediente': {'{jogo}'},
    'jogo-hora': {'{tempo}'}, 'jogo-muito': {'{tempo}'}, 'jogos-varios': {'{n}'},
})
for g in KNOWN_GROUPS:
    if g.startswith('clima:'):
        GROUP_VALUES[g].add('{temp}')
MIN_PER_MOOD = 3  # phase 1: every mood a group can occur in needs at least this many

BANNED = [r'de novo', r'como sempre', r'rotina', r'sua mania', r'adorei', r'adoro', r'gostei', r'amei', r'ontem',
          r'volta a trabalhar', r'volta pro trabalho', r'vou contar pro seu chefe', r'pregui[cç]os',
          r'transmiss[aã]o', r'recebi']
SATELLITE_WORDS = r'sat[eé]lite|esta[cç][aã]o espacial|\biss\b|sputnik|hubble|tiangong|skylab'
SPACE_WORDS = SATELLITE_WORDS + r'|\bespa[cç]o\b|planeta|\blua\b|marte|j[uú]piter|saturno|gal[aá]xia|astronauta|\bnasa\b|[oó]rbita|foguete|cometa|asteroide|telesc[oó]pio|estrela|universo'


def drawable_chars():
    font = io.open(os.path.join(ROOT, 'src', 'Brobot.Display.Simulator', 'Font5x7.cs'), encoding='utf-8').read()
    return set(re.search(r'private const string Chars = "(.*)";', font).group(1))


def load(kind, path):
    rows = []
    for n, line in enumerate(io.open(path, encoding='utf-8'), 1):
        line = line.rstrip('\n').rstrip('\r')
        if not line or line.startswith('#'):
            continue
        rows.append((n, line.split('\t')))
    return rows


def norm(text):
    t = unicodedata.normalize('NFD', text.lower())
    t = ''.join(c for c in t if unicodedata.category(c) != 'Mn')
    return re.sub(r'[^a-z0-9 ]', '', t).strip()


def main():
    allowed = drawable_chars()
    errors, warnings = [], []
    entries = []  # (kind, id, cat/group, face, level, text, lineno)
    for kind, path in FILES.items():
        for n, cols in load(kind, path):
            where = f'{os.path.basename(path)}:{n}'
            if len(cols) != 5:
                errors.append(f'{where}: expected 5 tab-separated columns, got {len(cols)}')
                continue
            pid, cat, face, level, text = cols
            entries.append((kind, pid, cat, face, level, text, where))

    ids = Counter(e[1] for e in entries)
    for pid, c in ids.items():
        if c > 1:
            errors.append(f'id {pid} used {c} times')

    for kind, pid, cat, face, level, text, where in entries:
        if face not in FACES:
            errors.append(f'{where} {pid}: unknown face {face}')
        if level not in LEVELS:
            errors.append(f'{where} {pid}: unknown level {level}')
        if kind == 'context' and cat not in KNOWN_GROUPS:
            errors.append(f'{where} {pid}: unknown group {cat}')
        used = set(re.findall(r'\{[a-z]+\}', text))
        if kind == 'base' and used:
            errors.append(f'{where} {pid}: base phrases take no placeholders ({", ".join(used)})')
        if kind == 'context':
            extra = used - GROUP_VALUES[cat]
            if extra:
                errors.append(f'{where} {pid}: group {cat} doesn\'t fill {", ".join(sorted(extra))}')
        rendered = text
        for k, v in PLACEHOLDERS.items():
            rendered = rendered.replace(k, v)
        if len(rendered) > MAX_LEN:
            errors.append(f'{where} {pid}: {len(rendered)} chars rendered (max {MAX_LEN}): {rendered}')
        bad = sorted(set(c for c in rendered if c not in allowed))
        if bad:
            errors.append(f'{where} {pid}: characters the display can\'t draw: {"".join(bad)!r}')
        low = text.lower()
        for b in BANNED:
            if re.search(b, low):
                errors.append(f'{where} {pid}: banned wording /{b}/: {text}')
        if face == 'SATELLITE' and not re.search(SATELLITE_WORDS, low):
            errors.append(f'{where} {pid}: SATELLITE face but no satellite/station in the text')
        if face not in ('SPACE', 'SATELLITE') and re.search(SPACE_WORDS, low):
            warnings.append(f'{where} {pid}: space word under face {face} — should it be SPACE? {text}')

    # Duplicates: exact after normalization, then near (ratio >= 0.88)
    by_norm = defaultdict(list)
    for e in entries:
        by_norm[norm(e[5])].append(e[1])
    for k, v in by_norm.items():
        if len(v) > 1:
            errors.append(f'duplicate text: {", ".join(v)}')
    normed = [(e[1], norm(e[5])) for e in entries]
    for i in range(len(normed)):
        a_id, a = normed[i]
        sm = difflib.SequenceMatcher(None, a, '')
        for j in range(i + 1, len(normed)):
            b_id, b = normed[j]
            if a == b or abs(len(a) - len(b)) > 12:
                continue
            sm.set_seq2(b)
            if sm.real_quick_ratio() >= 0.88 and sm.quick_ratio() >= 0.88 and sm.ratio() >= 0.88:
                warnings.append(f'near-duplicate: {a_id} ~ {b_id}')

    # Context coverage: every mood a group can occur in needs phrases.
    per_group = defaultdict(Counter)
    for kind, pid, cat, face, level, text, where in entries:
        if kind == 'context':
            per_group[cat][level] += 1
    for g in sorted(KNOWN_GROUPS):
        counts = per_group.get(g, Counter())
        for mood in sorted(GROUP_MOODS.get(g, ALL_MOODS)):
            have = counts[mood] + counts['-']
            if have < MIN_PER_MOOD:
                errors.append(f'group {g}: only {have} phrase(s) usable when mood is {mood} (min {MIN_PER_MOOD})')

    for w in warnings:
        print('WARN ', w)
    for e in errors:
        print('ERROR', e)
    base = sum(1 for e in entries if e[0] == 'base')
    ctx = sum(1 for e in entries if e[0] == 'context')
    print(f'\n{base} base + {ctx} context phrases — {len(errors)} error(s), {len(warnings)} warning(s)')

    if '--sample' in sys.argv:
        n = int(sys.argv[sys.argv.index('--sample') + 1])
        groups = defaultdict(list)
        for e in entries:
            groups[(e[0], e[2])].append(e)
        out = ['# Pensamentos — amostra para revisão', '']
        for (kind, cat) in sorted(groups):
            out.append(f'## {kind} / {cat} ({len(groups[(kind, cat)])})')
            for e in random.sample(groups[(kind, cat)], min(n, len(groups[(kind, cat)]))):
                out.append(f'- `{e[3]}` `{e[4]}` {e[5]}')
            out.append('')
        path = os.path.join(ROOT, 'tools', 'thoughts-sample.md')
        io.open(path, 'w', encoding='utf-8').write('\n'.join(out))
        print('sample written to', path)

    sys.exit(1 if errors else 0)


if __name__ == '__main__':
    main()

"""
Wissensartikel für den Helpdesk aus der Hilfe von dmOffice erzeugen (Deutsch und Englisch).

Quellen: docs/handbuch.md und docs/manual.md (je Kapitel bzw. Unterabschnitt ein Artikel, die häufigen Fragen je Frage) und die
Themenseiten docs/*.de.md mit ihrer englischen Fassung docs/*.md. Ausgabe: DmOffice.Server/Helpdesk/help-articles.json - die
Knöpfe „Aus der Hilfe übernehmen“ in der Wissens-Ansicht legen daraus fehlende Artikel an.

Aufruf (im Repo-Hauptordner):  python tools/helpdesk-wissen.py
Nach Änderungen an der Hilfe neu ausführen und die JSON-Datei mit committen.
"""
import io
import json
import os
import re

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DOCS = os.path.join(ROOT, 'docs')
OUT = os.path.join(ROOT, 'DmOffice.Server', 'Helpdesk', 'help-articles.json')
MAX = 5000
# Themenseiten, die eher Entwicklerdokumentation sind als Hilfe
SKIP = {'nextcloud-clients.de.md', 'duplikate.de.md'}
STOP = {'und', 'oder', 'mit', 'der', 'die', 'das', 'für', 'von', 'den', 'dem', 'des', 'ein', 'eine', 'im', 'in', 'auf', 'aus', 'zu',
        'and', 'or', 'with', 'the', 'for', 'from', 'of', 'a', 'an', 'in', 'on', 'to', 'by', 'via', 'dmoffice'}


def read(name):
    return io.open(os.path.join(DOCS, name), encoding='utf-8').read().replace('\r', '')


def clean(md):
    # HTML-Kommentare weg: Schalter-Markierungen [feature:x] und ihre auskommentierten else-Teile (release-strip.mjs)
    md = re.sub(r"<!--.*?-->[ \t]*\n?", "", md, flags=re.S)
    # Bilder zuerst (sonst bliebe aus ![Kalender](bild.png) ein „!Kalender“), dann Fett und Links auch über Zeilenumbrüche hinweg
    md = re.sub(r'!\[[^\]]*\]\([^)]*\)', '', md, flags=re.S)
    md = re.sub(r'\*\*(.+?)\*\*', r'\1', md, flags=re.S)
    md = re.sub(r'\[([^\]]+)\]\([^)]*\)', r'\1', md, flags=re.S)
    lines = []
    for line in md.split('\n'):
        s = line.rstrip()
        # Hinweise für Entwickler und Sprachumschalter weglassen
        if re.match(r'^\s*(Code|Tests?|Siehe Code|See code)\s*:', s) or re.search(r'\.(cs|kt|tsx?)\b`?', s) and '`' in s and len(s) < 200 and ('Code' in s or 'Test' in s):
            continue
        if s.strip() in ('Deutsch', 'English'):   # Sprachumschalter als eigene Zeile
            continue
        if re.match(r'^\s*\*?\[?(Deutsch|English|Deutsche Fassung|English version)\]?', s) and ('·' in s or 'version' in s.lower() or 'Fassung' in s):
            continue
        s = re.sub(r'!\[[^\]]*\]\([^)]*\)', '', s)              # Bilder
        s = re.sub(r'\[([^\]]+)\]\([^)]*\)', r'\1', s)          # Links → Text
        s = re.sub(r'\*\*([^*]+)\*\*', r'\1', s)                # fett
        s = re.sub(r'(?<![\w*])\*([^*\n]+)\*(?![\w*])', r'\1', s)  # kursiv
        s = s.replace('`', '')
        s = re.sub(r'^#{1,6}\s*', '', s)                        # Zwischenüberschriften als Text
        s = re.sub(r'^\|.*\|$', lambda m: ' · '.join(c.strip() for c in m.group(0).strip('|').split('|') if c.strip() and not set(c.strip()) <= set('-: ')), s)
        lines.append(s)
    text = re.sub(r'\n{3,}', '\n\n', '\n'.join(lines)).strip()
    if len(text) > MAX:
        cut = text[:MAX]
        text = cut[:cut.rfind('\n\n')] if '\n\n' in cut else cut
        text += '\n\n…'
    return text


def keys(title):
    words = [w for w in re.findall(r'[\wÄÖÜäöüß-]+', title.lower()) if len(w) > 2 and w not in STOP]
    return ', '.join(dict.fromkeys(words))


def handbook(md, lang):
    """Kapitel „## N. Titel“, Unterabschnitte „### …“; „Häufige Fragen“ je **Frage**."""
    items = []
    chapters = re.split(r'^## ', md, flags=re.M)[1:]
    for ch in chapters:
        head, _, body = ch.partition('\n')
        num = re.match(r'(\d+)\.', head)
        n = num.group(1) if num else str(len(items))
        title = re.sub(r'^\d+\.\s*', '', head).strip()
        parts = re.split(r'^### ', body, flags=re.M)
        intro = parts[0]
        if title.lower() in ('häufige fragen', 'frequently asked questions', 'faq'):
            for k, qa in enumerate(re.findall(r'^\*\*(.+?)\*\*\s*\n(.+?)(?=^\*\*|\Z)', intro, flags=re.M | re.S)):
                q = qa[0].strip()
                items.append({'id': f'handbuch-{n}-{k}', 'title': q, 'body': clean(qa[1])})
            continue
        if len(parts) == 1 or len(clean(intro)) > 120:
            items.append({'id': f'handbuch-{n}', 'title': title, 'body': clean(intro if len(parts) > 1 else body)})
        for k, sub in enumerate(parts[1:]):
            sh, _, sb = sub.partition('\n')
            items.append({'id': f'handbuch-{n}-{k + 1}', 'title': f'{title}: {sh.strip()}', 'body': clean(sb)})
    for it in items:
        it['lang'] = lang
    return items


def topic(name, lang):
    md = read(name)
    m = re.search(r'^# (.+)$', md, flags=re.M)
    title = m.group(1).strip() if m else name
    title = re.sub(r'\s*\((Konzept|Machbarkeitsprobe|concept|feasibility trial)\)\s*$', '', title, flags=re.I)
    body = md[m.end():] if m else md
    return {'id': 'thema-' + name.replace('.de.md', '').replace('.md', ''), 'title': title, 'body': clean(body), 'lang': lang}


def main():
    de = handbook(read('handbuch.md'), 'de')
    en = handbook(read('manual.md'), 'en')
    if [a['id'] for a in de] != [a['id'] for a in en]:
        raise SystemExit('Handbuch und Manual sind unterschiedlich gegliedert - bitte angleichen.')
    for f in sorted(os.listdir(DOCS)):
        if f.endswith('.de.md') and f not in SKIP:
            other = f.replace('.de.md', '.md')
            if not os.path.exists(os.path.join(DOCS, other)):
                continue
            de.append(topic(f, 'de'))
            en.append(dict(topic(other, 'en'), id='thema-' + f.replace('.de.md', '')))
    out = []
    for a in de + en:
        if re.match(r'(Diese Seite ist umgezogen|This page has moved)', a['body']):   # Weiterleitungsseiten
            continue
        if len(a['body']) < 40:
            continue
        a['keys'] = keys(a['title'])
        out.append(a)
    io.open(OUT, 'w', encoding='utf-8', newline='\n').write(json.dumps(out, ensure_ascii=False, indent=1) + '\n')
    print(f"{sum(a['lang'] == 'de' for a in out)} deutsch, {sum(a['lang'] == 'en' for a in out)} englisch -> {os.path.relpath(OUT, ROOT)}")


if __name__ == '__main__':
    main()

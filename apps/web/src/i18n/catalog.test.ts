import { describe, expect, it } from 'vitest';
import { de } from './de';
import { en } from './en';
import { normalizeLocale } from './index';

function flatten(value: object, prefix = ''): Map<string, string> {
  const entries = new Map<string, string>();
  for (const [key, child] of Object.entries(value)) {
    const path = prefix ? `${prefix}.${key}` : key;
    if (typeof child === 'string') entries.set(path, child);
    else entriesFor(entries, flatten(child, path));
  }
  return entries;
}

function entriesFor(target: Map<string, string>, source: Map<string, string>) {
  source.forEach((value, key) => target.set(key, value));
}

function placeholders(value: string): string[] {
  return [...value.matchAll(/{{\s*([\w.]+)\s*}}/g)].map((match) => match[1]!).sort();
}

describe('translation catalogs', () => {
  it('keeps English and German keys and placeholders in exact parity', () => {
    const english = flatten(en);
    const german = flatten(de);
    expect([...german.keys()].sort()).toEqual([...english.keys()].sort());
    for (const [key, value] of english) {
      expect(german.get(key)?.trim(), `${key} must be translated`).not.toBe('');
      expect(placeholders(german.get(key)!), `${key} placeholders`).toEqual(placeholders(value));
    }
  });

  it('falls back to English for unsupported locales', () => {
    expect(normalizeLocale('de-DE')).toBe('de');
    expect(normalizeLocale('fr-FR')).toBe('en');
  });
});

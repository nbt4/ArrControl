import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import { de } from './de';
import { en } from './en';

export const supportedLocales = ['en', 'de'] as const;
export type SupportedLocale = (typeof supportedLocales)[number];
export const defaultLocale: SupportedLocale = 'en';
export const localeStorageKey = 'arrcontrol.locale';
export const timeZoneStorageKey = 'arrcontrol.timeZone';

export function normalizeLocale(value: string | null | undefined): SupportedLocale {
  const language = value?.split('-')[0]?.toLowerCase();
  return supportedLocales.includes(language as SupportedLocale) ? language as SupportedLocale : defaultLocale;
}

export function browserTimeZone(): string {
  try { return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC'; }
  catch { return 'UTC'; }
}

const storedLocale = typeof localStorage === 'undefined' ? null : localStorage.getItem(localeStorageKey);
const initialLocale = normalizeLocale(storedLocale ?? (typeof navigator === 'undefined' ? null : navigator.language));

void i18n.use(initReactI18next).init({
  resources: { en: { translation: en }, de: { translation: de } },
  lng: initialLocale,
  fallbackLng: defaultLocale,
  interpolation: { escapeValue: false },
  returnNull: false,
});
if (typeof document !== 'undefined') {
  document.documentElement.lang = initialLocale;
  i18n.on('languageChanged', (language) => {
    document.documentElement.lang = normalizeLocale(language);
  });
}

export default i18n;

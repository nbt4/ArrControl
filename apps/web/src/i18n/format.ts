export function formatDateTime(value: string, locale: string, timeZone: string): string {
  return new Intl.DateTimeFormat(locale, {
    dateStyle: 'medium', timeStyle: 'medium', timeZone,
  }).format(new Date(value));
}

export function availableTimeZones(current: string): readonly string[] {
  const extendedIntl = Intl as typeof Intl & { supportedValuesOf?: (key: 'timeZone') => string[] };
  const values = extendedIntl.supportedValuesOf?.('timeZone') ?? ['UTC'];
  return [...new Set(['UTC', current, ...values])].sort((left, right) => left.localeCompare(right));
}

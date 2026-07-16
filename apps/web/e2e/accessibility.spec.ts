import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page, type Route } from '@playwright/test';

const status = { name: 'ArrControl', status: 'ok', version: '1.0.0', utc: '2026-07-16T18:00:00Z' };

async function json(route: Route, body: unknown, statusCode = 200) {
  await route.fulfill({ status: statusCode, contentType: 'application/json', body: JSON.stringify(body) });
}

async function anonymousApi(page: Page) {
  await page.route('**/api/v1/system/status', (route) => json(route, status));
  await page.route('**/api/v1/auth/me', (route) => json(route, { code: 'authentication_required' }, 401));
  await page.route('**/api/v1/auth/csrf', (route) => json(route, { token: 'a'.repeat(43) }));
}

test('anonymous dashboard is WCAG 2.2 AA clean and keyboard operable', async ({ page }) => {
  await anonymousApi(page);
  let loginSubmitted = false;
  await page.route('**/api/v1/auth/login', async (route) => {
    loginSubmitted = true;
    await json(route, { userId: crypto.randomUUID(), email: 'operator@example.invalid' });
  });
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'ArrControl overview' })).toBeVisible();

  await page.keyboard.press('Tab');
  await expect(page.getByRole('link', { name: 'Skip to main content' })).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page.locator('main')).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(page.getByLabel('Language')).toBeFocused();

  await page.getByLabel('Email').focus();
  await page.keyboard.type('operator@example.invalid');
  await page.keyboard.press('Tab');
  await page.keyboard.type('correct horse battery staple');
  await page.keyboard.press('Enter');
  await expect.poll(() => loginSubmitted).toBe(true);

  const results = await new AxeBuilder({ page })
    .withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa'])
    .analyze();
  expect(results.violations).toEqual([]);
});

test('authenticated incident and audit disclosures work without a pointer', async ({ page }) => {
  await page.route('**/api/v1/system/status', (route) => json(route, status));
  await page.route('**/api/v1/auth/me', (route) => json(route, {
    userId: crypto.randomUUID(), email: 'operator@example.invalid', authenticationMethod: 'local',
    locale: 'en', timeZone: 'UTC', permissions: [
      { code: 'instances.read', global: true, instanceGroupIds: [] },
      { code: 'tasks.execute', global: true, instanceGroupIds: [] },
      { code: 'audit.read', global: true, instanceGroupIds: [] },
    ],
  }));
  await page.route('**/api/v1/events/snapshot', (route) => json(route, {
    version: 1, cursor: 'origin', generatedAt: status.utc, resources: [],
  }));
  await page.route('**/api/v1/instances', (route) => json(route, []));
  await page.route('**/api/v1/health/incidents**', (route) => json(route, [{
    id: crypto.randomUUID(), instanceId: crypto.randomUUID(), instanceName: 'Sonarr', providerKind: 'sonarr',
    severity: 'warning', remediationUrl: null, firstSeenAt: status.utc, lastSeenAt: status.utc,
    resolvedAt: null, acknowledgedAt: null, acknowledgedByUserId: null, snoozedUntil: null,
    snoozedByUserId: null, stale: false, sources: [{ providerIssueId: 1, source: 'fixture.health',
      severity: 'warning', message: 'Fixture warning', remediationUrl: null,
      firstSeenAt: status.utc, lastSeenAt: status.utc, active: true }],
  }]));
  await page.route('**/api/v1/audit**', (route) => json(route, { items: [{
    id: crypto.randomUUID(), occurredAt: status.utc, actorUserId: null, actorType: 'system',
    actorIdentifier: 'system', action: 'fixture.action', scope: {}, correlationId: 'fixture',
    outcome: 'succeeded', summary: {}, ipAddress: null,
  }], nextCursor: null }));
  await page.goto('/');

  const sources = page.getByText('1 source(s)', { exact: true });
  await sources.focus();
  await page.keyboard.press('Enter');
  await expect(page.getByText('Fixture warning')).toBeVisible();
  const details = page.getByText('Redacted details', { exact: true });
  await details.focus();
  await page.keyboard.press('Enter');
  await expect(page.locator('.audit-row pre')).toBeVisible();

  const results = await new AxeBuilder({ page })
    .withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa'])
    .analyze();
  expect(results.violations).toEqual([]);
});

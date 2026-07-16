import { describe, expect, it } from 'vitest';
import type { InstanceSummary, SystemStatus } from './api/client';
import { buildDashboardMetrics } from './dashboard';
import i18n from './i18n';

const translate = i18n.getFixedT('en');

const status: SystemStatus = {
  name: 'ArrControl', status: 'ok', version: '0.1.0', utc: '2026-07-16T12:00:00Z',
};

const instance = (overrides: Partial<InstanceSummary> = {}): InstanceSummary => ({
  id: crypto.randomUUID(),
  name: 'Sonarr',
  kind: 'sonarr',
  baseUrl: 'https://sonarr.example.invalid/',
  enabled: true,
  instanceGroupId: null,
  tlsVerificationEnabled: true,
  allowPrivateNetworkAccess: false,
  credentialsConfigured: true,
  createdAt: '2026-07-16T12:00:00Z',
  updatedAt: '2026-07-16T12:00:00Z',
  ...overrides,
});

describe('buildDashboardMetrics', () => {
  it('derives every count from API instance data', () => {
    const metrics = buildDashboardMetrics(status, [
      instance(),
      instance({ enabled: false, credentialsConfigured: false, tlsVerificationEnabled: false }),
    ], translate);
    expect(metrics.map((metric) => metric.value)).toEqual(['Online', '2', '1', '1']);
    expect(metrics[1]?.hint).toBe('1 enabled');
    expect(metrics[3]?.tone).toBe('warning');
  });

  it('does not manufacture instance counts without permission', () => {
    const metrics = buildDashboardMetrics(status, null, translate);
    expect(metrics.slice(1).map((metric) => metric.value)).toEqual(['—', '—', '—']);
  });
});

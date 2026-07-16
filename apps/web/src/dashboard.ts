import type { InstanceSummary, SystemStatus } from './api/client';
import type { TFunction } from 'i18next';

export interface DashboardMetric {
  label: string;
  value: string;
  hint: string;
  tone: 'normal' | 'good' | 'warning';
}

export function buildDashboardMetrics(
  status: SystemStatus,
  instances: readonly InstanceSummary[] | null,
  translate: TFunction,
): readonly DashboardMetric[] {
  const configured = instances?.length ?? 0;
  const enabled = instances?.filter((instance) => instance.enabled).length ?? 0;
  const credentials = instances?.filter((instance) => instance.credentialsConfigured).length ?? 0;
  const tlsExceptions = instances?.filter((instance) => !instance.tlsVerificationEnabled).length ?? 0;
  return [
    {
      label: translate('dashboard.metric.controlPlane'),
      value: translate(status.status === 'ok' ? 'dashboard.metric.online' : 'dashboard.metric.degraded'),
      hint: translate('dashboard.metric.apiVersion', { version: status.version ?? 'development' }),
      tone: status.status === 'ok' ? 'good' : 'warning',
    },
    {
      label: translate('dashboard.metric.services'),
      value: instances === null ? '—' : String(configured),
      hint: instances === null
        ? translate('dashboard.metric.permissionRequired')
        : translate('dashboard.metric.enabledCount', { count: enabled }),
      tone: 'normal',
    },
    {
      label: translate('dashboard.metric.credentials'),
      value: instances === null ? '—' : String(credentials),
      hint: translate('dashboard.metric.encryptedKeys'),
      tone: 'normal',
    },
    {
      label: translate('dashboard.metric.tlsExceptions'),
      value: instances === null ? '—' : String(tlsExceptions),
      hint: translate(tlsExceptions === 0
        ? 'dashboard.metric.verificationEnabled'
        : 'dashboard.metric.reviewRecommended'),
      tone: tlsExceptions === 0 ? 'good' : 'warning',
    },
  ];
}

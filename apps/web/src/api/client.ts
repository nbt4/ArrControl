import createClient from 'openapi-fetch';
import type { components, paths } from './generated';

export const api = createClient<paths>({ baseUrl: '/api/v1', credentials: 'include' });

export type SystemStatus = components['schemas']['SystemStatus'];
export type CurrentAuthorization = components['schemas']['CurrentAuthorization'];
export type InstanceSummary = components['schemas']['InstanceSummary'];
export type UserPreferences = components['schemas']['UserPreferences'];
export type HealthIncident = components['schemas']['HealthIncident'];
export type AuditEvent = components['schemas']['AuditEvent'];

export interface DashboardSnapshot {
  status: SystemStatus;
  authorization: CurrentAuthorization | null;
  instances: readonly InstanceSummary[] | null;
  incidents: readonly HealthIncident[] | null;
  audit: readonly AuditEvent[] | null;
  liveCursor: string | null;
}

export async function loadDashboard(signal?: AbortSignal): Promise<DashboardSnapshot> {
  const statusResult = await api.GET('/system/status', { signal });
  if (!statusResult.data) throw new Error('system_status_unavailable');

  const authorizationResult = await api.GET('/auth/me', { signal });
  if (authorizationResult.response.status === 401) {
    return {
      status: statusResult.data, authorization: null, instances: null,
      incidents: null, audit: null, liveCursor: null,
    };
  }
  if (!authorizationResult.data) throw new Error('authorization_unavailable');

  const liveSnapshotResult = await api.GET('/events/snapshot', { signal });
  const liveCursor = liveSnapshotResult.data?.cursor ?? null;
  const canReadAudit = authorizationResult.data.permissions.some(
    (grant) => grant.code === 'audit.read' && grant.global,
  );
  const auditResult = canReadAudit
    ? await api.GET('/audit', { params: { query: { limit: 20 } }, signal })
    : null;
  if (canReadAudit && !auditResult?.data) throw new Error('audit_unavailable');

  const canReadInstances = authorizationResult.data.permissions.some(
    (grant) => grant.code === 'instances.read',
  );
  if (!canReadInstances) {
    return {
      status: statusResult.data, authorization: authorizationResult.data,
      instances: null, incidents: null, audit: auditResult?.data?.items ?? null, liveCursor,
    };
  }

  const [instanceResult, healthResult] = await Promise.all([
    api.GET('/instances', { signal }),
    api.GET('/health/incidents', { signal }),
  ]);
  if (!instanceResult.data || !healthResult.data) throw new Error('dashboard_data_unavailable');
  return {
    status: statusResult.data,
    authorization: authorizationResult.data,
    instances: instanceResult.data,
    incidents: healthResult.data,
    audit: auditResult?.data?.items ?? null,
    liveCursor,
  };
}

export async function exportDiagnostics(): Promise<Blob> {
  const token = await csrfToken();
  const response = await fetch('/api/v1/diagnostics/export', {
    method: 'POST',
    credentials: 'include',
    headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': token },
    body: JSON.stringify({ lookbackHours: 24, includeAudit: true }),
  });
  if (!response.ok) throw new Error('diagnostics_export_failed');
  return response.blob();
}

async function csrfToken(): Promise<string> {
  const result = await api.GET('/auth/csrf');
  if (!result.data) throw new Error('csrf_unavailable');
  return result.data.token;
}

export async function setHealthAcknowledgement(
  incidentId: string,
  acknowledged: boolean,
): Promise<HealthIncident> {
  const token = await csrfToken();
  const result = await api.PUT('/health/incidents/{incidentId}/acknowledgement', {
    params: { path: { incidentId }, header: { 'X-CSRF-Token': token } },
    body: { acknowledged },
  });
  if (!result.data) throw new Error('health_acknowledgement_failed');
  return result.data;
}

export async function setHealthSnooze(
  incidentId: string,
  snoozedUntil: string | null,
): Promise<HealthIncident> {
  const token = await csrfToken();
  const result = await api.PUT('/health/incidents/{incidentId}/snooze', {
    params: { path: { incidentId }, header: { 'X-CSRF-Token': token } },
    body: { snoozedUntil },
  });
  if (!result.data) throw new Error('health_snooze_failed');
  return result.data;
}

export async function login(email: string, password: string): Promise<void> {
  const csrfResult = await api.GET('/auth/csrf');
  if (!csrfResult.data) throw new Error('csrf_unavailable');
  const result = await api.POST('/auth/login', {
    params: { header: { 'X-CSRF-Token': csrfResult.data.token } },
    body: { email, password },
  });
  if (!result.data) throw new Error('login_failed');
}

export async function logout(): Promise<void> {
  const csrfResult = await api.GET('/auth/csrf');
  if (!csrfResult.data) throw new Error('csrf_unavailable');
  const result = await api.POST('/auth/logout', {
    params: { header: { 'X-CSRF-Token': csrfResult.data.token } },
  });
  if (!result.response.ok) throw new Error('logout_failed');
}

export async function updatePreferences(locale: 'en' | 'de', timeZone: string): Promise<UserPreferences> {
  const csrfResult = await api.GET('/auth/csrf');
  if (!csrfResult.data) throw new Error('csrf_unavailable');
  const result = await api.PUT('/auth/preferences', {
    params: { header: { 'X-CSRF-Token': csrfResult.data.token } },
    body: { locale, timeZone },
  });
  if (!result.data) throw new Error('preference_update_failed');
  return result.data;
}

import createClient from 'openapi-fetch';
import type { components, paths } from './generated';

export const api = createClient<paths>({ baseUrl: '/api/v1', credentials: 'include' });

export type SystemStatus = components['schemas']['SystemStatus'];
export type CurrentAuthorization = components['schemas']['CurrentAuthorization'];
export type InstanceSummary = components['schemas']['InstanceSummary'];
export type InstanceKind = components['schemas']['InstanceKind'];
export type UserPreferences = components['schemas']['UserPreferences'];
export type HealthIncident = components['schemas']['HealthIncident'];
export type AuditEvent = components['schemas']['AuditEvent'];
export type MissingPage = components['schemas']['MissingPage'];
export type SearchRequest = components['schemas']['SearchRequest'];
export type SearchScopePreview = components['schemas']['SearchScopePreview'];
export type QueueItem = components['schemas']['AggregatedQueueItem'];
export type HistoryItem = components['schemas']['AggregatedHistoryItem'];
export type AutomationJobSchedule = components['schemas']['AutomationJobSchedule'];

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

export async function createInstance(request: {
  name: string;
  kind: InstanceKind;
  baseUrl: string;
  allowPrivateNetworkAccess: boolean;
  tlsVerificationEnabled: boolean;
}): Promise<components['schemas']['InstanceDetails']> {
  const token = await csrfToken();
  const result = await api.POST('/instances', {
    params: { header: { 'X-CSRF-Token': token } },
    body: { ...request, enabled: true, instanceGroupId: null },
  });
  if (!result.data) throw new Error('instance_create_failed');
  return result.data;
}

export async function updateInstance(request: {
  id: string;
  name: string;
  kind: InstanceKind;
  baseUrl: string;
  enabled: boolean;
  instanceGroupId: string | null;
  allowPrivateNetworkAccess: boolean;
  tlsVerificationEnabled: boolean;
}): Promise<void> {
  const token = await csrfToken();
  const { id, ...body } = request;
  const result = await api.PUT('/instances/{instanceId}', {
    params: { path: { instanceId: id }, header: { 'X-CSRF-Token': token } },
    body,
  });
  if (!result.data) throw new Error('instance_update_failed');
}

export async function deleteInstance(instanceId: string): Promise<void> {
  const token = await csrfToken();
  const result = await api.DELETE('/instances/{instanceId}', {
    params: { path: { instanceId }, header: { 'X-CSRF-Token': token } },
  });
  if (!result.response.ok) throw new Error('instance_delete_failed');
}

export async function putApiKey(instanceId: string, secret: string): Promise<void> {
  const token = await csrfToken();
  const result = await api.PUT('/instances/{instanceId}/credentials/{purpose}', {
    params: { path: { instanceId, purpose: 'api-key' }, header: { 'X-CSRF-Token': token } },
    body: { secret },
  });
  if (!result.data) throw new Error('credential_save_failed');
}

export async function probeInstance(instanceId: string): Promise<components['schemas']['ConnectionProbe']> {
  const token = await csrfToken();
  const result = await api.POST('/instances/{instanceId}/probe', {
    params: { path: { instanceId }, header: { 'X-CSRF-Token': token } },
  });
  if (!result.data) throw new Error('instance_probe_failed');
  return result.data;
}

export async function listMissing(filter: { search?: string; instanceIds?: readonly string[] } = {}): Promise<MissingPage> {
  const result = await api.GET('/missing', {
    params: {
      query: {
        limit: 100,
        ...(filter.search ? { search: filter.search } : {}),
        ...(filter.instanceIds?.length ? { instanceId: [...filter.instanceIds] } : {}),
      },
    },
  });
  if (!result.data) throw new Error('missing_unavailable');
  return result.data;
}

export async function previewSearch(request: SearchRequest): Promise<SearchScopePreview> {
  const result = await api.POST('/operations/search/preview', { body: request });
  if (!result.data) throw new Error('search_preview_failed');
  return result.data;
}

export async function startSearch(request: SearchRequest): Promise<void> {
  const token = await csrfToken();
  const result = await api.POST('/operations/search', {
    params: { header: { 'X-CSRF-Token': token, 'Idempotency-Key': crypto.randomUUID() } },
    body: request,
  });
  if (!result.data) throw new Error('search_start_failed');
}

export async function listQueue(): Promise<readonly QueueItem[]> {
  const result = await api.GET('/queue');
  if (!result.data) throw new Error('queue_unavailable');
  return result.data;
}

export async function listHistory(): Promise<readonly HistoryItem[]> {
  const result = await api.GET('/history', { params: { query: { limit: 100 } } });
  if (!result.data) throw new Error('history_unavailable');
  return result.data;
}

export async function listAutomationJobs(): Promise<readonly AutomationJobSchedule[]> {
  const result = await api.GET('/automation/jobs');
  if (!result.data) throw new Error('automation_jobs_unavailable');
  return result.data;
}

export async function startAutomationJob(scheduleId: string): Promise<void> {
  const token = await csrfToken();
  const idempotencyKey = crypto.randomUUID();
  const result = await api.POST('/automation/jobs/{scheduleId}/start', {
    params: { path: { scheduleId }, header: { 'X-CSRF-Token': token, 'Idempotency-Key': idempotencyKey } },
  });
  if (!result.data) throw new Error('automation_job_start_failed');
}

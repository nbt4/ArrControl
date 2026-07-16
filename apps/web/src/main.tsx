import React, { FormEvent, useCallback, useEffect, useMemo, useState } from 'react';
import { createRoot } from 'react-dom/client';
import {
  Activity, AlertTriangle, CheckCircle2, Database, FileDown, KeyRound, Languages, LoaderCircle,
  LockKeyhole, LogIn, LogOut, RefreshCw, Server, ShieldCheck, ShieldOff,
} from 'lucide-react';
import { useTranslation } from 'react-i18next';
import type { AuditEvent, DashboardSnapshot, HealthIncident, HistoryItem, InstanceKind, InstanceSummary, MissingPage, QueueItem } from './api/client';
import {
  createInstance, deleteInstance, exportDiagnostics, listHistory, listMissing, listQueue, loadDashboard, login, logout, probeInstance, putApiKey, setHealthAcknowledgement, setHealthSnooze, updateInstance,
  updatePreferences,
} from './api/client';
import { buildDashboardMetrics } from './dashboard';
import i18n, {
  browserTimeZone, localeStorageKey, normalizeLocale, timeZoneStorageKey, type SupportedLocale,
} from './i18n';
import { availableTimeZones, formatDateTime } from './i18n/format';
import './styles.css';

type ViewState =
  | { kind: 'loading' }
  | { kind: 'ready'; snapshot: DashboardSnapshot }
  | { kind: 'error' };

type Page = 'overview' | 'missing' | 'queue' | 'history' | 'health' | 'audit' | 'settings';

function pageFromHash(): Page {
  const value = window.location.hash.slice(1);
  return ['missing', 'queue', 'history', 'health', 'audit', 'settings'].includes(value) ? value as Page : 'overview';
}

const metricIcons = [Activity, Server, KeyRound, ShieldCheck] as const;

function readStoredTimeZone(): string {
  try { return localStorage.getItem(timeZoneStorageKey) || browserTimeZone(); }
  catch { return browserTimeZone(); }
}

function App() {
  const { t } = useTranslation();
  const [view, setView] = useState<ViewState>({ kind: 'loading' });
  const [refreshKey, setRefreshKey] = useState(0);
  const [page, setPage] = useState<Page>(pageFromHash);
  const [localTimeZone, setLocalTimeZone] = useState(readStoredTimeZone);
  const refresh = useCallback(() => {
    setView({ kind: 'loading' });
    setRefreshKey((value) => value + 1);
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    loadDashboard(controller.signal)
      .then((snapshot) => {
        if (snapshot.authorization) {
          const locale = normalizeLocale(snapshot.authorization.locale);
          void i18n.changeLanguage(locale);
          localStorage.setItem(localeStorageKey, locale);
          localStorage.setItem(timeZoneStorageKey, snapshot.authorization.timeZone);
          setLocalTimeZone(snapshot.authorization.timeZone);
        }
        setView({ kind: 'ready', snapshot });
      })
      .catch((error: unknown) => {
        if (!(error instanceof DOMException && error.name === 'AbortError')) setView({ kind: 'error' });
      });
    return () => controller.abort();
  }, [refreshKey]);

  useEffect(() => {
    const onHashChange = () => setPage(pageFromHash());
    window.addEventListener('hashchange', onHashChange);
    return () => window.removeEventListener('hashchange', onHashChange);
  }, []);

  const liveUserId = view.kind === 'ready' ? view.snapshot.authorization?.userId : null;
  const liveCursor = view.kind === 'ready' ? view.snapshot.liveCursor : null;
  useEffect(() => {
    if (!liveUserId || !liveCursor) return undefined;
    const source = new EventSource(`/api/v1/events?cursor=${encodeURIComponent(liveCursor)}`, {
      withCredentials: true,
    });
    let pendingRefresh: number | undefined;
    const scheduleRefresh = () => {
      if (pendingRefresh !== undefined) return;
      pendingRefresh = window.setTimeout(refresh, 250);
    };
    source.addEventListener('changed', scheduleRefresh);
    source.addEventListener('snapshot-required', scheduleRefresh);
    return () => {
      source.close();
      if (pendingRefresh !== undefined) window.clearTimeout(pendingRefresh);
    };
  }, [liveCursor, liveUserId, refresh]);

  const locale = normalizeLocale(i18n.resolvedLanguage);
  const authorization = view.kind === 'ready' ? view.snapshot.authorization : null;
  const savePreferences = async (nextLocale: SupportedLocale, timeZone: string) => {
    await i18n.changeLanguage(nextLocale);
    localStorage.setItem(localeStorageKey, nextLocale);
    localStorage.setItem(timeZoneStorageKey, timeZone);
    setLocalTimeZone(timeZone);
    if (authorization) {
      await updatePreferences(nextLocale, timeZone);
      refresh();
    }
  };

  return (
    <>
      <a className="skip-link" href="#overview">{t('app.skipToContent')}</a>
      <div className="shell">
      <aside>
        <div className="brand"><span>AC</span><b>{t('app.brand')}</b></div>
        <nav aria-label={t('app.navigation.label')}>
          <a className={page === 'overview' ? 'active' : ''} href="#overview">{t('app.navigation.overview')}</a>
          <a className={page === 'missing' ? 'active' : ''} href="#missing">{t('app.navigation.missing')}</a>
          <a className={page === 'queue' ? 'active' : ''} href="#queue">{t('app.navigation.queue')}</a>
          <a className={page === 'history' ? 'active' : ''} href="#history">{t('app.navigation.history')}</a>
          <a className={page === 'health' ? 'active' : ''} href="#health">{t('app.navigation.health')}</a>
          <a className={page === 'audit' ? 'active' : ''} href="#audit">{t('app.navigation.audit')}</a>
          <a className={page === 'settings' ? 'active' : ''} href="#settings">{t('app.navigation.settings')}</a>
        </nav>
        <div className="api-badge"><Database size={16} />{t('app.liveApi')}</div>
      </aside>
      <main id="overview" tabIndex={-1}>
        <header>
          <div>
            <p className="eyebrow">{t('dashboard.eyebrow')}</p>
            <h1>{t('dashboard.title')}</h1>
            <p className="muted">{t('dashboard.subtitle')}</p>
          </div>
          <div className="header-actions">
            <PreferenceControls
              key={`${locale}:${authorization?.timeZone ?? localTimeZone}`}
              locale={locale}
              timeZone={authorization?.timeZone ?? localTimeZone}
              onSave={savePreferences}
            />
            <button className="secondary" onClick={refresh} type="button"><RefreshCw size={17} />{t('dashboard.refresh')}</button>
          </div>
        </header>
        {view.kind === 'loading' && <LoadingState />}
        {view.kind === 'error' && <ErrorState retry={refresh} />}
        {view.kind === 'ready' && <PageContent page={page} refresh={refresh} snapshot={view.snapshot} timeZone={localTimeZone} />}
      </main>
      </div>
    </>
  );
}

function PageContent({ page, refresh, snapshot, timeZone }: {
  page: Page; refresh: () => void; snapshot: DashboardSnapshot; timeZone: string;
}) {
  if (page === 'missing') return <MissingScreen authorized={snapshot.authorization !== null} />;
  if (page === 'queue') return <QueueScreen authorized={snapshot.authorization !== null} />;
  if (page === 'history') return <HistoryScreen authorized={snapshot.authorization !== null} timeZone={timeZone} />;
  if (page === 'health') return snapshot.authorization && snapshot.incidents
    ? <HealthPanel canManage={snapshot.authorization.permissions.some((grant) => grant.code === 'tasks.execute')} incidents={snapshot.incidents} onChanged={refresh} timeZone={timeZone} />
    : <LoginPanel onSuccess={refresh} />;
  if (page === 'audit') return snapshot.authorization && snapshot.audit
    ? <AuditPanel events={snapshot.audit} timeZone={timeZone} />
    : <LoginPanel onSuccess={refresh} />;
  if (page === 'settings') return snapshot.authorization
    ? <AuthenticatedPanel canManage={snapshot.authorization.permissions.some((grant) => grant.code === 'instances.manage')} email={snapshot.authorization.email} instances={snapshot.instances} onChanged={refresh} onLogout={refresh} />
    : <LoginPanel onSuccess={refresh} />;
  return <Dashboard snapshot={snapshot} refresh={refresh} timeZone={timeZone} />;
}

function PreferenceControls({ locale, timeZone, onSave }: {
  locale: SupportedLocale; timeZone: string;
  onSave: (locale: SupportedLocale, timeZone: string) => Promise<void>;
}) {
  const { t } = useTranslation();
  const [draftLocale, setDraftLocale] = useState(locale);
  const [draftTimeZone, setDraftTimeZone] = useState(timeZone);
  const [state, setState] = useState<'idle' | 'saving' | 'saved' | 'failed'>('idle');
  const zones = useMemo(() => availableTimeZones(draftTimeZone), [draftTimeZone]);
  const save = async () => {
    setState('saving');
    try { await onSave(draftLocale, draftTimeZone); setState('saved'); }
    catch { setState('failed'); }
  };
  return (
    <div className="preferences">
      <Languages size={18} aria-hidden="true" />
      <label>{t('preferences.language')}
        <select value={draftLocale} onChange={(event) => { setDraftLocale(normalizeLocale(event.target.value)); setState('idle'); }}>
          <option value="en">{t('preferences.locale.en')}</option><option value="de">{t('preferences.locale.de')}</option>
        </select>
      </label>
      <label>{t('preferences.timezone')}
        <select value={draftTimeZone} onChange={(event) => { setDraftTimeZone(event.target.value); setState('idle'); }}>
          {zones.map((zone) => <option key={zone} value={zone}>{zone}</option>)}
        </select>
      </label>
      <button disabled={state === 'saving'} onClick={save} type="button">
        {state === 'saving' ? <LoaderCircle className="spin" size={16} /> : null}
        {t(state === 'saving' ? 'preferences.saving' : 'preferences.save')}
      </button>
      {state === 'saved' && <span className="save-status good" role="status">{t('preferences.saved')}</span>}
      {state === 'failed' && <span className="save-status failed" role="alert">{t('preferences.failed')}</span>}
    </div>
  );
}

function MissingScreen({ authorized }: { authorized: boolean }) {
  const { t } = useTranslation();
  const [search, setSearch] = useState('');
  const [data, setData] = useState<MissingPage | null>(null);
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    if (!authorized) return;
    const controller = new AbortController();
    listMissing(search)
      .then((response) => {
        if (controller.signal.aborted) return;
        setData(response);
        setFailed(false);
      })
      .catch(() => { if (!controller.signal.aborted) setFailed(true); });
    return () => controller.abort();
  }, [authorized, search]);
  if (!authorized) return <LoginPanel onSuccess={() => window.location.reload()} />;
  return <section className="workspace" id="missing"><div className="panel-heading"><div><p className="eyebrow">{t('missing.eyebrow')}</p><h2>{t('missing.title')}</h2></div><input aria-label={t('missing.search')} onChange={(event) => setSearch(event.target.value)} placeholder={t('missing.search')} value={search} /></div>
    {failed ? <p className="form-error">{t('missing.failed')}</p> : data === null ? <LoadingState /> : data.items.length === 0 ? <Notice icon={CheckCircle2} title={t('missing.emptyTitle')}>{t('missing.emptyBody')}</Notice> : <div className="data-list">{data.items.map((item) => <article className="data-row" key={item.id}><div><strong>{item.title}</strong><p>{item.instanceName} · {item.kind} · {item.reason}</p></div><div className="service-flags"><StatusPill good={!item.stale} label={item.stale ? t('missing.stale') : t('missing.fresh')} /></div></article>)}</div>}
  </section>;
}

function QueueScreen({ authorized }: { authorized: boolean }) {
  const { t } = useTranslation();
  const [items, setItems] = useState<readonly QueueItem[] | null>(null);
  const [failed, setFailed] = useState(false);
  useEffect(() => { if (!authorized) return; listQueue().then(setItems).catch(() => setFailed(true)); }, [authorized]);
  if (!authorized) return <LoginPanel onSuccess={() => window.location.reload()} />;
  return <section className="workspace" id="queue"><div className="panel-heading"><div><p className="eyebrow">{t('queue.eyebrow')}</p><h2>{t('queue.title')}</h2></div></div>
    {failed ? <p className="form-error">{t('queue.failed')}</p> : items === null ? <LoadingState /> : items.length === 0 ? <Notice icon={CheckCircle2} title={t('queue.emptyTitle')}>{t('queue.emptyBody')}</Notice> : <div className="data-list">{items.map((item) => <article className="data-row" key={`${item.instanceId}:${item.providerKey}`}><div><strong>{item.title}</strong><p>{item.instanceName} · {item.status} · {item.protocol ?? t('queue.unknownProtocol')}</p></div><div className="service-flags"><StatusPill good={!item.stale} label={item.stale ? t('queue.stale') : t('queue.fresh')} /></div></article>)}</div>}
  </section>;
}

function HistoryScreen({ authorized, timeZone }: { authorized: boolean; timeZone: string }) {
  const { t, i18n: translation } = useTranslation();
  const [items, setItems] = useState<readonly HistoryItem[] | null>(null);
  const [failed, setFailed] = useState(false);
  useEffect(() => { if (!authorized) return; listHistory().then(setItems).catch(() => setFailed(true)); }, [authorized]);
  if (!authorized) return <LoginPanel onSuccess={() => window.location.reload()} />;
  return <section className="workspace" id="history"><div className="panel-heading"><div><p className="eyebrow">{t('history.eyebrow')}</p><h2>{t('history.title')}</h2></div></div>
    {failed ? <p className="form-error">{t('history.failed')}</p> : items === null ? <LoadingState /> : items.length === 0 ? <Notice icon={CheckCircle2} title={t('history.emptyTitle')}>{t('history.emptyBody')}</Notice> : <div className="data-list">{items.map((item) => <article className="data-row" key={`${item.instanceId}:${item.providerKey}:${item.eventAt}`}><div><strong>{item.title}</strong><p>{item.instanceName} · {item.eventType} · {formatDateTime(item.eventAt, translation.resolvedLanguage ?? 'en', timeZone)}</p></div><div className="service-flags"><StatusPill good={!item.stale} label={item.stale ? t('history.stale') : t('history.fresh')} /></div></article>)}</div>}
  </section>;
}

function Dashboard({ snapshot, refresh, timeZone }: {
  snapshot: DashboardSnapshot; refresh: () => void; timeZone: string;
}) {
  const { t, i18n: translation } = useTranslation();
  const metrics = buildDashboardMetrics(snapshot.status, snapshot.instances, t);
  return (
    <>
      <p className="observed">{t('dashboard.observedAt', { date: formatDateTime(snapshot.status.utc, translation.resolvedLanguage ?? 'en', timeZone) })}</p>
      <section className="grid" aria-label={t('dashboard.summaryLabel')}>
        {metrics.map((metric, index) => {
          const Icon = metricIcons[index] ?? Activity;
          return (
            <article className={`metric ${metric.tone}`} key={metric.label}>
              <div className="icon"><Icon size={20} /></div><p>{metric.label}</p>
              <strong>{metric.value}</strong><small>{metric.hint}</small>
            </article>
          );
        })}
      </section>
      {snapshot.authorization === null
        ? <LoginPanel onSuccess={refresh} />
        : <AuthenticatedPanel
            canManage={snapshot.authorization.permissions.some((grant) => grant.code === 'instances.manage')}
            email={snapshot.authorization.email}
            instances={snapshot.instances}
            onChanged={refresh}
            onLogout={refresh}
          />}
      {snapshot.authorization !== null && snapshot.incidents !== null && (
        <HealthPanel
          canManage={snapshot.authorization.permissions.some((grant) => grant.code === 'tasks.execute')}
          incidents={snapshot.incidents}
          onChanged={refresh}
          timeZone={timeZone}
        />
      )}
      {snapshot.authorization !== null && snapshot.audit !== null && (
        <AuditPanel events={snapshot.audit} timeZone={timeZone} />
      )}
    </>
  );
}

function AuditPanel({ events, timeZone }: { events: readonly AuditEvent[]; timeZone: string }) {
  const { t, i18n: translation } = useTranslation();
  const [exportState, setExportState] = useState<'idle' | 'busy' | 'failed'>('idle');
  const download = async () => {
    setExportState('busy');
    try {
      const blob = await exportDiagnostics();
      const href = URL.createObjectURL(blob);
      const anchor = document.createElement('a');
      anchor.href = href;
      anchor.download = `arrcontrol-diagnostics-${new Date().toISOString().slice(0, 10)}.zip`;
      anchor.click();
      URL.revokeObjectURL(href);
      setExportState('idle');
    } catch { setExportState('failed'); }
  };
  return (
    <section className="workspace audit-panel" id="audit" aria-labelledby="audit-title">
      <div className="panel-heading">
        <div><p className="eyebrow">{t('audit.eyebrow')}</p><h2 id="audit-title">{t('audit.title')}</h2></div>
        <button className="secondary" disabled={exportState === 'busy'} onClick={() => void download()} type="button">
          {exportState === 'busy' ? <LoaderCircle className="spin" size={17} /> : <FileDown size={17} />}
          {t('audit.export')}
        </button>
      </div>
      {exportState === 'failed' && <p className="form-error" role="alert">{t('audit.exportFailed')}</p>}
      {events.length === 0
        ? <Notice icon={CheckCircle2} title={t('audit.emptyTitle')}>{t('audit.emptyBody')}</Notice>
        : <div className="audit-list">{events.map((event) => (
          <article className="audit-row" key={event.id}>
            <div><strong>{event.action}</strong><span className={`pill ${event.outcome === 'failed' ? 'danger' : ''}`}>{event.outcome}</span></div>
            <p>{event.actorIdentifier} · {formatDateTime(event.occurredAt, translation.resolvedLanguage ?? 'en', timeZone)}</p>
            <details><summary>{t('audit.details')}</summary>
              <pre>{JSON.stringify({ scope: event.scope, summary: event.summary, correlationId: event.correlationId }, null, 2)}</pre>
            </details>
          </article>
        ))}</div>}
    </section>
  );
}

function HealthPanel({ incidents, canManage, onChanged, timeZone }: {
  incidents: readonly HealthIncident[]; canManage: boolean; onChanged: () => void; timeZone: string;
}) {
  const { t, i18n: translation } = useTranslation();
  const [busyId, setBusyId] = useState<string | null>(null);
  const [failedId, setFailedId] = useState<string | null>(null);
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 60_000);
    return () => window.clearInterval(timer);
  }, []);
  const update = async (incident: HealthIncident, kind: 'ack' | 'snooze') => {
    setBusyId(incident.id); setFailedId(null);
    try {
      if (kind === 'ack') await setHealthAcknowledgement(incident.id, incident.acknowledgedAt === null);
      else {
        const activeSnooze = incident.snoozedUntil !== null && Date.parse(incident.snoozedUntil) > now;
        await setHealthSnooze(
          incident.id,
          activeSnooze ? null : new Date(now + 24 * 60 * 60 * 1000).toISOString(),
        );
      }
      onChanged();
    } catch { setFailedId(incident.id); } finally { setBusyId(null); }
  };
  return (
    <section className="workspace health-panel" id="health" aria-labelledby="health-title">
      <div className="panel-heading">
        <div><p className="eyebrow">{t('health.eyebrow')}</p><h2 id="health-title">{t('health.title')}</h2></div>
        <span className="pill">{t('health.activeCount', { count: incidents.length })}</span>
      </div>
      {incidents.length === 0
        ? <Notice icon={CheckCircle2} title={t('health.emptyTitle')}>{t('health.emptyBody')}</Notice>
        : <div className="incident-list">{incidents.map((incident) => {
          const activeSnooze = incident.snoozedUntil !== null && Date.parse(incident.snoozedUntil) > now;
          return (
            <article className={`incident ${incident.severity}`} key={incident.id}>
              <div className="incident-heading">
                <div>
                  <span className={`pill ${incident.severity === 'error' ? 'danger' : 'warning'}`}>{t(`health.severity.${incident.severity}`)}</span>
                  <h3>{incident.instanceName}</h3>
                  <p>{t('health.lastSeen', { date: formatDateTime(incident.lastSeenAt, translation.resolvedLanguage ?? 'en', timeZone) })}</p>
                </div>
                <div className="incident-actions">
                  {incident.remediationUrl && <a className="button-link" href={incident.remediationUrl} rel="noreferrer" target="_blank">{t('health.remediation')}</a>}
                  {canManage && <button className="secondary" disabled={busyId === incident.id} onClick={() => void update(incident, 'ack')} type="button">
                    {t(incident.acknowledgedAt ? 'health.unacknowledge' : 'health.acknowledge')}
                  </button>}
                  {canManage && <button className="secondary" disabled={busyId === incident.id} onClick={() => void update(incident, 'snooze')} type="button">
                    {t(activeSnooze ? 'health.unsnooze' : 'health.snooze')}
                  </button>}
                </div>
              </div>
              {failedId === incident.id && <p className="form-error" role="alert">{t('health.actionFailed')}</p>}
              <details><summary>{t('health.sources', { count: incident.sources.length })}</summary>
                <div className="incident-sources">{incident.sources.map((source) => (
                  <div key={`${source.providerIssueId}:${source.source}`}>
                    <strong>{source.source}</strong><span>{source.severity}</span>
                    {source.message && <p>{source.message}</p>}
                    {source.remediationUrl && source.remediationUrl !== incident.remediationUrl
                      ? <a href={source.remediationUrl} rel="noreferrer" target="_blank">{t('health.remediation')}</a> : null}
                  </div>
                ))}</div>
              </details>
            </article>
          );
        })}</div>}
    </section>
  );
}

function AuthenticatedPanel({ canManage, email, instances, onChanged, onLogout }: {
  canManage: boolean; email: string; instances: readonly InstanceSummary[] | null;
  onChanged: () => void; onLogout: () => void;
}) {
  const { t } = useTranslation();
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);
  const signOut = async () => {
    setBusy(true); setFailed(false);
    try { await logout(); onLogout(); } catch { setFailed(true); } finally { setBusy(false); }
  };
  return (
    <section className="workspace">
      <div className="panel-heading">
        <div><p className="eyebrow">{t('auth.signedIn')}</p><h2>{email}</h2></div>
        <button className="secondary" disabled={busy} onClick={signOut} type="button"><LogOut size={17} />{t('auth.signOut')}</button>
      </div>
      {failed && <p className="form-error" role="alert">{t('auth.signOutFailed')}</p>}
      {instances === null
        ? <Notice icon={LockKeyhole} title={t('instance.accessMissingTitle')}>{t('instance.accessMissingBody')}</Notice>
        : instances.length === 0
          ? <Notice icon={Server} title={t('instance.emptyTitle')}>{t('instance.emptyBody')}</Notice>
          : <InstanceList canManage={canManage} instances={instances} onChanged={onChanged} />}
      {canManage && <CreateInstanceForm onCreated={onChanged} />}
    </section>
  );
}

const instanceKinds: readonly InstanceKind[] = [
  'sonarr', 'radarr', 'lidarr', 'readarr', 'whisparr', 'prowlarr', 'bazarr', 'sabnzbd', 'nzbget',
  'qbittorrent', 'transmission', 'deluge', 'plex', 'jellyfin', 'emby', 'overseerr', 'jellyseerr', 'ombi',
];

function CreateInstanceForm({ onCreated }: { onCreated: () => void }) {
  const { t } = useTranslation();
  const [name, setName] = useState('');
  const [kind, setKind] = useState<InstanceKind>('sonarr');
  const [baseUrl, setBaseUrl] = useState('');
  const [apiKey, setApiKey] = useState('');
  const [allowPrivateNetworkAccess, setAllowPrivateNetworkAccess] = useState(true);
  const [tlsVerificationEnabled, setTlsVerificationEnabled] = useState(true);
  const [state, setState] = useState<'idle' | 'saving' | 'failed'>('idle');
  const submit = async (event: FormEvent) => {
    event.preventDefault(); setState('saving');
    try {
      const instance = await createInstance({ name, kind, baseUrl, allowPrivateNetworkAccess, tlsVerificationEnabled });
      if (apiKey.trim()) await putApiKey(instance.id, apiKey.trim());
      setName(''); setBaseUrl(''); setApiKey(''); setState('idle'); onCreated();
    } catch { setState('failed'); }
  };
  return (
    <form className="instance-form" onSubmit={submit}>
      <div><p className="eyebrow">{t('instance.addEyebrow')}</p><h3>{t('instance.addTitle')}</h3><p className="muted">{t('instance.addBody')}</p></div>
      <label>{t('instance.name')}<input onChange={(event) => setName(event.target.value)} required value={name} /></label>
      <label>{t('instance.kind')}<select onChange={(event) => setKind(event.target.value as InstanceKind)} value={kind}>{instanceKinds.map((value) => <option key={value} value={value}>{value}</option>)}</select></label>
      <label>{t('instance.baseUrl')}<input onChange={(event) => setBaseUrl(event.target.value)} placeholder="https://sonarr.example" required type="url" value={baseUrl} /></label>
      <label>{t('instance.apiKey')}<input autoComplete="off" onChange={(event) => setApiKey(event.target.value)} type="password" value={apiKey} /></label>
      <label className="checkbox-label"><input checked={allowPrivateNetworkAccess} onChange={(event) => setAllowPrivateNetworkAccess(event.target.checked)} type="checkbox" />{t('instance.allowPrivateNetwork')}</label>
      <label className="checkbox-label"><input checked={tlsVerificationEnabled} onChange={(event) => setTlsVerificationEnabled(event.target.checked)} type="checkbox" />{t('instance.verifyTls')}</label>
      {state === 'failed' && <p className="form-error" role="alert">{t('instance.addFailed')}</p>}
      <button disabled={state === 'saving'} type="submit">{state === 'saving' ? <LoaderCircle className="spin" size={17} /> : <Server size={17} />}{t('instance.add')}</button>
    </form>
  );
}

function InstanceList({ canManage, instances, onChanged }: {
  canManage: boolean; instances: readonly InstanceSummary[]; onChanged: () => void;
}) {
  return (
    <div className="instance-list">
      {instances.map((instance) => (
        <InstanceRow canManage={canManage} instance={instance} key={instance.id} onChanged={onChanged} />
      ))}
    </div>
  );
}

function InstanceRow({ canManage, instance, onChanged }: {
  canManage: boolean; instance: InstanceSummary; onChanged: () => void;
}) {
  const { t } = useTranslation();
  const [state, setState] = useState<'idle' | 'probing' | 'connected' | 'failed'>('idle');
  const [probeOutcome, setProbeOutcome] = useState<string | null>(null);
  const [editing, setEditing] = useState(false);
  const [name, setName] = useState(instance.name);
  const [baseUrl, setBaseUrl] = useState(instance.baseUrl);
  const [apiKey, setApiKey] = useState('');
  const [allowPrivateNetworkAccess, setAllowPrivateNetworkAccess] = useState(instance.allowPrivateNetworkAccess);
  const [tlsVerificationEnabled, setTlsVerificationEnabled] = useState(instance.tlsVerificationEnabled);
  const [deleteConfirmation, setDeleteConfirmation] = useState('');
  const [saving, setSaving] = useState(false);
  const probe = async () => {
    setState('probing'); setProbeOutcome(null);
    try {
      const { connected, outcome } = await probeInstance(instance.id);
      setState(connected ? 'connected' : 'failed');
      setProbeOutcome(outcome);
      if (connected) onChanged();
    } catch { setState('failed'); setProbeOutcome('request_failed'); }
  };
  return (
        <article className="instance-row">
          <div className="service-mark"><Server size={20} /></div>
          <div className="service-copy"><div><h3>{instance.name}</h3><span>{instance.kind}</span></div><p>{new URL(instance.baseUrl).host}</p></div>
          <div className="service-flags">
            <StatusPill good={instance.enabled} label={t(instance.enabled ? 'instance.enabled' : 'instance.disabled')} />
            <StatusPill good={instance.credentialsConfigured} label={t(instance.credentialsConfigured ? 'instance.keyConfigured' : 'instance.keyMissing')} />
            <StatusPill good={instance.tlsVerificationEnabled} label={t(instance.tlsVerificationEnabled ? 'instance.tlsVerified' : 'instance.tlsBypass')} />
            {canManage && <button className="secondary instance-probe" disabled={state === 'probing'} onClick={probe} type="button">{state === 'probing' ? <LoaderCircle className="spin" size={14} /> : <RefreshCw size={14} />}{t('instance.probe')}</button>}
            {canManage && <button className="secondary instance-probe" onClick={() => setEditing(!editing)} type="button">{t('instance.edit')}</button>}
            {state === 'connected' && <span className="pill good">{t('instance.probeConnected')}</span>}
            {state === 'failed' && <span className="pill warning">{t('instance.probeFailed', { outcome: probeOutcome })}</span>}
          </div>
          {editing && <div className="instance-edit">
            <label>{t('instance.name')}<input onChange={(event) => setName(event.target.value)} value={name} /></label>
            <label>{t('instance.baseUrl')}<input onChange={(event) => setBaseUrl(event.target.value)} type="url" value={baseUrl} /></label>
            <label>{t('instance.apiKeyReplace')}<input autoComplete="off" onChange={(event) => setApiKey(event.target.value)} type="password" value={apiKey} /></label>
            <label className="checkbox-label"><input checked={allowPrivateNetworkAccess} onChange={(event) => setAllowPrivateNetworkAccess(event.target.checked)} type="checkbox" />{t('instance.allowPrivateNetwork')}</label>
            <label className="checkbox-label"><input checked={tlsVerificationEnabled} onChange={(event) => setTlsVerificationEnabled(event.target.checked)} type="checkbox" />{t('instance.verifyTls')}</label>
            {!tlsVerificationEnabled && <p className="form-error">{t('instance.tlsWarning')}</p>}
            <button className="secondary" disabled={saving} onClick={async () => { setSaving(true); try { await updateInstance({ id: instance.id, name, kind: instance.kind, baseUrl, enabled: instance.enabled, instanceGroupId: instance.instanceGroupId, allowPrivateNetworkAccess, tlsVerificationEnabled }); if (apiKey.trim()) await putApiKey(instance.id, apiKey.trim()); setApiKey(''); setEditing(false); onChanged(); } catch { setState('failed'); } finally { setSaving(false); } }} type="button">{t('instance.save')}</button>
            <div className="instance-delete"><strong>{t('instance.deleteTitle')}</strong><p>{t('instance.deleteBody', { name: instance.name })}</p><label>{t('instance.deleteConfirm')}<input onChange={(event) => setDeleteConfirmation(event.target.value)} value={deleteConfirmation} /></label><button disabled={deleteConfirmation !== instance.name || saving} onClick={async () => { setSaving(true); try { await deleteInstance(instance.id); onChanged(); } catch { setState('failed'); } finally { setSaving(false); } }} type="button">{t('instance.delete')}</button></div>
          </div>}
        </article>
  );
}

function StatusPill({ good, label }: { good: boolean; label: string }) {
  const Icon = good ? CheckCircle2 : ShieldOff;
  return <span className={good ? 'pill good' : 'pill warning'}><Icon size={14} />{label}</span>;
}

function LoginPanel({ onSuccess }: { onSuccess: () => void }) {
  const { t } = useTranslation();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);
  const submit = async (event: FormEvent) => {
    event.preventDefault(); setBusy(true); setFailed(false);
    try { await login(email, password); setPassword(''); onSuccess(); }
    catch { setPassword(''); setFailed(true); }
    finally { setBusy(false); }
  };
  return (
    <section className="login-panel">
      <div><p className="eyebrow">{t('auth.required')}</p><h2>{t('auth.title')}</h2><p className="muted">{t('auth.secureCookies')}</p></div>
      <form onSubmit={submit}>
        <label>{t('auth.email')}<input autoComplete="username" onChange={(event) => setEmail(event.target.value)} required type="email" value={email} /></label>
        <label>{t('auth.password')}<input autoComplete="current-password" onChange={(event) => setPassword(event.target.value)} required type="password" value={password} /></label>
        {failed && <p className="form-error" role="alert">{t('auth.failed')}</p>}
        <button disabled={busy} type="submit">{busy ? <LoaderCircle className="spin" size={17} /> : <LogIn size={17} />}{t('auth.signIn')}</button>
      </form>
    </section>
  );
}

function LoadingState() {
  const { t } = useTranslation();
  return <section className="state-card" aria-live="polite"><LoaderCircle className="spin" size={24} /><div><h2>{t('state.loadingTitle')}</h2><p>{t('state.loadingBody')}</p></div></section>;
}
function ErrorState({ retry }: { retry: () => void }) {
  const { t } = useTranslation();
  return <section className="state-card error" role="alert"><AlertTriangle size={24} /><div><h2>{t('state.errorTitle')}</h2><p>{t('state.errorBody')}</p><button onClick={retry} type="button">{t('state.retry')}</button></div></section>;
}
function Notice({ icon: Icon, title, children }: { icon: typeof Server; title: string; children: React.ReactNode }) {
  return <div className="notice"><Icon size={22} /><div><h3>{title}</h3><p>{children}</p></div></div>;
}

createRoot(document.getElementById('root')!).render(<React.StrictMode><App /></React.StrictMode>);

import React, { FormEvent, useCallback, useEffect, useMemo, useState } from 'react';
import { createRoot } from 'react-dom/client';
import {
  Activity, AlertTriangle, CheckCircle2, Database, FileDown, KeyRound, Languages, LoaderCircle,
  LockKeyhole, LogIn, LogOut, RefreshCw, Server, ShieldCheck, ShieldOff,
} from 'lucide-react';
import { useTranslation } from 'react-i18next';
import type { AuditEvent, DashboardSnapshot, HealthIncident, InstanceSummary } from './api/client';
import {
  exportDiagnostics, loadDashboard, login, logout, setHealthAcknowledgement, setHealthSnooze,
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

const metricIcons = [Activity, Server, KeyRound, ShieldCheck] as const;

function readStoredTimeZone(): string {
  try { return localStorage.getItem(timeZoneStorageKey) || browserTimeZone(); }
  catch { return browserTimeZone(); }
}

function App() {
  const { t } = useTranslation();
  const [view, setView] = useState<ViewState>({ kind: 'loading' });
  const [refreshKey, setRefreshKey] = useState(0);
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
          <a className="active" href="#overview">{t('app.navigation.overview')}</a>
          <span aria-disabled="true">{t('app.navigation.library')}</span>
          <span aria-disabled="true">{t('app.navigation.missing')}</span>
          <span aria-disabled="true">{t('app.navigation.queue')}</span>
          <a href="#health">{t('app.navigation.health')}</a>
          <a href="#audit">{t('app.navigation.audit')}</a>
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
        {view.kind === 'ready' && <Dashboard snapshot={view.snapshot} refresh={refresh} timeZone={localTimeZone} />}
      </main>
      </div>
    </>
  );
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
        : <AuthenticatedPanel email={snapshot.authorization.email} instances={snapshot.instances} onLogout={refresh} />}
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

function AuthenticatedPanel({ email, instances, onLogout }: {
  email: string; instances: readonly InstanceSummary[] | null; onLogout: () => void;
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
          : <InstanceList instances={instances} />}
    </section>
  );
}

function InstanceList({ instances }: { instances: readonly InstanceSummary[] }) {
  const { t } = useTranslation();
  return (
    <div className="instance-list">
      {instances.map((instance) => (
        <article className="instance-row" key={instance.id}>
          <div className="service-mark"><Server size={20} /></div>
          <div className="service-copy"><div><h3>{instance.name}</h3><span>{instance.kind}</span></div><p>{new URL(instance.baseUrl).host}</p></div>
          <div className="service-flags">
            <StatusPill good={instance.enabled} label={t(instance.enabled ? 'instance.enabled' : 'instance.disabled')} />
            <StatusPill good={instance.credentialsConfigured} label={t(instance.credentialsConfigured ? 'instance.keyConfigured' : 'instance.keyMissing')} />
            <StatusPill good={instance.tlsVerificationEnabled} label={t(instance.tlsVerificationEnabled ? 'instance.tlsVerified' : 'instance.tlsBypass')} />
          </div>
        </article>
      ))}
    </div>
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

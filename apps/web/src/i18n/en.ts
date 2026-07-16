export const en = {
  app: {
    brand: 'ArrControl',
    skipToContent: 'Skip to main content',
    navigation: {
      label: 'Primary navigation', overview: 'Overview', library: 'Library', missing: 'Missing',
      queue: 'Queue', health: 'Health', audit: 'Audit',
    },
    liveApi: 'Live API data',
  },
  preferences: {
    language: 'Language', timezone: 'Timezone', save: 'Save preferences', saving: 'Saving…',
    saved: 'Preferences saved.', failed: 'Preferences could not be saved.',
    locale: { en: 'English', de: 'Deutsch' },
  },
  dashboard: {
    eyebrow: 'OPERATIONS CENTER', title: 'ArrControl overview',
    subtitle: 'Current state from the local API, without cached demo values.',
    refresh: 'Refresh', observedAt: 'Observed {{date}}', summaryLabel: 'System summary',
    metric: {
      controlPlane: 'Control plane', online: 'Online', degraded: 'Degraded', apiVersion: 'API {{version}}',
      services: 'Services', permissionRequired: 'Permission required', enabledCount: '{{count}} enabled',
      credentials: 'Credentials', encryptedKeys: 'Encrypted provider keys', tlsExceptions: 'TLS exceptions',
      verificationEnabled: 'Verification enabled', reviewRecommended: 'Review recommended',
    },
  },
  auth: {
    signedIn: 'SIGNED IN', signOut: 'Sign out', signOutFailed: 'Sign-out failed. Try again.',
    required: 'AUTHENTICATION REQUIRED', title: 'Sign in to load your services',
    secureCookies: 'Secure session cookies require HTTPS, including local development.',
    email: 'Email', password: 'Password', signIn: 'Sign in',
    failed: 'Sign-in failed. Check the credentials and HTTPS setup.',
  },
  instance: {
    accessMissingTitle: 'Instance access is not assigned',
    accessMissingBody: 'Your account is active, but it has no instances.read grant.',
    emptyTitle: 'No services configured',
    emptyBody: 'Use the instance API or an administrator account to add the first provider.',
    enabled: 'Enabled', disabled: 'Disabled', keyConfigured: 'Key configured', keyMissing: 'Key missing',
    tlsVerified: 'TLS verified', tlsBypass: 'TLS bypass',
  },
  import: {
    guidance: {
      check_download_client: 'Check the configured download client and its connection.',
      review_upstream_import: 'Review the import details in the source provider.',
      wait_or_review_upstream: 'Wait for the pending import or review it in the source provider.',
      review_upstream: 'Review the diagnostic state in the source provider.',
      search_again: 'Review the failed download before starting another search.',
    },
  },
  health: {
    eyebrow: 'HEALTH', title: 'Active incidents', activeCount: '{{count}} active',
    emptyTitle: 'No active incidents', emptyBody: 'The latest provider health snapshots report no issues.',
    lastSeen: 'Last seen {{date}}', remediation: 'Open guidance', acknowledge: 'Acknowledge',
    unacknowledge: 'Unacknowledge', snooze: 'Snooze 24 hours', unsnooze: 'Clear snooze',
    sources: '{{count}} source(s)', actionFailed: 'The incident could not be updated.',
    severity: { ok: 'OK', notice: 'Notice', warning: 'Warning', error: 'Error', unknown: 'Unknown' },
  },
  audit: {
    eyebrow: 'AUDIT', title: 'Recent audit events', export: 'Export redacted diagnostics',
    exportFailed: 'The diagnostics archive could not be created.', details: 'Redacted details',
    emptyTitle: 'No recent audit events', emptyBody: 'No events match the default 30-day window.',
  },
  state: {
    loadingTitle: 'Loading current state', loadingBody: 'Contacting the ArrControl API…',
    errorTitle: 'API data is unavailable',
    errorBody: 'No placeholder values are shown. Check readiness and try again.', retry: 'Try again',
  },
} as const;

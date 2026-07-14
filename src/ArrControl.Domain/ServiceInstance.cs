namespace ArrControl.Domain;

public enum ServiceKind { Sonarr, Radarr, Lidarr, Readarr, Whisparr, Prowlarr, Bazarr, Sabnzbd, NzbGet, QBittorrent, Deluge, Transmission, Plex, Jellyfin, Emby }
public enum InstanceState { Unknown, Online, Degraded, Offline, Disabled }

public sealed record ServiceInstance(Guid Id, string Name, ServiceKind Kind, Uri BaseUri, bool Enabled, InstanceState State);

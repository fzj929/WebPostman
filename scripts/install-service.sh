#!/usr/bin/env bash
set -Eeuo pipefail

port="${1:-5078}"
listen_address="${2:-127.0.0.1}"
[[ "$port" =~ ^[0-9]+$ ]] && (( 10#$port >= 1 && 10#$port <= 65535 )) || {
  echo "Usage: $0 [port] [listen-address]" >&2
  exit 2
}
[[ "$listen_address" == '127.0.0.1' || "$listen_address" == '0.0.0.0' || "$listen_address" == 'localhost' ]] || {
  echo 'Listen address must be 127.0.0.1, 0.0.0.0, or localhost.' >&2
  exit 2
}

if (( EUID != 0 )); then
  echo 'Run this script as root (for example, with sudo).' >&2
  exit 1
fi

script_dir="$(cd -- "${BASH_SOURCE[0]%/*}" && pwd)"
publish_directory="$(cd -- "$script_dir/.." && pwd)"
application="$publish_directory/WebPostman.Api.dll"
data_directory="$publish_directory/App_Data"
service_name='webpostman.service'
service_user='webpostman'
unit_file="/etc/systemd/system/$service_name"
listen_url="http://$listen_address:$port"

[[ -f "$application" ]] || {
  echo "Published application not found: $application. Keep this script in the scripts directory of the published application." >&2
  exit 1
}
command -v systemctl >/dev/null || { echo 'systemd is required.' >&2; exit 1; }
if [[ -x "$publish_directory/WebPostman.Api" && -f "$publish_directory/libhostfxr.so" ]]; then
  exec_start="\"$publish_directory/WebPostman.Api\""
else
  command -v dotnet >/dev/null || { echo '.NET 8 runtime is required for a framework-dependent publish.' >&2; exit 1; }
  dotnet_path="$(command -v dotnet)"
  exec_start="\"$dotnet_path\" \"$application\""
fi

if systemctl list-unit-files "$service_name" --no-legend 2>/dev/null | grep -q "^$service_name"; then
  systemctl stop "$service_name"
fi
if ! id "$service_user" >/dev/null 2>&1; then
  useradd --system --home-dir "$data_directory" --shell /usr/sbin/nologin "$service_user"
fi
install -d -o "$service_user" -g "$service_user" -m 0750 "$data_directory"
chown -R "$service_user:$service_user" "$data_directory"

cat > "$unit_file" <<UNIT
[Unit]
Description=WebPostman API Workspace
After=network.target

[Service]
Type=notify
User=$service_user
Group=$service_user
WorkingDirectory=$publish_directory
ExecStart=$exec_start
Environment="WebPostman__DataDirectory=$data_directory"
Environment="WebPostman__ListenUrl=$listen_url"
Restart=on-failure
RestartSec=5
NoNewPrivileges=true
ProtectSystem=strict
ReadWritePaths="$data_directory"
PrivateTmp=true

[Install]
WantedBy=multi-user.target
UNIT

systemctl daemon-reload
systemctl enable --now "$service_name"
echo "WebPostman service installed and running at $listen_url"
echo "Application directory: $publish_directory"
echo "Data directory: $data_directory"

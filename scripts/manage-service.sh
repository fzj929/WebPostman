#!/usr/bin/env bash
set -Eeuo pipefail

usage() {
  echo "Usage: $0 {install|uninstall|start|stop|restart|status|run} [port]" >&2
  exit 2
}

action="${1:-}"
port="${2:-5078}"
[[ "$action" =~ ^(install|uninstall|start|stop|restart|status|run)$ ]] || usage
[[ "$port" =~ ^[0-9]+$ ]] && (( 10#$port >= 1 && 10#$port <= 65535 )) || usage

project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
web_project="$project_root/WebPostman.Web"
api_project="$project_root/WebPostman.Api/WebPostman.Api.csproj"
service_name='webpostman.service'
service_user='webpostman'
install_directory='/opt/webpostman'
data_directory='/var/lib/webpostman'
unit_file="/etc/systemd/system/$service_name"
listen_url="http://127.0.0.1:$port"

require_root() {
  if (( EUID != 0 )); then
    echo 'Run this command as root (for example, with sudo).' >&2
    exit 1
  fi
}

build_app() {
  if [[ ! -f "$web_project/package.json" ]]; then
    echo "Frontend project not found: $web_project/package.json" >&2
    exit 1
  fi
  if [[ -f "$web_project/package-lock.json" ]]; then
    npm --prefix "$web_project" ci
  else
    echo "Warning: package-lock.json was not found. Falling back to npm install: $web_project" >&2
    npm --prefix "$web_project" install
  fi
  npm --prefix "$web_project" run build
}

publish_app() {
  dotnet publish "$api_project" -c Release -o "$install_directory"
}

case "$action" in
  install)
    require_root
    command -v systemctl >/dev/null || { echo 'systemd is required.' >&2; exit 1; }
    command -v dotnet >/dev/null || { echo '.NET 8 SDK is required.' >&2; exit 1; }
    dotnet_path="$(command -v dotnet)"
    build_app
    if systemctl list-unit-files "$service_name" --no-legend 2>/dev/null | grep -q "^$service_name"; then
      systemctl stop "$service_name"
    fi
    install -d -m 0755 "$install_directory"
    publish_app
    if ! id "$service_user" >/dev/null 2>&1; then
      useradd --system --home-dir "$data_directory" --shell /usr/sbin/nologin "$service_user"
    fi
    install -d -o "$service_user" -g "$service_user" -m 0750 "$data_directory"
    cat > "$unit_file" <<UNIT
[Unit]
Description=WebPostman API Workspace
After=network.target

[Service]
Type=notify
User=$service_user
Group=$service_user
WorkingDirectory=$install_directory
ExecStart=$dotnet_path $install_directory/WebPostman.Api.dll
Environment=WebPostman__DataDirectory=$data_directory
Environment=WebPostman__ListenUrl=$listen_url
Restart=on-failure
RestartSec=5
NoNewPrivileges=true
ProtectSystem=strict
ReadWritePaths=$data_directory
PrivateTmp=true

[Install]
WantedBy=multi-user.target
UNIT
    systemctl daemon-reload
    systemctl enable --now "$service_name"
    echo "WebPostman is running at $listen_url"
    echo "Data directory: $data_directory"
    ;;
  uninstall)
    require_root
    if [[ -f "$unit_file" ]]; then
      systemctl disable --now "$service_name"
      rm -- "$unit_file"
      systemctl daemon-reload
      echo "Service $service_name removed. Published files and data were preserved."
    else
      echo "Service $service_name is not installed."
    fi
    ;;
  start|stop|restart)
    require_root
    systemctl "$action" "$service_name"
    systemctl show "$service_name" --no-pager -p ActiveState -p SubState -p UnitFileState
    ;;
  status)
    systemctl show "$service_name" --no-pager -p ActiveState -p SubState -p UnitFileState
    ;;
  run)
    build_app
    export WebPostman__ListenUrl="$listen_url"
    echo "Running WebPostman in the foreground at $listen_url (Ctrl+C to stop)."
    dotnet run --project "$api_project" --no-launch-profile
    ;;
esac

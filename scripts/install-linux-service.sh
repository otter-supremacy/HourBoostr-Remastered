#!/usr/bin/env bash
set -euo pipefail
if [ "$#" -ne 2 ]; then echo "Usage: $0 /absolute/release/path /absolute/data/path"; exit 2; fi
release=$(realpath "$1")
data=$(realpath -m "$2")
if [[ "$release" != /* || "$data" != /* || "$release" == *[[:space:]]* || "$data" == *[[:space:]]* ]]; then echo "Use absolute paths without whitespace."; exit 2; fi
if [ ! -f "$release/HourBoostr.Remastered" ]; then echo "Published Linux executable not found."; exit 2; fi
mkdir -p "$data" "$HOME/.config/systemd/user"
chmod 700 "$data"
chmod u+x "$release/HourBoostr.Remastered"
cat > "$HOME/.config/systemd/user/hourboostr-remastered.service" <<EOF
[Unit]
Description=HourBoostr Remastered
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
WorkingDirectory=$release
ExecStart=$release/HourBoostr.Remastered serve --data-dir $data
Restart=on-failure
RestartSec=30
UMask=0077
NoNewPrivileges=true
PrivateTmp=true

[Install]
WantedBy=default.target
EOF
systemctl --user daemon-reload
systemctl --user enable --now hourboostr-remastered.service
echo "Installed a user service. For startup before sign-in, an administrator must enable lingering: loginctl enable-linger $(id -un)"

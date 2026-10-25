#!/usr/bin/env bash
set -euo pipefail
unit="$HOME/.config/systemd/user/hourboostr-remastered.service"
systemctl --user disable --now hourboostr-remastered.service
if [ -f "$unit" ]; then rm -- "$unit"; fi
systemctl --user daemon-reload
echo "Removed the service. Application files and account data remain in place."

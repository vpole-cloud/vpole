#!/usr/bin/env bash
# Copyright (C) 2026 Victor Shnaider and vPole contributors
# SPDX-License-Identifier: AGPL-3.0-only

set -euo pipefail

if [ ! -f /tmp/mongodb-keyfile ]; then
  echo -n 'DummyKey' > /tmp/mongodb-keyfile
  chown mongodb:mongodb /tmp/mongodb-keyfile
  chmod 600 /tmp/mongodb-keyfile
fi

exec /usr/local/bin/docker-entrypoint.sh "$@"

#!/bin/bash
set -euo pipefail

if [ ! -f /tmp/mongodb-keyfile ]; then
  echo -n 'DummyKey' > /tmp/mongodb-keyfile
  chown mongodb:mongodb /tmp/mongodb-keyfile
  chmod 600 /tmp/mongodb-keyfile
fi

exec /usr/local/bin/docker-entrypoint.sh "$@"

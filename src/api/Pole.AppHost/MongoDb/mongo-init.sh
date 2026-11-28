#!/bin/bash
# Copyright (C) 2026 Victor Shnaider and vPole contributors
# SPDX-License-Identifier: AGPL-3.0-only

echo -n 'DummyKey' > /tmp/mongodb-keyfile && \
    chown -R mongodb:mongodb /tmp/mongodb-keyfile && \
    chmod 600 /tmp/mongodb-keyfile
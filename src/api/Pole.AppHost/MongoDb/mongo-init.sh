#!/bin/bash

echo -n 'DummyKey' > /tmp/mongodb-keyfile && \
    chown -R mongodb:mongodb /tmp/mongodb-keyfile && \
    chmod 600 /tmp/mongodb-keyfile
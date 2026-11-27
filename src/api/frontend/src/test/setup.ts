// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { notifyManager } from '@tanstack/react-query'

// React act drains microtasks; keep query delivery deterministic without real sleeps.
notifyManager.setScheduler(queueMicrotask)

import { notifyManager } from '@tanstack/react-query'

// React act drains microtasks; keep query delivery deterministic without real sleeps.
notifyManager.setScheduler(queueMicrotask)

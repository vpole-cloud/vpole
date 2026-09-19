#!/usr/bin/env python3
"""Run every Python automation test and reject skips as well as failures."""

import json
from pathlib import Path
import sys
import unittest

if __name__ == "__main__":
    suite = unittest.defaultTestLoader.discover(str(Path(__file__).parent / "tests"))
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    counts = {"total": result.testsRun, "failed": len(result.failures), "errors": len(result.errors), "skipped": len(result.skipped), "expected_failures": len(result.expectedFailures)}
    if len(sys.argv) > 1:
        Path(sys.argv[1]).write_text(json.dumps(counts) + "\n")
    raise SystemExit(0 if result.wasSuccessful() and result.testsRun > 0 and not result.skipped and not result.expectedFailures else 1)
